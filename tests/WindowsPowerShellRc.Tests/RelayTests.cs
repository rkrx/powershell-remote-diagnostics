using System.Net;
using System.Net.Sockets;
using Grpc.Core;
using WindowsPowerShellRc.Client;
using WindowsPowerShellRc.Transport;
namespace WindowsPowerShellRc.Tests;

public class RelayTests
{
    [Fact]
    public void RelayRangeAndModeAreValidatedBeforeListening()
    {
        Assert.Equal(20000, RelayOptions.Parse([]).RangeStart);
        Assert.Throws<ArgumentException>(() => RelayOptions.Parse(["--port-range-start", "25001", "--port-range-end", "25000"]));
        Assert.Throws<ArgumentException>(() => RelayOptions.Parse(["--port", "20000"]));
        Assert.Throws<ArgumentException>(() => RelayOptions.Parse(["--advertise-host", "0.0.0.0"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--relay", "grpc://bad@host:8080"]));
    }

    [Fact]
    public async Task ListenerIsOwnedUntilRcLeavesAndOccupiedPortsAreSkipped()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Start();
        int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        if (port == 65535) return;
        await using var registry = new RelayRegistry(new(Port: port == 8080 ? 8081 : 8080, Bind: "127.0.0.1", RangeStart: port, RangeEnd: port + 1));
        var lease = registry.Allocate();
        Assert.Equal(port + 1, lease.Port);
        Assert.Equal(StatusCode.ResourceExhausted, Assert.Throws<RpcException>(() => registry.Allocate()).StatusCode);
        using (var tcp = new TcpClient()) { await tcp.ConnectAsync(IPAddress.Loopback, lease.Port); }
        Assert.Equal(StatusCode.ResourceExhausted, Assert.Throws<RpcException>(() => registry.Allocate()).StatusCode);
        await registry.ReleaseAsync(lease);
        var next = registry.Allocate(); Assert.Equal(port + 1, next.Port);
        await registry.ReleaseAsync(next);
    }

    [Fact]
    public async Task TunnelClaimsCannotCrossLeasesOrBeReusedAndOversizedDataIsRejected()
    {
        await using var relay = await TestRelay.Start();
        using var channel = relay.Address.CreateChannel();
        var client = new WindowsPowerShellRc.Protocol.Relay.RelayClient(channel);
        using var a = client.Register(relay.Address.Headers);
        using var b = client.Register(relay.Address.Headers);
        Assert.True(await a.ResponseStream.MoveNext(default)); var ackA = a.ResponseStream.Current;
        Assert.True(await b.ResponseStream.MoveNext(default)); var ackB = b.ResponseStream.Current;
        Assert.NotEqual(ackA.Port, ackB.Port);
        using var external = new TcpClient(); await external.ConnectAsync(IPAddress.Loopback, ackA.Port);
        Assert.True(await a.ResponseStream.MoveNext(default).WaitAsync(TimeSpan.FromSeconds(3)));
        string id = a.ResponseStream.Current.ConnectionId;
        Grpc.Core.Metadata Headers(string lease) { var h = relay.Address.Headers; h.Add("x-relay-lease", lease); return h; }
        using (var wrong = client.Tunnel(Headers(ackB.Lease)))
        {
            await wrong.RequestStream.WriteAsync(new() { ConnectionId = id });
            Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(async () => await wrong.ResponseStream.MoveNext(default))).StatusCode);
        }
        using var good = client.Tunnel(Headers(ackA.Lease));
        await good.RequestStream.WriteAsync(new() { ConnectionId = id });
        await external.GetStream().WriteAsync(new byte[] { 42 });
        Assert.True(await good.ResponseStream.MoveNext(default).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(42, good.ResponseStream.Current.Data[0]);
        using (var duplicate = client.Tunnel(Headers(ackA.Lease)))
        {
            await duplicate.RequestStream.WriteAsync(new() { ConnectionId = id });
            Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(async () => await duplicate.ResponseStream.MoveNext(default))).StatusCode);
        }
        await good.RequestStream.WriteAsync(new() { Data = Google.Protobuf.ByteString.CopyFrom(new byte[32769]) });
        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(async () => await good.ResponseStream.MoveNext(default))).StatusCode);
    }

    [Fact]
    public async Task HalfClosedRequestStillReceivesTheFullResponse()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
        async Task Respond()
        {
            using var socket = await target.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes);
            Assert.Equal("request", System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("response after EOF"));
            socket.Client.Shutdown(SocketShutdown.Send);
        }
        var responding = Respond();
        await using var relay = await TestRelay.Start();
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = RelayAgent.RunAsync(relay.Address, ((IPEndPoint)target.LocalEndpoint).Port, (_, p) => ready.TrySetResult(p), stop.Token);
        try
        {
            using var external = new TcpClient(); await external.ConnectAsync(IPAddress.Loopback, await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            var stream = external.GetStream();
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("request"));
            external.Client.Shutdown(SocketShutdown.Send);
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("response after EOF", System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
            await responding;
        }
        finally { stop.Cancel(); try { await agent; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task InvalidRegistrationTokenCannotReserveAPort()
    {
        await using var relay = await TestRelay.Start();
        using var channel = relay.Address.CreateChannel();
        using var call = new WindowsPowerShellRc.Protocol.Relay.RelayClient(channel).Register(new Metadata { { "authorization", "Bearer invalid" } });
        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext(default))).StatusCode);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var agent = RelayAgent.RunAsync(relay.Address, 1, (_, _) => ready.TrySetResult(), stop.Token);
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { stop.Cancel(); try { await agent; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task TunnelLimitAndUnclaimedTimeoutDoNotFreeTheRcPort()
    {
        await using var relay = await TestRelay.Start();
        using var channel = relay.Address.CreateChannel();
        var client = new WindowsPowerShellRc.Protocol.Relay.RelayClient(channel);
        using var control = client.Register(relay.Address.Headers);
        Assert.True(await control.ResponseStream.MoveNext(default)); var ack = control.ResponseStream.Current;
        var sockets = new List<TcpClient>();
        try
        {
            for (int i=0; i<8; i++)
            {
                var socket = new TcpClient(); sockets.Add(socket);
                await socket.ConnectAsync(IPAddress.Loopback, ack.Port);
                Assert.True(await control.ResponseStream.MoveNext(default).WaitAsync(TimeSpan.FromSeconds(2)));
            }
            using var extra = new TcpClient(); await extra.ConnectAsync(IPAddress.Loopback, ack.Port);
            Assert.Equal(0, await extra.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(0, await sockets[0].GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(12)));
            await control.RequestStream.WriteAsync(new() { Heartbeat = true });
            Assert.True(await control.ResponseStream.MoveNext(default).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(control.ResponseStream.Current.Heartbeat);
            using var second = client.Register(relay.Address.Headers);
            Assert.True(await second.ResponseStream.MoveNext(default));
            Assert.NotEqual(ack.Port, second.ResponseStream.Current.Port);
        }
        finally { foreach (var socket in sockets) socket.Dispose(); }
    }

    [Fact]
    public async Task IdleConnectedTunnelIsClosedWithoutKillingRcRegistration()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
        await using var relay = await TestRelay.Start();
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = RelayAgent.RunAsync(relay.Address, ((IPEndPoint)target.LocalEndpoint).Port, (_, p) => ready.TrySetResult(p), stop.Token);
        try
        {
            using var external = new TcpClient(); await external.ConnectAsync(IPAddress.Loopback, await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            using var accepted = await target.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, await external.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(23)));
            Assert.False(agent.IsCompleted);
        }
        finally { stop.Cancel(); try { await agent; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task MissingRegistrationHeartbeatExpiresAndReleasesItsPort()
    {
        await using var relay = await TestRelay.Start();
        using var channel = relay.Address.CreateChannel();
        var client = new WindowsPowerShellRc.Protocol.Relay.RelayClient(channel);
        using var control = client.Register(relay.Address.Headers);
        Assert.True(await control.ResponseStream.MoveNext(default)); int firstPort = control.ResponseStream.Current.Port;
        Assert.Equal(StatusCode.DeadlineExceeded, (await Assert.ThrowsAsync<RpcException>(async () =>
            await control.ResponseStream.MoveNext(default).WaitAsync(TimeSpan.FromSeconds(23)))).StatusCode);
        using var replacement = client.Register(relay.Address.Headers);
        Assert.True(await replacement.ResponseStream.MoveNext(default));
        Assert.Equal(firstPort, replacement.ResponseStream.Current.Port);
    }

    [Fact]
    public async Task PublicSocketChurnCannotTerminateRcRegistration()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
        using var stop = new CancellationTokenSource();
        async Task Drain()
        {
            try { while (true) { using var socket = await target.AcceptTcpClientAsync(stop.Token); } }
            catch (OperationCanceledException) { }
        }
        var draining = Drain();
        await using var relay = await TestRelay.Start();
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = RelayAgent.RunAsync(relay.Address, ((IPEndPoint)target.LocalEndpoint).Port, (_, p) => ready.TrySetResult(p), stop.Token);
        try
        {
            int port = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int round=0; round<200 && !agent.IsCompleted; round++)
            {
                await Task.WhenAll(Enumerable.Range(0,8).Select(async _ =>
                {
                    using var tcp = new TcpClient();
                    tcp.LingerState = new LingerOption(true,0);
                    await tcp.ConnectAsync(IPAddress.Loopback, port);
                }));
            }
            if (agent.IsCompleted) await agent;
            Assert.False(agent.IsCompleted);
            using var last = new TcpClient(); await last.ConnectAsync(IPAddress.Loopback, port);
        }
        finally { stop.Cancel(); try { await agent; } catch (OperationCanceledException) { } await draining; }
    }

    [Fact]
    public async Task BackpressuredControlResponsesCannotPreventHeartbeatExpiry()
    {
        await using var relay = await TestRelay.Start(rangeSize: 0);
        using var channel = relay.Address.CreateChannel();
        var client = new WindowsPowerShellRc.Protocol.Relay.RelayClient(channel);
        using var call = client.Register(relay.Address.Headers);
        Assert.True(await call.ResponseStream.MoveNext(default)); int port = call.ResponseStream.Current.Port;
        int sent = 0;
        async Task Flood()
        {
            try
            {
                for (int i=0; i<500000; i++)
                {
                    await call.RequestStream.WriteAsync(new() { Heartbeat = true });
                    Interlocked.Increment(ref sent);
                }
            }
            catch (Exception ex) when (ex is RpcException or IOException or InvalidOperationException) { }
        }
        var flood = Flood();
        // Do not consume heartbeat responses. Detect actual HTTP/2 backpressure.
        int last = -1;
        for (int i=0; i<200; i++)
        {
            await Task.Delay(100);
            int now = Volatile.Read(ref sent);
            if (now == last && now > 1000) break;
            last = now;
        }
        Assert.False(flood.IsCompleted);
        await Task.Delay(TimeSpan.FromSeconds(21));
        using var replacement = client.Register(relay.Address.Headers);
        try
        {
            Assert.True(await replacement.ResponseStream.MoveNext(default).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(port, replacement.ResponseStream.Current.Port);
        }
        finally { call.Dispose(); await flood.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task RealTlsDiagnosticsPassThroughRelayAndClientReconnectKeepsTheSamePort()
    {
        await using var rc = await TestGrpcServer.Start();
        await using var relay = await TestRelay.Start();
        using var stop = new CancellationTokenSource();
        var allocated = new TaskCompletionSource<ConnectionAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = RelayAgent.RunAsync(relay.Address, rc.Address.Port, (host, port) =>
            allocated.TrySetResult(ConnectionAddress.Parse(rc.Address.ToConnectionString().Replace($"@127.0.0.1:{rc.Address.Port}", $"@{host}:{port}"))), stop.Token);
        var address = await allocated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var invalid = ConnectionAddress.Parse(address.ToConnectionString().Replace(rc.Identity.Key.Value, new string('0',64)));
            using (var bad = invalid.CreateChannel())
                Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(() => DiagnosticSession.OpenAsync(new(bad), invalid.Headers, default))).StatusCode);
            string pin = rc.Identity.Certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256).ToLowerInvariant();
            var untrusted = ConnectionAddress.Parse(address.ToConnectionString().Replace(pin, new string('0',64)));
            using (var bad = untrusted.CreateChannel())
                await Assert.ThrowsAsync<RpcException>(() => DiagnosticSession.OpenAsync(new(bad), address.Headers, default));
            using (var channel = address.CreateChannel())
            {
                var client = new WindowsPowerShellRc.Protocol.Diagnostics.DiagnosticsClient(channel);
                using var session = await DiagnosticSession.OpenAsync(client, address.Headers, default);
                var info = await client.GetInfoAsync(new(), session.Headers);
                Assert.Equal("7.test", info.EngineVersion);
                await Assert.ThrowsAsync<RpcException>(() => DiagnosticSession.OpenAsync(client, address.Headers, default));
            }
            for (int i=0; ; i++)
            {
                using var channel = address.CreateChannel();
                try { using var session = await DiagnosticSession.OpenAsync(new(channel), address.Headers, default); break; }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists && i < 100) { await Task.Delay(10); }
            }
            Assert.False(agent.IsCompleted);
        }
        finally { stop.Cancel(); try { await agent; } catch (OperationCanceledException) { } }
    }
}

internal sealed class TestRelay : IAsyncDisposable
{
    private readonly ServerIdentity identity = new();
    private Microsoft.AspNetCore.Builder.WebApplication app = null!;
    public ConnectionAddress Address { get; private set; } = null!;
    public static async Task<TestRelay> Start(int rangeSize = 20)
    {
        var result = new TestRelay();
        // Reserve a candidate range only for the fixture; production allocation itself binds atomically.
        using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        result.app = RelayHost.Build(new(Port: 0, Bind: "127.0.0.1", AdvertiseHost: "127.0.0.1", RangeStart: port, RangeEnd: Math.Min(65535, port + rangeSize)), result.identity);
        await result.app.StartAsync();
        result.Address = result.identity.Address("127.0.0.1", new Uri(result.app.Urls.Single()).Port);
        return result;
    }
    public async ValueTask DisposeAsync() { await app.DisposeAsync(); identity.Dispose(); }
}
