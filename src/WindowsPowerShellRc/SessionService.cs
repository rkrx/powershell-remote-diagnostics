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
        try
        {
            await responses.WriteAsync(new() { Credential = lease.Credential }, context.CancellationToken);
            while (await requests.MoveNext(context.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), context.CancellationToken))
            {
                if (!requests.Current.Heartbeat || requests.Current.Credential.Length != 0)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Expected a session heartbeat."));
                sessions.Heartbeat(lease);
                await responses.WriteAsync(new() { Heartbeat = true }, context.CancellationToken);
            }
        }
        catch (TimeoutException) { reason = "heartbeat expired"; throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Session heartbeat expired.")); }
        finally { sessions.Release(lease, reason); }
    }
}
