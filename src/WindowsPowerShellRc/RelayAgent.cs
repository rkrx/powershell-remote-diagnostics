using Grpc.Core;
using System.Net;
using System.Net.Sockets;
using WindowsPowerShellRc.Protocol;
using WindowsPowerShellRc.Transport;
namespace WindowsPowerShellRc;

public static class RelayAgent
{
    public static async Task RunAsync(ConnectionAddress relay, int localPort, Action<string, int> allocated, CancellationToken token)
    {
        using var channel = relay.CreateChannel();
        var client = new Relay.RelayClient(channel);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var registration = client.Register(relay.Headers, cancellationToken: stop.Token);
        using var capacity = new SemaphoreSlim(8);
        var workers = new Dictionary<string, Task>(); var gate = new object();
        Task sending = Task.CompletedTask, receiving = Task.CompletedTask;
        try
        {
            if (!await registration.ResponseStream.MoveNext(stop.Token).WaitAsync(TimeSpan.FromSeconds(10), token))
                throw new IOException("Relay did not acknowledge registration.");
            var ack = registration.ResponseStream.Current;
            if (ack.Lease.Length != 64 || ack.Port is < 1 or > 65535 || Uri.CheckHostName(ack.Host) == UriHostNameType.Unknown ||
                IPAddress.TryParse(ack.Host.Trim('[', ']'), out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)))
                throw new IOException("Relay returned an invalid allocation.");
            var headers = relay.Headers; headers.Add("x-relay-lease", ack.Lease);
            allocated(ack.Host.Trim('[', ']'), ack.Port);
            async Task Send()
            {
                while (true)
                {
                    await registration.RequestStream.WriteAsync(new() { Heartbeat = true }, stop.Token);
                    await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
                }
            }
            async Task Bridge(string id)
            {
                try
                {
                    using var socket = new TcpClient();
                    using var opening = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    opening.CancelAfter(TimeSpan.FromSeconds(10));
                    await socket.ConnectAsync(IPAddress.Loopback, localPort, opening.Token);
                    using var call = client.Tunnel(headers, cancellationToken: stop.Token);
                    await call.RequestStream.WriteAsync(new() { ConnectionId = id }, opening.Token);
                    await TunnelPump.RunAsync(socket, call.ResponseStream, (frame, ct) => call.RequestStream.WriteAsync(frame, ct), stop.Token);
                    await call.RequestStream.CompleteAsync();
                }
                catch (Exception ex) when (ex is RpcException or OperationCanceledException or IOException or SocketException or ObjectDisposedException) { /* One failed external socket does not kill its RC registration. */ }
                finally { lock (gate) workers.Remove(id); capacity.Release(); }
            }
            async Task Receive()
            {
                while (await registration.ResponseStream.MoveNext(stop.Token).WaitAsync(TimeSpan.FromSeconds(20), stop.Token))
                {
                    var frame = registration.ResponseStream.Current;
                    if (frame.Heartbeat && frame.ConnectionId.Length == 0 && frame.Lease.Length == 0 && frame.Host.Length == 0 && frame.Port == 0) continue;
                    if (!Guid.TryParseExact(frame.ConnectionId, "D", out _) || frame.Heartbeat || frame.Lease.Length != 0 || frame.Host.Length != 0 || frame.Port != 0)
                        throw new IOException("Relay returned an invalid connection notification.");
                    lock (gate)
                        if (workers.ContainsKey(frame.ConnectionId)) throw new IOException("Relay repeated an active connection notification.");
                    // Relay and agent finish their two ends at different times. Wait for
                    // bounded local capacity instead of treating ordinary cleanup skew as fatal.
                    if (!await capacity.WaitAsync(TimeSpan.FromSeconds(10), stop.Token)) continue;
                    lock (gate)
                    {
                        string id = frame.ConnectionId;
                        workers.Add(id, Task.Run(() => Bridge(id), CancellationToken.None));
                    }
                }
                throw new IOException("Relay registration ended. Restart the RC to reconnect.");
            }
            sending = Send(); receiving = Receive();
            await await Task.WhenAny(sending, receiving);
        }
        finally
        {
            stop.Cancel(); registration.Dispose();
            Task[] bridges; lock (gate) bridges = workers.Values.ToArray();
            try { await Task.WhenAll(bridges.Append(sending).Append(receiving)); }
            catch (Exception ex) when (ex is RpcException or OperationCanceledException or IOException or TimeoutException) { }
        }
    }
}
