using System.Security.Cryptography;
using System.Text;
using MQTTnet;
using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.Outputs;

public sealed record MqttRoute(string Broker, int Port, bool Tls, string ClientId, string Topic,
    int Qos, string? Username, string? ProtectedPassword)
{
    public static MqttRoute Default => new(
        "localhost", 1883, false,
        $"scanflow-ocr-{Environment.MachineName.ToLowerInvariant()}",
        "scanflow-ocr/scans", 1, null, null);

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Broker) || Broker.Any(char.IsWhiteSpace)) return "MQTT 地址无效。";
        if (Port is < 1 or > 65535) return "MQTT 端口必须在 1–65535。";
        if (string.IsNullOrWhiteSpace(ClientId)) return "MQTT Client ID 不能为空。";
        if (string.IsNullOrWhiteSpace(Topic) || Topic.Contains('+') || Topic.Contains('#'))
            return "MQTT 发布主题不能为空或包含通配符。";
        if (Qos is < 0 or > 2) return "MQTT QoS 必须为 0、1 或 2。";
        return null;
    }

    /// <summary>
    /// Windows: DPAPI-unprotect base64 blob. Non-Windows: treat as plaintext (no DPAPI).
    /// </summary>
    public string? GetPassword()
    {
        if (string.IsNullOrEmpty(ProtectedPassword)) return null;
        if (OperatingSystem.IsWindows())
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(ProtectedPassword), null, DataProtectionScope.CurrentUser));
        return ProtectedPassword;
    }

    /// <summary>
    /// Windows: DPAPI-protect. Non-Windows: store plaintext in <see cref="ProtectedPassword"/>.
    /// </summary>
    public static string? ProtectPassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        if (OperatingSystem.IsWindows())
            return Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
        return password;
    }
}

public sealed class MqttOutputSink(MqttRoute route) : IOutputSink
{
    public OutputSinkDescriptor Descriptor => new("mqtt", route.Qos > 0, true);

    public async ValueTask<DeliveryReceipt> SendAsync(OutputMessage message, CancellationToken cancellationToken)
    {
        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(route.Broker, route.Port)
            .WithClientId(route.ClientId);
        if (route.Tls) options.WithTlsOptions(o => o.UseTls());
        if (!string.IsNullOrWhiteSpace(route.Username))
            options.WithCredentials(route.Username, route.GetPassword());
        try
        {
            using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            var connected = await client.ConnectAsync(options.Build(), connectDeadline.Token).ConfigureAwait(false);
            if (connected.ResultCode != MqttClientConnectResultCode.Success)
                return new(message.Record.EventId, "mqtt", DeliveryDisposition.NotDelivered, "ConnectRejected", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(message.Record.EventId, "mqtt", DeliveryDisposition.NotDelivered, "ConnectFailed", null); }
        try
        {
            var mqttMessage = new MqttApplicationMessageBuilder().WithTopic(route.Topic)
                .WithPayload(message.Payload.ToArray())
                .WithQualityOfServiceLevel((MQTTnet.Protocol.MqttQualityOfServiceLevel)route.Qos)
                .Build();
            using var publishDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            publishDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await client.PublishAsync(mqttMessage, publishDeadline.Token).ConfigureAwait(false);
            return result.IsSuccess
                ? new(message.Record.EventId, "mqtt",
                    route.Qos == 0 ? DeliveryDisposition.LocallyAccepted : DeliveryDisposition.Acknowledged,
                    route.Qos == 0 ? "PublishedQos0" : "BrokerAcknowledged", null)
                : new(message.Record.EventId, "mqtt", DeliveryDisposition.Unknown, "BrokerRejected", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(message.Record.EventId, "mqtt", DeliveryDisposition.Unknown, "PublishUncertain", null); }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
