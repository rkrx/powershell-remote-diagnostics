using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace WindowsPowerShellRc.Tests;

public class PackagedHostTests
{
    [Fact]
    public async Task BundledModulesWorkWithoutInheritedPowerShellInstallationAndOutputIsMirrored()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        string? executable = Environment.GetEnvironmentVariable("RC_TEST_EXECUTABLE");
        string assembly = typeof(WindowsPowerShellRc.PowerShellEngine).Assembly.Location;
        string packageDirectory = Path.GetDirectoryName(executable ?? assembly)!;
        var start = new ProcessStartInfo(executable ?? "dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath()
        };
        start.Environment["PSModulePath"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        if (executable is null) start.ArgumentList.Add(assembly);
        foreach (string arg in new[] { "--auto", "--test", "--bind", "127.0.0.1", "--port", port.ToString() })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var console = new List<string>();
        var tokenReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                console.Add(line);
                if (line.StartsWith("Bearer token: ")) tokenReady.TrySetResult(line[14..]);
            }
        });
        try
        {
            string token = await tokenReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            string script = "'bundled-engine-ready'; (Get-Module Microsoft.PowerShell.Utility).Path; " +
                "(Get-Item -LiteralPath $PSHOME).Exists; Write-Warning 'mirror-warning'; Write-Host 'mirror-host'; " +
                "Write-Output ([string][char]27 + '[31mcolored')";
            var response = await client.PostAsJsonAsync("/executions", new { commands = new[] { script }, reason = "Verify bundled modules and console output" });
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            string output = string.Join("\n", body.GetProperty("output").GetProperty("lines")
                .EnumerateArray().Select(line => line.GetProperty("text").GetString()));
            Assert.True(body.GetProperty("status").GetString() == "completed", output);
            Assert.Contains("bundled-engine-ready", output);
            Assert.True(output.Contains(packageDirectory, StringComparison.OrdinalIgnoreCase), output);
            Assert.Contains("True", output);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await pump;
            await errors;
        }
        // Exact lines distinguish actual output from the displayed script request.
        Assert.Contains("bundled-engine-ready", console);
        Assert.Contains("mirror-warning", console);
        Assert.Contains("mirror-host", console);
        Assert.Contains("\\u{1B}[31mcolored", console);
        Assert.DoesNotContain(console, line => line.Contains('\u001b'));
    }
}
