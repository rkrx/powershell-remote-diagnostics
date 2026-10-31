using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace WindowsPowerShellRc;

public sealed record ServerState(CliOptions Options, string Declaration, string EngineVersion, ServerIdentity Identity, ExecutionQueue Queue, TimeSpan Wait);
public static class GrpcApi
{
    public static WebApplication Build(CliOptions options, string declaration, ServerIdentity identity, ExecutionQueue queue,
        string engineVersion, Action<WebApplicationBuilder>? configure = null, TimeSpan? wait = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new ServerState(options, declaration, engineVersion, identity, queue, wait ?? TimeSpan.FromSeconds(15)));
        builder.Services.AddSingleton(new ClientSessions(log: Console.WriteLine, mode: options.Relay is null ? "direct" : "relay"));
        builder.Services.AddGrpc(o => { o.MaxReceiveMessageSize = 1024 * 1024; o.MaxSendMessageSize = 4 * 1024 * 1024; o.Interceptors.Add<AccessInterceptor>(); });
        if (configure is not null) configure(builder);
        else builder.WebHost.ConfigureKestrel(k =>
        {
            foreach (var address in options.ResolveAddresses())
                k.Listen(address, options.Port, l => { l.Protocols = HttpProtocols.Http2; l.UseHttps(identity.Certificate); });
        });
        var app = builder.Build();
        app.MapGrpcService<DiagnosticsService>();
        return app;
    }
}
public sealed class AccessInterceptor(ServerState state, ClientSessions sessions) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest,TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest,TResponse> continuation)
    {
        Authenticate(context);
        sessions.Validate(context.RequestHeaders);
        try { return await continuation(request, context); }
        catch (ArgumentException ex) { throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)); }
        catch (ObjectDisposedException) { throw new RpcException(new Status(StatusCode.NotFound, "Unknown or evicted execution.")); }
        catch (IOException) { throw new RpcException(new Status(StatusCode.Internal, "Output storage is unavailable.")); }
    }
    public override Task DuplexStreamingServerHandler<TRequest,TResponse>(IAsyncStreamReader<TRequest> request, IServerStreamWriter<TResponse> response, ServerCallContext context, DuplexStreamingServerMethod<TRequest,TResponse> continuation)
    { Authenticate(context); return continuation(request, response, context); }
    private void Authenticate(ServerCallContext context)
    {
        var auth = context.RequestHeaders.Where(h => h.Key == "authorization").ToArray();
        if (auth.Length != 1 || !state.Identity.Key.IsValid(auth[0].Value))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "A valid connection token is required."));
    }
}
