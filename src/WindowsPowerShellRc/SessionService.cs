using Grpc.Core;
using WindowsPowerShellRc.Protocol;
namespace WindowsPowerShellRc;

public static class SessionService
{
    public static async Task Run(ClientSessions sessions, IAsyncStreamReader<SessionFrame> requests,
        IServerStreamWriter<SessionFrame> responses, ServerCallContext context)
    {
        var lease = sessions.Acquire();
        string reason = "connection closed";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, deadline.Token);
        try
        {
            await responses.WriteAsync(new() { Credential = lease.Credential }, stop.Token);
            while (await requests.MoveNext(stop.Token))
            {
                if (!requests.Current.Heartbeat || requests.Current.Credential.Length != 0)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Expected a session heartbeat."));
                sessions.Heartbeat(lease);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                await responses.WriteAsync(new() { Heartbeat = true }, stop.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested) { reason = "heartbeat expired"; throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Session heartbeat expired.")); }
        finally { sessions.Release(lease, reason); }
    }
}
