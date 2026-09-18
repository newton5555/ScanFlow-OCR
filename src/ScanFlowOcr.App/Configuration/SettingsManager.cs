using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScanFlowOcr.App.Models;

namespace ScanFlowOcr.App.Configuration;

public sealed class SettingsManager : ISettingsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _primaryFilePath;
    private readonly string _fallbackFilePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private AppSettings _current;
    private string _activeFilePath;

    public AppSettings Current => _current;
    public string ActiveFilePath => _activeFilePath;
    public event Action<AppSettings>? SettingsChanged;

    public SettingsManager(string? primaryFilePath = null, string? fallbackFilePath = null)
    {
        _primaryFilePath = primaryFilePath ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        _fallbackFilePath = fallbackFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScanFlowOcr",
            "appsettings.json");
        _current = new AppSettings();
        _activeFilePath = _primaryFilePath;
        Reload();
    }

    public AppSettings CreateDefault() => new();

    public void Reload()
    {
        _fileLock.Wait();
        try
        {
            string? pathToLoad = null;
            if (File.Exists(_primaryFilePath)) pathToLoad = _primaryFilePath;
            else if (File.Exists(_fallbackFilePath)) pathToLoad = _fallbackFilePath;

            if (pathToLoad is null)
            {
                _current = CreateDefault();
                _activeFilePath = DeterminePreferredSavePath();
                return;
            }

            _activeFilePath = pathToLoad;
            try
            {
                var loaded = ParseSettings(File.ReadAllText(pathToLoad));
                if (loaded is not null)
                {
                    _current = loaded;
                    return;
                }
            }
            catch
            {
                // keep defaults
            }
            _current = CreateDefault();
        }
        finally { _fileLock.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var targetPath = DeterminePreferredSavePath();
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            var rootDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(targetPath))
            {
                try
                {
                    var existingBytes = await File.ReadAllBytesAsync(targetPath, cancellationToken).ConfigureAwait(false);
                    using var existingDoc = JsonDocument.Parse(existingBytes);
                    if (existingDoc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in existingDoc.RootElement.EnumerateObject())
                        {
                            if (!string.Equals(prop.Name, "ScanFlowOcr", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(prop.Name, "ScanFlow", StringComparison.OrdinalIgnoreCase))
                                rootDict[prop.Name] = prop.Value.Clone();
                        }
                    }
                }
                catch { /* rewrite */ }
            }

            rootDict["ScanFlowOcr"] = settings;
            var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(rootDict, JsonOptions);
            var tempPath = targetPath + $".tmp_{Guid.NewGuid():N}";
            await File.WriteAllBytesAsync(tempPath, serializedBytes, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, targetPath, overwrite: true);
            _current = settings.Clone();
            _activeFilePath = targetPath;
        }
        finally { _fileLock.Release(); }

        SettingsChanged?.Invoke(_current);
    }

    private static AppSettings? ParseSettings(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        JsonElement target = root;
        if (root.TryGetProperty("ScanFlowOcr", out var ocr) && ocr.ValueKind == JsonValueKind.Object)
            target = ocr;
        else if (root.TryGetProperty("ScanFlow", out var sf) && sf.ValueKind == JsonValueKind.Object)
            target = sf;

        var result = JsonSerializer.Deserialize<AppSettings>(target.GetRawText(), JsonOptions);
        if (result is not null)
            result.OcrParameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private string DeterminePreferredSavePath()
    {
        if (string.Equals(_activeFilePath, _fallbackFilePath, StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(_primaryFilePath))
            return _fallbackFilePath;

        var primaryDir = Path.GetDirectoryName(_primaryFilePath);
        if (!string.IsNullOrEmpty(primaryDir) && IsDirectoryWritable(primaryDir))
            return _primaryFilePath;
        return _fallbackFilePath;
    }

    private static bool IsDirectoryWritable(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath)) return false;
            var testFile = Path.Combine(directoryPath, $".scanflow_ocr_perm_test_{Guid.NewGuid():N}.tmp");
            using (var fs = new FileStream(testFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                fs.WriteByte(1);
            File.Delete(testFile);
            return true;
        }
        catch { return false; }
    }

    public void Dispose() => _fileLock.Dispose();
}
