using WindowsPowerShellRc;

try
{
    var options = CliOptions.Parse(args);
    if (options.Help) { Console.WriteLine(CliOptions.Usage); return 0; }
    if (options.Version) { Console.WriteLine("Windows-Powershell-RC 0.1.2"); return 0; }
    if (!options.Auto && (Console.IsInputRedirected || Console.IsOutputRedirected))
        throw new ArgumentException("Manual mode requires an interactive console. Use --auto explicitly for unattended operation.");
    string declaration = options.ReadDeclaration();
    var addresses = options.ResolveAddresses();
    using var engine = new PowerShellEngine();
    using var store = new OutputStore();
    var terminal = new ConsoleTerminal();
    await using var queue = new ExecutionQueue(engine, new ConsoleApproval(options.Auto, terminal), store, terminal.Output);
    var key = new BearerKey();
    await using var app = Api.Build(options, declaration, key, queue, engine.Version, builder =>
        builder.WebHost.ConfigureKestrel(server =>
        {
            foreach (var address in addresses) server.Listen(address, options.Port);
        }));
    await app.StartAsync();
    Console.WriteLine($"Windows-Powershell-RC 0.1.2 — embedded PowerShell {engine.Version}");
    foreach (string url in app.Urls) Console.WriteLine($"HTTP: {url}");
    Console.WriteLine($"Environment: {(options.Test ? "TEST" : "PRODUCTION")}; approval: {(options.Auto ? "AUTO" : "MANUAL")}");
    Console.WriteLine($"Declaration:\n{ConsoleApproval.Escape(declaration)}");
    Console.WriteLine($"Bearer token: {key.Value}");
    Console.WriteLine("HTTP transport. Use a trusted/encrypted network path. Ctrl+C stops the server.");
    await app.WaitForShutdownAsync();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Startup/operation failed: {ex.Message}");
    return 1;
}
