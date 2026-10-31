using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WindowsPowerShellRc.Client;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--version") { Console.WriteLine("Windows-Powershell-RC Client 0.2.0"); return 0; }
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("WindowsPowerShellRc.Client: MCP over stdio. Configure as an MCP server with no arguments. Use connect(connectionString) for direct connection, or connect() for the optional desktop dialog."); return 0;
        }
        if (args.Length == 2 && args[0] == "--connection-dialog") return ConnectionDialog.Run(args[1]);
        if (args.Length != 0) { Console.Error.WriteLine("Unknown client option. Use --help."); return 1; }
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
            // stdout is exclusively MCP. Do not register console log providers or log tool arguments.
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IConnectionPrompt, ConnectionDialog>();
            builder.Services.AddSingleton<ConnectionManager>();
            builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "powershell-remote-diagnostics", Version = "0.2.0" })
                .WithStdioServerTransport().WithTools<ClientTools>();
            using var host = builder.Build(); host.RunAsync().GetAwaiter().GetResult(); return 0;
        }
        catch { Console.Error.WriteLine("MCP client failed. Check client configuration; connection secrets are omitted from diagnostics."); return 1; }
    }
}
