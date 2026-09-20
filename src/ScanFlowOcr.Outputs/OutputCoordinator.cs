using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScanFlowOcr.Contracts;
using Serilog;

namespace ScanFlowOcr.Outputs;

/// <summary>
/// Durable per-sink delivery queue (SQLite) with MQTT / TCP / keyboard sinks.
/// OCR-only payloads (textLines); no barcode fields.
/// </summary>
public sealed class OutputCoordinator : IRecordDelivery, IAsyncDisposable
{
    private const int Complete = 1, Uncertain = 2;
    private readonly string _dbPath;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, OutputRouteProfile> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _workers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _wakes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RouteCounts> _counts = new(StringComparer.Ordinal);
    private bool _initialized;
    private string? _lastError, _lastReceiptCode;

    private sealed class RouteCounts { public int Pending, Delivered, Uncertain; }
    private sealed record PendingItem(Guid EventId, string SinkId, byte[] Payload, string RouteJson, string RecordJson, int Attempt);

    public OutputCoordinator(string? dataDirectory = null)
    {
        var dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanFlowOcr");
        _dbPath = Path.Combine(dir, "outputs.db");
        _logger = new LoggerConfiguration().MinimumLevel.Information()
            .WriteTo.File(Path.Combine(dir, "logs", "scanflow-ocr-.log"),
                rollingInterval: RollingInterval.Day, rollOnFileSizeLimit: true,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                fileSizeLimitBytes: 5 * 1024 * 1024, retainedFileCountLimit: 14, shared: true)
            .CreateLogger();
    }

    public void Configure(bool enabled, MqttRoute route, int capacity) =>
        Configure(enabled
            ? [new OutputRouteProfile("mqtt", true, capacity, 1024 * 1024, JsonSerializer.SerializeToElement(route))]
            : []);

    public void Configure(ImmutableArray<OutputRouteProfile> routes)
    {
        if (routes.IsDefault) routes = [];
        if (routes.Length > 1)
            throw new ArgumentException("当前只能选择一种输出方式。", nameof(routes));
        if (routes.Select(x => x.SinkId).Distinct(StringComparer.Ordinal).Count() != routes.Length)
            throw new ArgumentException("输出通道 ID 重复。", nameof(routes));
        foreach (var profile in routes) ValidateRoute(profile);
        lock (_gate)
        {
            _routes.Clear();
            foreach (var profile in routes) _routes.Add(profile.SinkId, profile);
            if (routes.Length > 0)
            {
                EnsureDatabase();
                foreach (var profile in routes)
                {
                    if (!_wakes.ContainsKey(profile.SinkId)) _wakes.Add(profile.SinkId, new SemaphoreSlim(0, 1));
                    if (!_workers.ContainsKey(profile.SinkId))
                        _workers.Add(profile.SinkId, Task.Run(() => SenderLoop(profile.SinkId)));
                }
            }
            Log(routes.Length > 0 ? "output-enabled" : "output-disabled", null, null);
            SignalAll();
        }
    }

    private static void ValidateRoute(OutputRouteProfile profile)
    {
        if (profile.Capacity is < 1 or > 100000 || profile.MaxPayloadBytes is < 1 or > 1024 * 1024)
            throw new ArgumentException("输出容量或消息大小无效。");
        if (profile.ProviderOptions.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new ArgumentException("输出通道配置不能为空。");
        string? error = profile.SinkId switch
        {
            "mqtt" => profile.ProviderOptions.Deserialize<MqttRoute>()?.Validate()
                ?? (profile.ProviderOptions.ValueKind == JsonValueKind.Null ? "MQTT 配置无效。" : null),
            "tcp" => profile.ProviderOptions.Deserialize<TcpRoute>()?.Validate(),
            "keyboard" => profile.ProviderOptions.Deserialize<KeyboardRoute>()?.Validate(),
            _ => "不支持的输出通道。"
        };
        if (error is not null) throw new ArgumentException(error);
    }

    public OutputQueueStatus GetStatus()
    {
        lock (_gate)
        {
            var routeStatuses = _counts.Select(pair =>
            {
                _routes.TryGetValue(pair.Key, out var profile);
                return new OutputRouteStatus(pair.Key, profile is not null, pair.Value.Pending,
                    pair.Value.Delivered, pair.Value.Uncertain, profile?.Capacity ?? 0);
            }).ToList();
            foreach (var profile in _routes.Values)
                if (!routeStatuses.Any(x => x.SinkId == profile.SinkId))
                    routeStatuses.Add(new(profile.SinkId, true, 0, 0, 0, profile.Capacity));
            return new(_routes.Count > 0, routeStatuses.Sum(x => x.PendingCount),
                _routes.Values.Sum(x => x.Capacity),
                routeStatuses.Any(x => x.Enabled && x.PendingCount >= x.Capacity),
                _lastError, routeStatuses.Sum(x => x.DeliveredCount),
                routeStatuses.Sum(x => x.UncertainCount), _lastReceiptCode, [.. routeStatuses]);
        }
    }

    public ValueTask<bool> TryAcceptAsync(ScanRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_routes.Count == 0) return ValueTask.FromResult(true);
            EnsureDatabase();

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                eventId = record.EventId,
                createdUtc = record.CreatedUtc,
                sourceId = record.Frame.SourceId,
                textLines = record.TextLines.Select(l => new { l.Text, l.Bounds, l.Confidence, l.Language })
            });
            var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var profile in _routes.Values)
            {
                byte[] payload;
                if (profile.SinkId == "keyboard")
                {
                    var kbRoute = profile.ProviderOptions.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
                        ? profile.ProviderOptions.Deserialize<KeyboardRoute>()
                        : null;
                    var items = KeyboardText.CollectItems(record);
                    string sep = KeyboardRoute.UnescapeSeparator(kbRoute?.Separator);
                    payload = Encoding.UTF8.GetBytes(string.Join(sep, items));
                }
                else
                {
                    payload = json;
                }
                if (payload.Length > profile.MaxPayloadBytes)
                {
                    _lastError = profile.SinkId + " 输出消息超出上限。";
                    Log("admission-rejected-size", record.EventId, profile.SinkId);
                    return ValueTask.FromResult(false);
                }
                payloads.Add(profile.SinkId, payload);
            }

            try
            {
                using var db = Open();
                using var tx = db.BeginTransaction();
                foreach (var profile in _routes.Values)
                {
                    using var count = db.CreateCommand();
                    count.Transaction = tx;
                    count.CommandText = "SELECT count(*) FROM deliveries WHERE sink_id=$sink AND state IN (0,2)";
                    count.Parameters.AddWithValue("$sink", profile.SinkId);
                    int pending = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                    if (pending >= profile.Capacity)
                    {
                        _lastError = profile.SinkId + " 输出队列已满；新业务记录暂停接纳。";
                        return ValueTask.FromResult(false);
                    }
                }
                using (var insert = db.CreateCommand())
                {
                    insert.Transaction = tx;
                    insert.CommandText = "INSERT INTO records(event_id,created_utc,record_json) VALUES($id,$utc,$record)";
                    insert.Parameters.AddWithValue("$id", record.EventId.ToString("N"));
                    insert.Parameters.AddWithValue("$utc", record.CreatedUtc.ToString("O"));
                    insert.Parameters.AddWithValue("$record", JsonSerializer.Serialize(record));
                    insert.ExecuteNonQuery();
                }
                foreach (var profile in _routes.Values)
                {
                    using var insert = db.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText =
                        "INSERT INTO deliveries(event_id,sink_id,payload,route_json,state,attempt,due_utc) VALUES($id,$sink,$payload,$route,0,0,$due)";
                    insert.Parameters.AddWithValue("$id", record.EventId.ToString("N"));
                    insert.Parameters.AddWithValue("$sink", profile.SinkId);
                    insert.Parameters.AddWithValue("$payload", payloads[profile.SinkId]);
                    insert.Parameters.AddWithValue("$route", profile.ProviderOptions.GetRawText());
                    insert.Parameters.AddWithValue("$due", DateTimeOffset.UtcNow.ToString("O"));
                    insert.ExecuteNonQuery();
                }
                tx.Commit();
                RefreshCounts(db);
                _lastError = null;
                Log("admission-accepted", record.EventId, null);
                SignalAll();
                return ValueTask.FromResult(true);
            }
            catch (Exception ex)
            {
                _lastError = "输出存储写入失败：" + ex.GetType().Name;
                Log("admission-storage-error", record.EventId, null);
                return ValueTask.FromResult(false);
            }
        }
    }

    public int RetryUncertain() => Requeue(onlyUncertain: true);
    public int ResendCompleted() => Requeue(onlyUncertain: false);

    private int Requeue(bool onlyUncertain)
    {
        lock (_gate)
        {
            if (_routes.Count == 0) throw new InvalidOperationException("请先启用输出通道。");
            using var db = Open();
            int changed = 0;
            foreach (var profile in _routes.Values)
            {
                int limit = onlyUncertain ? int.MaxValue :
                    Math.Max(0, profile.Capacity - (_counts.TryGetValue(profile.SinkId, out var c) ? c.Pending : 0));
                if (limit == 0) continue;
                using var cmd = db.CreateCommand();
                cmd.CommandText =
                    "UPDATE deliveries SET state=0,attempt=0,due_utc=$now,last_error=NULL WHERE rowid IN (SELECT rowid FROM deliveries WHERE sink_id=$sink AND state=$state ORDER BY rowid DESC LIMIT $limit)";
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$sink", profile.SinkId);
                cmd.Parameters.AddWithValue("$state", onlyUncertain ? Uncertain : Complete);
                cmd.Parameters.AddWithValue("$limit", limit);
                changed += cmd.ExecuteNonQuery();
            }
            RefreshCounts(db);
            Log(onlyUncertain ? "manual-retry-uncertain" : "manual-resend", null, null);
            SignalAll();
            return changed;
        }
    }

    public int ClearPending(string? sinkId = null)
    {
        lock (_gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            int deletedDeliveries;
            using (var delCmd = db.CreateCommand())
            {
                delCmd.Transaction = tx;
                if (string.IsNullOrEmpty(sinkId))
                {
                    delCmd.CommandText = "DELETE FROM deliveries WHERE state IN (0,2)";
                }
                else
                {
                    delCmd.CommandText = "DELETE FROM deliveries WHERE sink_id=$sink AND state IN (0,2)";
                    delCmd.Parameters.AddWithValue("$sink", sinkId);
                }
                deletedDeliveries = delCmd.ExecuteNonQuery();
            }

            using (var cleanCmd = db.CreateCommand())
            {
                cleanCmd.Transaction = tx;
                cleanCmd.CommandText = "DELETE FROM records WHERE event_id NOT IN (SELECT event_id FROM deliveries)";
                cleanCmd.ExecuteNonQuery();
            }
            tx.Commit();

            RefreshCounts(db);
            _lastError = null;
            Log("manual-clear-pending", null, sinkId);
            return deletedDeliveries;
        }
    }

    private async Task SenderLoop(string sinkId)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                PendingItem? item;
                lock (_gate) item = _routes.ContainsKey(sinkId) ? NextDue(sinkId) : null;
                if (item is null)
                {
                    await _wakes[sinkId].WaitAsync(
                        sinkId == "keyboard" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(2),
                        _stop.Token).ConfigureAwait(false);
                    continue;
                }
                var record = JsonSerializer.Deserialize<ScanRecord>(item.RecordJson)!;
                await using IOutputSink sink = sinkId switch
                {
                    "mqtt" => new MqttOutputSink(JsonSerializer.Deserialize<MqttRoute>(item.RouteJson)!),
                    "tcp" => new TcpOutputSink(JsonSerializer.Deserialize<TcpRoute>(item.RouteJson)!),
                    "keyboard" => KeyboardOutputSink.Create(JsonSerializer.Deserialize<KeyboardRoute>(item.RouteJson)!),
                    _ => throw new InvalidOperationException("未知输出通道。")
                };
                var receipt = await sink.SendAsync(new(record,
                    sinkId == "keyboard" ? "text/plain" : "application/json",
                    [.. item.Payload], item.Attempt + 1), _stop.Token).ConfigureAwait(false);
                lock (_gate) UpdateDelivery(item, receipt);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _lastError = sinkId + " 发送器异常：" + ex.GetType().Name;
                    Log("sender-error", null, sinkId);
                }
                try { await Task.Delay(2000, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private PendingItem? NextDue(string sinkId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT d.event_id,d.payload,d.route_json,r.record_json,d.attempt FROM deliveries d JOIN records r ON r.event_id=d.event_id WHERE d.sink_id=$sink AND d.state=0 AND d.due_utc<=$now ORDER BY d.rowid LIMIT 1";
        cmd.Parameters.AddWithValue("$sink", sinkId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(Guid.ParseExact(reader.GetString(0), "N"), sinkId,
            (byte[])reader[1], reader.GetString(2), reader.GetString(3), reader.GetInt32(4)) : null;
    }

    private void UpdateDelivery(PendingItem item, DeliveryReceipt receipt)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        int state = receipt.Disposition switch
        {
            DeliveryDisposition.Acknowledged or DeliveryDisposition.LocallyAccepted => Complete,
            DeliveryDisposition.Unknown => Uncertain,
            _ => 0
        };
        cmd.CommandText =
            "UPDATE deliveries SET state=$state,attempt=attempt+1,due_utc=$due,last_error=$error WHERE event_id=$id AND sink_id=$sink AND state=0";
        cmd.Parameters.AddWithValue("$state", state);
        int delaySeconds = item.SinkId == "keyboard" ? 1 : (int)Math.Min(30,
            2 * Math.Pow(2, Math.Min(4, item.Attempt)));
        cmd.Parameters.AddWithValue("$due", DateTimeOffset.UtcNow.AddSeconds(delaySeconds).ToString("O"));
        string errorText = !string.IsNullOrWhiteSpace(receipt.Message)
            ? $"{receipt.Code}: {receipt.Message}"
            : (receipt.Code ?? "");
        cmd.Parameters.AddWithValue("$error", string.IsNullOrEmpty(errorText) ? DBNull.Value : errorText);
        cmd.Parameters.AddWithValue("$id", item.EventId.ToString("N"));
        cmd.Parameters.AddWithValue("$sink", item.SinkId);
        cmd.ExecuteNonQuery();
        if (state == Complete && _counts.TryGetValue(item.SinkId, out var prior) && (prior.Delivered + 1) % 1000 == 0)
            PruneCompleted(db);
        RefreshCounts(db);
        _lastReceiptCode = receipt.Code;
        _lastError = state == Complete ? null : state == Uncertain
            ? item.SinkId + " 发送结果不确定，需人工核对。" : item.SinkId + " 未送达: " + errorText;
        Log(state == Complete ? "delivery-complete" : state == Uncertain ? "delivery-uncertain" : "delivery-retry",
            item.EventId, item.SinkId, errorText);
    }

    private void EnsureDatabase()
    {
        if (_initialized) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS records(event_id TEXT PRIMARY KEY,created_utc TEXT NOT NULL,record_json TEXT NOT NULL)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='deliveries'";
        bool exists = Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
        if (!exists) CreateDeliveryTable(cmd);
        cmd.CommandText = "CREATE INDEX IF NOT EXISTS deliveries_due ON deliveries(sink_id,state,due_utc)";
        cmd.ExecuteNonQuery();
        PruneCompleted(db);
        RefreshCounts(db);
        _initialized = true;
        Log("output-storage-open", null, null);
    }

    private static void CreateDeliveryTable(SqliteCommand cmd)
    {
        cmd.CommandText =
            "CREATE TABLE deliveries(event_id TEXT NOT NULL REFERENCES records(event_id),sink_id TEXT NOT NULL,payload BLOB NOT NULL,route_json TEXT NOT NULL,state INTEGER NOT NULL,attempt INTEGER NOT NULL,due_utc TEXT NOT NULL,last_error TEXT,PRIMARY KEY(event_id,sink_id))";
        cmd.ExecuteNonQuery();
    }

    private void RefreshCounts(SqliteConnection db)
    {
        _counts.Clear();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT sink_id,state,count(*) FROM deliveries GROUP BY sink_id,state";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string id = reader.GetString(0);
            if (!_counts.TryGetValue(id, out var count)) _counts.Add(id, count = new());
            int state = reader.GetInt32(1), value = reader.GetInt32(2);
            if (state == Complete) count.Delivered += value;
            else
            {
                count.Pending += value;
                if (state == Uncertain) count.Uncertain += value;
            }
        }
    }

    private static void PruneCompleted(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "DELETE FROM deliveries WHERE state=1 AND (event_id IN (SELECT event_id FROM records WHERE created_utc<$cutoff) OR rowid NOT IN (SELECT rowid FROM deliveries WHERE state=1 ORDER BY rowid DESC LIMIT 50000)); DELETE FROM records WHERE event_id NOT IN (SELECT event_id FROM deliveries)";
        cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _dbPath, Pooling = true, DefaultTimeout = 5 }.ToString());
        db.Open();
        return db;
    }

    private void SignalAll()
    {
        foreach (var wake in _wakes.Values) if (wake.CurrentCount == 0) wake.Release();
    }

    private void Log(string kind, Guid? eventId, string? sinkId, string? details = null)
    {
        if (!string.IsNullOrEmpty(details))
            _logger.Information("{EventKind} eventId={EventId} sinkId={SinkId} details={Details}", kind, eventId, sinkId, details);
        else
            _logger.Information("{EventKind} eventId={EventId} sinkId={SinkId}", kind, eventId, sinkId);
    }

    public void LogLifecycle(string eventKind) => Log(eventKind, null, null);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        lock (_gate) SignalAll();
        await Task.WhenAll(_workers.Values).ConfigureAwait(false);
        foreach (var wake in _wakes.Values) wake.Dispose();
        _stop.Dispose();
        if (_logger is IDisposable disposable) disposable.Dispose();
    }
}
