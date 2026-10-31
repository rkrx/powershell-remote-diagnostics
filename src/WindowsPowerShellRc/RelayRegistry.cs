using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Grpc.Core;
using WindowsPowerShellRc.Protocol;
namespace WindowsPowerShellRc;

/// <summary>Owns actual bound listeners until all sockets of an RC registration close.</summary>
public sealed class RelayRegistry(RelayOptions options) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Lease> leases = new();
    private bool stopping;
    public sealed class Lease(int port, List<TcpListener> listeners)
    {
        public int Port { get; } = port;
        public string Credential { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        internal List<TcpListener> Listeners { get; } = listeners;
        internal CancellationTokenSource Stop { get; } = new();
        internal Channel<RelayControl> Messages { get; } = Channel.CreateBounded<RelayControl>(16);
        internal ConcurrentDictionary<string, Pending> Sockets { get; } = new();
        internal List<Task> Accepting { get; } = [];
        internal Task? Releasing;
    }
    public sealed class Pending(TcpClient socket)
    {
        internal TcpClient Socket { get; } = socket;
        internal TaskCompletionSource Claimed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Worker { get; set; } = Task.CompletedTask;
    }
    public Lease Allocate()
    {
        lock (gate)
        {
            if (stopping || leases.Count >= 100) throw Full();
            for (int port = options.RangeStart; port <= options.RangeEnd; port++)
            {
                if (leases.Values.Any(l => l.Port == port)) continue;
                var listeners = new List<TcpListener>();
                try
                {
                    foreach (var address in new CliOptions(Bind: options.Bind).ResolveAddresses())
                    {
                        var listener = new TcpListener(address, port);
                        if (address.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
                        // Do not permit rebinding a still-owned Windows listener.
                        if (OperatingSystem.IsWindows()) listener.Server.ExclusiveAddressUse = true;
                        else listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        listeners.Add(listener); listener.Start();
                    }
                    var lease = new Lease(port, listeners); leases.Add(lease.Credential, lease);
                    foreach (var listener in listeners) lease.Accepting.Add(Accept(lease, listener));
                    return lease;
                }
                catch (SocketException) { foreach (var listener in listeners) listener.Stop(); }
            }
            throw Full();
        }
    }
    private static RpcException Full() => new(new Status(StatusCode.ResourceExhausted, "No relay registration capacity or free public port is available."));
    private static async Task Accept(Lease lease, TcpListener listener)
    {
        try
        {
            while (!lease.Stop.IsCancellationRequested)
            {
                var socket = await listener.AcceptTcpClientAsync(lease.Stop.Token);
                lock (lease)
                {
                    if (lease.Stop.IsCancellationRequested || lease.Sockets.Count >= 8) { socket.Dispose(); continue; }
                    string id = Guid.NewGuid().ToString();
                    var pending = new Pending(socket); lease.Sockets[id] = pending;
                    if (!lease.Messages.Writer.TryWrite(new() { ConnectionId = id }))
                    { lease.Sockets.TryRemove(id, out _); socket.Dispose(); continue; }
                    pending.Worker = WaitForTunnel(lease, id, pending);
                }
            }
        }
        catch (Exception ex) when (lease.Stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private static async Task WaitForTunnel(Lease lease, string id, Pending pending)
    {
        try
        {
            await pending.Claimed.Task.WaitAsync(TimeSpan.FromSeconds(10), lease.Stop.Token);
            await pending.Finished.Task.WaitAsync(lease.Stop.Token);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        finally
        {
            lock (lease) { lease.Sockets.TryRemove(id, out _); pending.Socket.Dispose(); pending.Finished.TrySetResult(); }
        }
    }
    public (Lease Lease, Pending Tunnel) Claim(string credential, string connectionId)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(credential, out var lease) || lease.Stop.IsCancellationRequested)
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid relay lease."));
            lock (lease)
            {
                if (!lease.Sockets.TryGetValue(connectionId, out var tunnel) || !tunnel.Claimed.TrySetResult())
                    throw new RpcException(new Status(StatusCode.NotFound, "Unknown or already claimed relay connection."));
                return (lease, tunnel);
            }
        }
    }
    public Task ReleaseAsync(Lease lease)
    {
        lock (gate) return lease.Releasing ??= ReleaseCore(lease);
    }
    private async Task ReleaseCore(Lease lease)
    {
        lease.Stop.Cancel();
        foreach (var listener in lease.Listeners) listener.Stop();
        await Task.WhenAll(lease.Accepting);
        Pending[] pending;
        lock (lease) { pending = lease.Sockets.Values.ToArray(); foreach (var item in pending) item.Socket.Dispose(); }
        await Task.WhenAll(pending.Select(p => p.Worker));
        lease.Messages.Writer.TryComplete();
        lock (gate) leases.Remove(lease.Credential);
    }
    public async ValueTask DisposeAsync()
    {
        Lease[] active;
        lock (gate) { stopping = true; active = leases.Values.ToArray(); }
        await Task.WhenAll(active.Select(ReleaseAsync));
    }
}
