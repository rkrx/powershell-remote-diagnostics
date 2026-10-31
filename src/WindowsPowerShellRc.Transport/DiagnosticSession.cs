using Grpc.Core;
using WindowsPowerShellRc.Protocol;
namespace WindowsPowerShellRc.Transport;

/// <summary>Owns one admission stream; never reconnects or resubmits work.</summary>
public sealed class DiagnosticSession : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly AsyncDuplexStreamingCall<SessionFrame, SessionFrame> call;
    public Metadata Headers { get; private set; } = new();
    public Task Completion { get; private set; } = Task.CompletedTask;
    public bool IsAlive => !stop.IsCancellationRequested && !Completion.IsCompleted;
    private DiagnosticSession(Diagnostics.DiagnosticsClient client, Metadata bearer) => call = client.Session(bearer, cancellationToken: stop.Token);
    public static async Task<DiagnosticSession> OpenAsync(Diagnostics.DiagnosticsClient client, Metadata bearer, CancellationToken token)
    {
        var session = new DiagnosticSession(client, bearer);
        try
        {
            if (!await session.call.ResponseStream.MoveNext(session.stop.Token).WaitAsync(TimeSpan.FromSeconds(10), token) ||
                session.call.ResponseStream.Current.Credential.Length != 64)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Server did not establish a diagnostic session."));
            session.Headers = new Metadata();
            foreach (var entry in bearer) session.Headers.Add(entry);
            session.Headers.Add("x-rc-session", session.call.ResponseStream.Current.Credential);
            session.Completion = session.Run();
            return session;
        }
        catch { session.Dispose(); throw; }
    }
    private async Task Run()
    {
        using var workers = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        async Task Send()
        {
            while (true)
            {
                await call.RequestStream.WriteAsync(new() { Heartbeat = true }, workers.Token);
                await Task.Delay(TimeSpan.FromSeconds(5), workers.Token);
            }
        }
        var sending = Send();
        try
        {
            while (await call.ResponseStream.MoveNext(workers.Token).WaitAsync(TimeSpan.FromSeconds(20), workers.Token))
                if (!call.ResponseStream.Current.Heartbeat) throw new IOException("Invalid session heartbeat.");
        }
        catch (Exception ex) when (ex is RpcException or OperationCanceledException or TimeoutException or IOException) { }
        finally
        {
            workers.Cancel();
            try { await sending; } catch (Exception ex) when (ex is RpcException or OperationCanceledException or IOException or InvalidOperationException) { }
            call.Dispose();
        }
    }
    public void Dispose() { stop.Cancel(); call.Dispose(); }
}
