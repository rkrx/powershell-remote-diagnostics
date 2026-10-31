using WindowsPowerShellRc;

try
{
    var options = CliOptions.Parse(args);
    if (options.Help) { Console.WriteLine(CliOptions.Usage); return 0; }
    if (options.Version) { Console.WriteLine("Windows-Powershell-RC Server 0.2.0"); return 0; }
    if (!options.Auto && (Console.IsInputRedirected || Console.IsOutputRedirected))
        throw new ArgumentException("Manual mode requires an interactive console. Use --auto explicitly for unattended operation.");
    string declaration = options.ReadDeclaration();
    var addresses = options.ResolveAddresses();
    using var engine = new PowerShellEngine();
    using var store = new OutputStore();
    var terminal = new ConsoleTerminal();
    await using var queue = new ExecutionQueue(engine, new ConsoleApproval(options.Auto, terminal), store, terminal.Output);
    using var identity = new ServerIdentity();
    await using var app = GrpcApi.Build(options, declaration, identity, queue, engine.Version);
    await app.StartAsync();
    Console.WriteLine($"Windows-Powershell-RC Server 0.2.0 — embedded PowerShell {engine.Version}");

    Console.WriteLine($"Environment: {(options.Test ? "TEST" : "PRODUCTION")}; approval: {(options.Auto ? "AUTO" : "MANUAL")}");
    Console.WriteLine($"Declaration:\n{ConsoleApproval.Escape(declaration)}");
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
