using Google.Protobuf;
using Grpc.Core;
using System.Net.Sockets;
using WindowsPowerShellRc.Protocol;
namespace WindowsPowerShellRc;

public static class TunnelPump
{
    public static async Task RunAsync(TcpClient socket, IAsyncStreamReader<TunnelFrame> incoming,
        Func<TunnelFrame, CancellationToken, Task> send, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(TimeSpan.FromSeconds(20));
        var stream = socket.GetStream();
        async Task FromSocket()
        {
            byte[] buffer = new byte[32768];
            int count;
            while ((count = await stream.ReadAsync(buffer, stop.Token)) != 0)
            {
                stop.CancelAfter(TimeSpan.FromSeconds(20));
                await send(new() { Data = ByteString.CopyFrom(buffer, 0, count) }, stop.Token);
            }
            await send(new() { Eof = true }, stop.Token);
        }
        async Task ToSocket()
        {
            while (await incoming.MoveNext(stop.Token))
            {
                stop.CancelAfter(TimeSpan.FromSeconds(20));
                var frame = incoming.Current;
                if (frame.ConnectionId.Length != 0 || frame.Data.Length > 32768 || frame.Eof && frame.Data.Length != 0 || !frame.Eof && frame.Data.Length == 0)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid relay data frame."));
                if (frame.Eof) { socket.Client.Shutdown(SocketShutdown.Send); return; }
                await stream.WriteAsync(frame.Data.Memory, stop.Token);
            }
            throw new IOException("Relay stream ended without EOF.");
        }
        var sending = FromSocket(); var receiving = ToSocket();
        try
        {
            var first = await Task.WhenAny(sending, receiving);
            await first; // Failure cancels the other direction; EOF preserves half-close responses.
            await Task.WhenAll(sending, receiving);
        }
        finally
        {
            stop.Cancel(); socket.Dispose();
            try { await Task.WhenAll(sending, receiving); } catch { /* preserve the original failure */ }
        }
    }
}
