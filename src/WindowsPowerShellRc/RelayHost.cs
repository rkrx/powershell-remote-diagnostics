using Grpc.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using WindowsPowerShellRc.Protocol;
namespace WindowsPowerShellRc;

public static class RelayHost
{
    public static WebApplication Build(RelayOptions options, ServerIdentity identity)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(options); builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton<RelayRegistry>();
        builder.Services.AddGrpc(o => { o.MaxReceiveMessageSize = 40 * 1024; o.MaxSendMessageSize = 40 * 1024; });
        builder.WebHost.ConfigureKestrel(k =>
        {
            foreach (var address in new CliOptions(Bind: options.Bind).ResolveAddresses())
                k.Listen(address, options.Port, l => { l.Protocols = HttpProtocols.Http2; l.UseHttps(identity.Certificate); });
        });
        var app = builder.Build(); app.MapGrpcService<RelayService>(); return app;
    }
}

public sealed class RelayService(RelayOptions options, ServerIdentity identity, RelayRegistry registry, IHostApplicationLifetime lifetime) : Relay.RelayBase
{
    private void Authenticate(ServerCallContext context)
    {
        var headers = context.RequestHeaders.Where(h => h.Key == "authorization").ToArray();
        if (headers.Length != 1 || !identity.Key.IsValid(headers[0].Value))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "A valid relay registration token is required."));
    }
    public override async Task Register(IAsyncStreamReader<RelayControl> requests, IServerStreamWriter<RelayControl> responses, ServerCallContext context)
    {
        Authenticate(context);
        var lease = registry.Allocate();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping, deadline.Token);
        Task sending = Task.CompletedTask, receiving = Task.CompletedTask;
        try
        {
            await responses.WriteAsync(new() { Lease = lease.Credential, Host = options.PublicHost, Port = lease.Port }, stop.Token);
            async Task Send()
            {
                await foreach (var message in lease.Messages.Reader.ReadAllAsync(stop.Token))
                    await responses.WriteAsync(message, stop.Token);
            }
            async Task Receive()
            {
                while (await requests.MoveNext(stop.Token))
                {
                    var frame = requests.Current;
                    if (!frame.Heartbeat || frame.Lease.Length != 0 || frame.Host.Length != 0 || frame.Port != 0 || frame.ConnectionId.Length != 0)
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "Expected relay heartbeat."));
                    deadline.CancelAfter(TimeSpan.FromSeconds(20));
                    await lease.Messages.Writer.WriteAsync(new() { Heartbeat = true }, stop.Token);
                }
            }
            sending = Send(); receiving = Receive();
            await await Task.WhenAny(sending, receiving);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested) { throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Relay registration heartbeat expired.")); }
        finally
        {
            stop.Cancel();
            await registry.ReleaseAsync(lease);
            try { await Task.WhenAll(sending, receiving); } catch (Exception ex) when (ex is RpcException or OperationCanceledException or IOException or TimeoutException) { }
        }
    }
    public override async Task Tunnel(IAsyncStreamReader<TunnelFrame> requests, IServerStreamWriter<TunnelFrame> responses, ServerCallContext context)
    {
        Authenticate(context);
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        opening.CancelAfter(TimeSpan.FromSeconds(10));
        if (!await requests.MoveNext(opening.Token) || requests.Current.ConnectionId.Length != 36 || requests.Current.Data.Length != 0 || requests.Current.Eof)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Expected relay connection claim."));
        var keys = context.RequestHeaders.Where(h => h.Key == "x-relay-lease").ToArray();
        if (keys.Length != 1) throw new RpcException(new Status(StatusCode.Unauthenticated, "Relay lease is required."));
        var (lease, pending) = registry.Claim(keys[0].Value, requests.Current.ConnectionId);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping, lease.Stop.Token);
        try { await TunnelPump.RunAsync(pending.Socket, requests, (frame, ct) => responses.WriteAsync(frame, ct), stop.Token); }
        finally { pending.Finished.TrySetResult(); }
    }
}
