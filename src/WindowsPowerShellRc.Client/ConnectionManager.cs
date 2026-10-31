using System.Collections.Concurrent;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using WindowsPowerShellRc.Protocol;
using WindowsPowerShellRc.Transport;

namespace WindowsPowerShellRc.Client;
public sealed class ClientOperationException(string message) : Exception(message);
public interface IConnectionPrompt { Task<string?> RequestAsync(CancellationToken cancellationToken); }
public sealed class RemoteSession(GrpcChannel channel, DiagnosticSession admission) : IDisposable
{
    public Diagnostics.DiagnosticsClient Client { get; } = new(channel);
    public Metadata Headers => admission.Headers;
    public bool IsAlive => admission.IsAlive;
    public void Dispose() { admission.Dispose(); channel.Dispose(); }
}
public sealed class ConnectionManager(IConnectionPrompt prompt) : IDisposable
{
    private static readonly JsonFormatter Formatter = new(new JsonFormatter.Settings(true));
    private readonly ConcurrentDictionary<string, RemoteSession> sessions = new();
    private readonly SemaphoreSlim connecting = new(1);
    public async Task<string> ConnectAsync(string? connectionString, CancellationToken token)
    {
        await connecting.WaitAsync(token);
        try
        {
            if (sessions.Count >= 16) throw new ClientOperationException("Connection limit reached. Disconnect an unused connection first.");
            if (string.IsNullOrWhiteSpace(connectionString))
                connectionString = await prompt.RequestAsync(token) ?? throw new OperationCanceledException("Connection entry was cancelled.");
            var address = ConnectionAddress.Parse(connectionString);
            var channel = address.CreateChannel();
            RemoteSession? session = null;
            try
            {
                var admission = await DiagnosticSession.OpenAsync(new Diagnostics.DiagnosticsClient(channel), address.Headers, token);
                session = new RemoteSession(channel, admission);
                var info = await session.Client.GetInfoAsync(new(), session.Headers, DateTime.UtcNow.AddSeconds(10), token);
                string id = Guid.NewGuid().ToString(); sessions[id] = session;
                return JsonSerializer.Serialize(new { connectionId = id, host = address.Host, port = address.Port,
                    serverInfo = JsonDocument.Parse(Formatter.Format(info)).RootElement });
            }
            catch (RpcException ex)
            {
                session?.Dispose(); channel.Dispose();
                throw new ClientOperationException($"Connection failed ({ex.StatusCode}). Check the address, server token and certificate fingerprint; no connection was retained.");
            }
            catch { session?.Dispose(); channel.Dispose(); throw; }
        }
        finally { connecting.Release(); }
    }
    public async Task<string> InvokeAsync<T>(string connectionId, Func<RemoteSession, Task<T>> operation) where T : IMessage
    {
        if (!sessions.TryGetValue(connectionId, out var session)) throw new ClientOperationException("Unknown connection ID. Connect first.");
        if (!session.IsAlive) throw new ClientOperationException("Connection session has ended. Disconnect and connect again; accepted work may still be running. Do not blindly retry submission.");
        try { return Formatter.Format(await operation(session)); }
        catch (RpcException ex)
        {
            throw new ClientOperationException($"Remote request failed ({ex.StatusCode}). An interrupted execution may still be running; do not blindly retry submission.");
        }
    }
    public string Disconnect(string connectionId)
    {
        if (!sessions.TryRemove(connectionId, out var session)) throw new ClientOperationException("Unknown connection ID.");
        session.Dispose(); return "Disconnected. Accepted remote executions were not cancelled.";
    }
    public void Dispose() { foreach (var s in sessions.Values) s.Dispose(); sessions.Clear(); connecting.Dispose(); }
}
