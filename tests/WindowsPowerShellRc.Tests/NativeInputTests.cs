using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace WindowsPowerShellRc.Tests;

public class NativeInputTests
{
    [Fact]
    public async Task NativeProcessSeesEofEvenWhenParentStdinPipeRemainsOpen()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start(); int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(WindowsPowerShellRc.PowerShellEngine).Assembly.Location);
        foreach (var arg in new[] { "--auto", "--test", "--bind", "127.0.0.1", "--port", port.ToString() }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            string? token = null;
            while (token is null)
            {
                string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.NotNull(line);
                if (line.StartsWith("Bearer token: ")) token = line[14..];
            }
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            string command = OperatingSystem.IsWindows() ?
                "cmd /d /c 'set /p rcInput= & echo EOF'" : "/bin/sh -c 'read answer; printf EOF'";
            var response = await client.PostAsJsonAsync("/executions", new { commands = new[] { command }, reason = "Test native EOF without console input" });
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("outputReady").GetBoolean());
            Assert.Contains("EOF", body.GetProperty("output").ToString());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}
