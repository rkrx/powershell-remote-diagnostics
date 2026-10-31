using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WindowsPowerShellRc.Protocol;
using WindowsPowerShellRc.Transport;
using Grpc.Net.Client;

namespace WindowsPowerShellRc.Tests;
internal sealed class ServerProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task pump;
    private readonly Task<string> errors;
    private DiagnosticSession? admission;
    private GrpcChannel channel = null!;
    public ConcurrentQueue<string> ConsoleLines { get; } = new();
    public ConnectionAddress Address { get; private set; } = null!;
    public Diagnostics.DiagnosticsClient Client { get; private set; } = null!;
    public string PackageDirectory { get; }
    private readonly TaskCompletionSource<string> addressReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ServerProcess(int port)
    {
        string? executable = Environment.GetEnvironmentVariable("RC_TEST_SERVER_EXECUTABLE");
        string assembly = typeof(WindowsPowerShellRc.PowerShellEngine).Assembly.Location;
        PackageDirectory = Path.GetDirectoryName(executable ?? assembly)!;
        var start = new ProcessStartInfo(executable ?? "dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath() };
        start.Environment["PSModulePath"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        if (executable is null) start.ArgumentList.Add(assembly);
        foreach (string arg in new[] { "--auto", "--test", "--bind", "127.0.0.1", "--port", port.ToString() }) start.ArgumentList.Add(arg);
        process = Process.Start(start)!; errors = process.StandardError.ReadToEndAsync();
        pump = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("Connection: ")) addressReady.TrySetResult(line[12..]);
                else ConsoleLines.Enqueue(line); // Secret connection lines are never included in test diagnostics.
            }
            addressReady.TrySetException(new InvalidOperationException("Server exited before reporting a connection string."));
        });
    }
    public static async Task<ServerProcess> Start()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var result = new ServerProcess(port);
        try
        {
            result.Address = ConnectionAddress.Parse(await result.addressReady.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            result.channel = result.Address.CreateChannel(); result.Client = new(result.channel); return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }
    public async Task<ExecutionReply> Execute(string script)
    {
        var request = new ExecuteRequest { Reason = "Synthetic package verification" }; request.Commands.Add(script);
        admission ??= await DiagnosticSession.OpenAsync(Client, Address.Headers, default);
        return await Client.ExecuteAsync(request, admission.Headers, DateTime.UtcNow.AddSeconds(25));
    }
    public async ValueTask DisposeAsync()
    {
        admission?.Dispose();
        channel?.Dispose();
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(); await pump; await errors; process.Dispose();
    }
}
