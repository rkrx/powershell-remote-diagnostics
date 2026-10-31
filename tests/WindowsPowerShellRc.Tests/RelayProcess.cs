using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WindowsPowerShellRc.Transport;
namespace WindowsPowerShellRc.Tests;

internal sealed class RelayProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task pump;
    private readonly Task<string> errors;
    private readonly TaskCompletionSource<ConnectionAddress> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConnectionAddress Address { get; private set; } = null!;
    private RelayProcess()
    {
        int FreePort() { using var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }
        int port = FreePort(), first;
        do { first = FreePort(); } while (port >= first && port <= Math.Min(65535, first+20));
        string? executable = Environment.GetEnvironmentVariable("RC_TEST_SERVER_EXECUTABLE");
        var start = new ProcessStartInfo(executable ?? "dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath() };
        if (executable is null) start.ArgumentList.Add(typeof(PowerShellEngine).Assembly.Location);
        foreach (var arg in new[] { "relay", "--bind", "127.0.0.1", "--advertise-host", "127.0.0.1", "--port", port.ToString(), "--port-range-start", first.ToString(), "--port-range-end", Math.Min(65535, first+20).ToString() }) start.ArgumentList.Add(arg);
        process = Process.Start(start)!; errors = process.StandardError.ReadToEndAsync();
        pump = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                if (line.StartsWith("Registration: ")) ready.TrySetResult(ConnectionAddress.Parse(line[14..]));
            ready.TrySetException(new InvalidOperationException("Relay exited before registration became available."));
        });
    }
    public static async Task<RelayProcess> Start()
    {
        var result = new RelayProcess();
        try { result.Address = await result.ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); return result; }
        catch { await result.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(); await pump; await errors; process.Dispose();
    }
}
