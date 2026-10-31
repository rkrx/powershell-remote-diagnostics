using Microsoft.AspNetCore.Hosting;
using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using WindowsPowerShellRc;
using WindowsPowerShellRc.Protocol;
using WindowsPowerShellRc.Transport;

namespace WindowsPowerShellRc.Tests;
public class GrpcTests
{
    [Fact]
    public async Task TlsPinsAndAuthenticationProtectEveryOperation()
    {
        await using var server = await TestGrpcServer.Start();
        using var channel = server.Address.CreateChannel();
        var client = new Diagnostics.DiagnosticsClient(channel);
        var failure = await Assert.ThrowsAsync<RpcException>(async () => await client.GetInfoAsync(new Empty()));
        Assert.Equal(StatusCode.Unauthenticated, failure.StatusCode);
        foreach (var operation in new Func<Task>[] {
            async () => { await client.ExecuteAsync(new()); },
            async () => { await client.GetExecutionAsync(new()); },
            async () => { await client.ReadOutputAsync(new()); },
            async () => { await client.SearchAsync(new()); },
            async () => { await client.CancelAsync(new()); } })
            Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(operation)).StatusCode);
        var info = await client.GetInfoAsync(new Empty(), server.Address.Headers);
        Assert.Contains("CUSTOM POLICY", info.Instructions);
        Assert.Contains(Environment.MachineName, info.Instructions);
        Assert.DoesNotContain(server.Identity.Key.Value, info.Instructions);
        Assert.True(Guid.TryParse(info.SessionId, out _));
        var wrong = ConnectionAddress.Parse(server.Address.ToConnectionString().Replace(server.Identity.Key.Value, new string('0',64)));
        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await client.GetInfoAsync(new(), wrong.Headers))).StatusCode);
        var url = server.Address.ToConnectionString();
        var badPin = ConnectionAddress.Parse(url[..url.LastIndexOf('@')].Replace(url.Split('.')[2].Split('@')[0], new string('0',64)) + url[url.LastIndexOf('@')..]);
        using var badChannel = badPin.CreateChannel();
        var pinFailure = await Assert.ThrowsAsync<RpcException>(async () => await new Diagnostics.DiagnosticsClient(badChannel).GetInfoAsync(new(), badPin.Headers));
        Assert.True(pinFailure.StatusCode is StatusCode.Unavailable or StatusCode.Internal);
    }
    [Fact]
    public async Task PendingUuidPollingOutputSearchAndCancellationPreserveSemantics()
    {
        using var engine = new FakeEngine { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var server = await TestGrpcServer.Start(engine);
        using var channel = server.Address.CreateChannel();
        var client = new Diagnostics.DiagnosticsClient(channel); var headers = server.Address.Headers;
        var request = new ExecuteRequest { Reason = "Check persistent script" }; request.Commands.Add(["$x=1", "$x"]);
        var pending = await client.ExecuteAsync(request, headers);
        Assert.False(pending.OutputReady); Assert.True(Guid.TryParse(pending.Id, out var id));
        Assert.Equal("running", pending.Status);
        Assert.Equal(StatusCode.FailedPrecondition, (await Assert.ThrowsAsync<RpcException>(async () => await client.ReadOutputAsync(new() { Id = pending.Id }, headers))).StatusCode);
        engine.Release.SetResult(); await server.Queue.Find(id)!.Completion;
        var done = await client.GetExecutionAsync(new() { Id = pending.Id }, headers);
        Assert.Equal(pending.Id, done.Id); Assert.True(done.OutputReady);
        Assert.Equal("$x=1\n$x", string.Join("\n", done.Output.Lines.Select(l => l.Text)));
        Assert.Equal(2, (await client.ReadOutputAsync(new() { Id = done.Id }, headers)).Output.TotalLines);
        Assert.Single((await client.SearchAsync(new() { Id = done.Id, Q = "=1" }, headers)).Matches);
        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(async () => await client.ReadOutputAsync(new() { Id = done.Id, LineCount = 0 }, headers))).StatusCode);
        Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(async () => await client.GetExecutionAsync(new() { Id = Guid.NewGuid().ToString() }, headers))).StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(async () => await client.ExecuteAsync(new(), headers))).StatusCode);
        engine.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = await client.ExecuteAsync(request, headers);
        Assert.Equal("cancelled", (await client.CancelAsync(new() { Id = next.Id }, headers)).Status is "cancelled" ? "cancelled" : (await WaitForStatus(client, headers, next.Id)).Status);
    }
    private static async Task<ExecutionReply> WaitForStatus(Diagnostics.DiagnosticsClient c, Metadata h, string id)
    {
        for (int i=0;i<100;i++) { var r = await c.GetExecutionAsync(new() { Id=id },h); if(r.OutputReady) return r; await Task.Delay(10); }
        throw new TimeoutException();
    }
}
internal sealed class TestGrpcServer : IAsyncDisposable
{
    public ServerIdentity Identity { get; } = new();
    private readonly OutputStore store = new();
    private Microsoft.AspNetCore.Builder.WebApplication app = null!;
    public ExecutionQueue Queue { get; private set; } = null!;
    public ConnectionAddress Address { get; private set; } = null!;
    public static async Task<TestGrpcServer> Start(IPowerShellEngine? engine = null)
    {
        var server = new TestGrpcServer();
        server.Queue = new ExecutionQueue(engine ?? new FakeEngine(), new AutoApproval(), server.store);
        server.app = GrpcApi.Build(new(Auto: true), "CUSTOM POLICY", server.Identity, server.Queue, "7.test", b =>
            b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => { l.Protocols = HttpProtocols.Http2; l.UseHttps(server.Identity.Certificate); })), TimeSpan.FromMilliseconds(30));
        await server.app.StartAsync();
        server.Address = server.Identity.Address("127.0.0.1", new Uri(server.app.Urls.Single()).Port);
        return server;
    }
    public async ValueTask DisposeAsync() { await app.DisposeAsync(); await Queue.DisposeAsync(); store.Dispose(); Identity.Dispose(); }
}
