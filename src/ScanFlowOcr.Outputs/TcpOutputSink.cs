using System.Net.Security;
using System.Net.Sockets;
using ScanFlowOcr.Contracts;

namespace ScanFlowOcr.Outputs;

public sealed record TcpRoute(string Host, int Port, bool Tls)
{
    public string? Validate() => string.IsNullOrWhiteSpace(Host) || Host.Any(char.IsWhiteSpace)
        ? "TCP 主机名或 IP 无效。" : Port is < 1 or > 65535 ? "TCP 端口必须在 1–65535。" : null;
}

public sealed class TcpOutputSink(TcpRoute route) : IOutputSink
{
    public OutputSinkDescriptor Descriptor => new("tcp", false, true);

    public async ValueTask<DeliveryReceipt> SendAsync(OutputMessage message, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        Stream stream;
        try
        {
            await client.ConnectAsync(route.Host, route.Port, deadline.Token).ConfigureAwait(false);
            stream = client.GetStream();
            if (route.Tls)
            {
                var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                await tls.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = route.Host }, deadline.Token).ConfigureAwait(false);
                stream = tls;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(message.Record.EventId, "tcp", DeliveryDisposition.NotDelivered, "ConnectFailed", null); }
        try
        {
            using (stream)
            {
                var payload = message.Payload.ToArray();
                await stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
                await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            return new(message.Record.EventId, "tcp", DeliveryDisposition.LocallyAccepted, "SocketWriteCompleted", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(message.Record.EventId, "tcp", DeliveryDisposition.Unknown, "WriteUncertain", null); }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
