using System.Security.Cryptography;
using Grpc.Core;

namespace WindowsPowerShellRc;

/// <summary>RC-wide admission, independent of TCP channels and accepted executions.</summary>
public sealed class ClientSessions(TimeProvider? clock = null, TimeSpan? timeout = null, Action<string>? log = null, string mode = "direct")
{
    private readonly object gate = new();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly TimeSpan timeout = timeout ?? TimeSpan.FromSeconds(20);
    private Lease? owner;
    public sealed class Lease
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public string Credential { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        internal long LastSeen;
    }
    public Lease Acquire()
    {
        lock (gate)
        {
            Expire();
            if (owner is not null) throw new RpcException(new Status(StatusCode.AlreadyExists, "Another diagnostic client is connected. Disconnect it or wait for its session to expire."));
            owner = new() { LastSeen = clock.GetTimestamp() };
            Write($"Client connected; session {owner.Id}; {mode}.");
            return owner;
        }
    }
    public void Heartbeat(Lease lease)
    {
        lock (gate)
        {
            Expire();
            if (!ReferenceEquals(owner, lease)) throw Required();
            lease.LastSeen = clock.GetTimestamp();
        }
    }
    public void Validate(Metadata headers)
    {
        var values = headers.Where(h => h.Key == "x-rc-session").ToArray();
        lock (gate)
        {
            Expire();
            if (owner is null || values.Length != 1 || values[0].Value.Length != 64 ||
                !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(values[0].Value), System.Text.Encoding.ASCII.GetBytes(owner.Credential)))
                throw Required();
        }
    }
    public void Release(Lease lease, string reason)
    {
        lock (gate) { if (ReferenceEquals(owner, lease)) { owner = null; Write($"Client disconnected; session {lease.Id}; {mode}; {reason}."); } }
    }
    private void Expire()
    {
        if (owner is { } lease && clock.GetElapsedTime(lease.LastSeen) >= timeout) Release(lease, "heartbeat expired");
    }
    private void Write(string message) => log?.Invoke($"[{clock.GetUtcNow():O}] {message}");
    private static RpcException Required() => new(new Status(StatusCode.FailedPrecondition, "An active diagnostic session is required. Connect using a current client."));
}
