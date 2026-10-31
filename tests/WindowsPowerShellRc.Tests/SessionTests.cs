using System.Text.Json;
using Grpc.Core;
using WindowsPowerShellRc.Client;
using WindowsPowerShellRc.Protocol;
namespace WindowsPowerShellRc.Tests;

public class SessionTests
{
    [Fact]
    public async Task OnlyOneConcurrentClientWinsAndDisconnectPermitsReplacement()
    {
        await using var server = await TestGrpcServer.Start();
        using var a = new ConnectionManager(new NoPrompt());
        using var b = new ConnectionManager(new NoPrompt());
        async Task<string?> Connect(ConnectionManager manager)
        {
            try { return await manager.ConnectAsync(server.Address.ToConnectionString(), default); }
            catch (ClientOperationException ex) { Assert.Contains("AlreadyExists", ex.Message); return null; }
        }
        var results = await Task.WhenAll(Connect(a), Connect(b));
        Assert.Single(results, r => r is not null);
        var winner = results[0] is not null ? a : b;
        var loser = results[0] is not null ? b : a;
        var id = JsonDocument.Parse(results.First(r => r is not null)!).RootElement.GetProperty("connectionId").GetString()!;
        winner.Disconnect(id);
        // Close-session completion can race the first retry, but cannot reserve a permanent slot.
        for (int i = 0; ; i++)
        {
            try { await loser.ConnectAsync(server.Address.ToConnectionString(), default); break; }
            catch (ClientOperationException) when (i < 100) { await Task.Delay(10); }
        }
    }

    [Fact]
    public async Task BearerAloneDoesNotAuthorizeDiagnosticOperations()
    {
        await using var server = await TestGrpcServer.Start();
        using var channel = server.Address.CreateChannel();
        var client = new Diagnostics.DiagnosticsClient(channel);
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await client.GetInfoAsync(new(), server.Address.Headers));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public void ExpiredOwnerAndLateCleanupCannotReleaseReplacementOrLeakCredentials()
    {
        var clock = new ManualClock(); var logs = new List<string>();
        var registry = new WindowsPowerShellRc.ClientSessions(clock, log: logs.Add);
        var first = registry.Acquire();
        var headers = new Metadata { { "x-rc-session", first.Credential } };
        registry.Validate(headers);
        clock.Advance(TimeSpan.FromSeconds(20));
        var replacement = registry.Acquire();
        registry.Release(first, "late cleanup");
        Assert.Equal(StatusCode.AlreadyExists, Assert.Throws<RpcException>(() => registry.Acquire()).StatusCode);
        Assert.Equal(StatusCode.FailedPrecondition, Assert.Throws<RpcException>(() => registry.Validate(headers)).StatusCode);
        registry.Validate(new Metadata { { "x-rc-session", replacement.Credential } });
        Assert.Contains(logs, l => l.Contains("heartbeat expired"));
        Assert.DoesNotContain(logs, l => l.Contains(first.Credential) || l.Contains(replacement.Credential));
        registry.Release(replacement, "closed");
        registry.Acquire();
    }

    [Fact]
    public async Task EndedServerStreamMakesRetainedClientConnectionUnavailable()
    {
        var server = await TestGrpcServer.Start();
        using var manager = new ConnectionManager(new NoPrompt());
        var result = await manager.ConnectAsync(server.Address.ToConnectionString(), default);
        string id = JsonDocument.Parse(result).RootElement.GetProperty("connectionId").GetString()!;
        await server.DisposeAsync();
        var error = await Assert.ThrowsAsync<ClientOperationException>(() => manager.InvokeAsync(id,
            async s => await s.Client.GetInfoAsync(new(), s.Headers, DateTime.UtcNow.AddSeconds(2))));
        Assert.Contains("session", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
    }

    private sealed class NoPrompt : IConnectionPrompt
    {
        public Task<string?> RequestAsync(CancellationToken t) => throw new InvalidOperationException("Unexpected prompt");
    }
}
