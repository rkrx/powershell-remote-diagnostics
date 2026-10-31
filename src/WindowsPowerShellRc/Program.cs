using WindowsPowerShellRc;

try
{
    if (args.FirstOrDefault() == "relay")
    {
        var relayOptions = RelayOptions.Parse(args[1..]);
        if (relayOptions.Help) { Console.WriteLine(RelayOptions.Usage); return 0; }
        if (relayOptions.Version) { Console.WriteLine("PowerShell RC Relay 0.2.1"); return 0; }
        using var relayIdentity = new ServerIdentity();
        await using var relayApp = RelayHost.Build(relayOptions, relayIdentity);
        await relayApp.StartAsync();
        Console.WriteLine("PowerShell RC Relay — TLS passthrough; no PowerShell engine initialized.");
        Console.WriteLine($"Registration: {relayIdentity.Address(relayOptions.PublicHost.Trim('[', ']'), relayOptions.Port).ToConnectionString()}");
        Console.WriteLine($"Public TCP range: {relayOptions.RangeStart}–{relayOptions.RangeEnd}. Keep the registration string secret.");
        await relayApp.WaitForShutdownAsync();
        return 0;
    }
    var options = CliOptions.Parse(args);
    if (options.Help) { Console.WriteLine(CliOptions.Usage); return 0; }
    if (options.Version) { Console.WriteLine("Windows-Powershell-RC Server 0.2.1"); return 0; }
    if (!options.Auto && (Console.IsInputRedirected || Console.IsOutputRedirected))
        throw new ArgumentException("Manual mode requires an interactive console. Use --auto explicitly for unattended operation.");
    string declaration = options.ReadDeclaration();
    var addresses = options.Relay is null ? options.ResolveAddresses() : [System.Net.IPAddress.Loopback];
    using var engine = new PowerShellEngine();
    using var store = new OutputStore();
    var terminal = new ConsoleTerminal();
    await using var queue = new ExecutionQueue(engine, new ConsoleApproval(options.Auto, terminal), store, terminal.Output);
    using var identity = new ServerIdentity();
    await using var app = GrpcApi.Build(options.Relay is null ? options : options with { Bind = "127.0.0.1", Port = 0 }, declaration, identity, queue, engine.Version);
    await app.StartAsync();
    Console.WriteLine($"Windows-Powershell-RC Server 0.2.1 — embedded PowerShell {engine.Version}");

    Console.WriteLine($"Environment: {(options.Test ? "TEST" : "PRODUCTION")}; approval: {(options.Auto ? "AUTO" : "MANUAL")}");
    Console.WriteLine($"Declaration:\n{ConsoleApproval.Escape(declaration)}");
    if (options.Relay is not null)
    {
        var relay = WindowsPowerShellRc.Transport.ConnectionAddress.Parse(options.Relay);
        int localPort = new Uri(app.Urls.Single()).Port;
        try
        {
            await RelayAgent.RunAsync(relay, localPort,
                (host, port) => Console.WriteLine($"Connection: {identity.Address(host, port).ToConnectionString()}"), app.Lifetime.ApplicationStopping);
        }
        catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested) { }
        catch (Exception ex) when (ex is Grpc.Core.RpcException or IOException or TimeoutException)
        { Console.Error.WriteLine("Relay connection lost or registration failed. Restart the RC to reconnect."); return 1; }
        finally { await app.StopAsync(); }
        return 0;
    }
    foreach (var address in addresses)
    {
        string host = address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any) ? System.Net.Dns.GetHostName() : address.ToString();
        Console.WriteLine($"Connection: {identity.Address(host, options.Port).ToConnectionString()}");
    }
    Console.WriteLine("Encrypted gRPC with certificate pinning. Keep the connection string secret. Ctrl+C stops the server.");
    await app.WaitForShutdownAsync();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Startup/operation failed: {ex.Message}");
    return 1;
}
