using System.Diagnostics;
using System.Text.Json;
using WindowsPowerShellRc.Client;
namespace WindowsPowerShellRc.Tests;
public class McpProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StdioClientConnectsWithoutGuiAndExecutesThroughPinnedGrpc(bool throughRelay)
    {
        await using var relay = throughRelay ? await RelayProcess.Start() : null;
        await using var server = await ServerProcess.Start(relay?.Address.ToConnectionString());
        string? executable = Environment.GetEnvironmentVariable("RC_TEST_CLIENT_EXECUTABLE");
        var start = new ProcessStartInfo(executable ?? "dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath() };
        if (executable is null)
        {
            string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            start.ArgumentList.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../../src/WindowsPowerShellRc.Client/bin", configuration, "net10.0/WindowsPowerShellRc.Client.dll")));
        }
        using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync();
        async Task<JsonElement> Send(int id, string method, object parameters)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            await process.StandardInput.FlushAsync();
            while (true)
            {
                string line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) ?? throw new IOException("MCP client exited.");
                var json = JsonDocument.Parse(line).RootElement.Clone(); // Any stdout noise breaks this assertion.
                if (json.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id) return json;
            }
        }
        try
        {
            var init = await Send(1, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "rc-tests", version = "1" } });
            Assert.True(init.TryGetProperty("result", out _));
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.FlushAsync();
            var list = await Send(2, "tools/list", new { });
            Assert.Contains(list.GetProperty("result").GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "connect");
            var connected = await Send(3, "tools/call", new { name = "connect", arguments = new { connectionString = server.Address.ToConnectionString() } });
            var content = connected.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.DoesNotContain(server.Address.ToConnectionString(), content);
            string connectionId = JsonDocument.Parse(content).RootElement.GetProperty("connectionId").GetString()!;
            var executed = await Send(4, "tools/call", new { name = "execute", arguments = new { connectionId, commands = new[] { "$mcpValue = 42", "$mcpValue" }, reason = "Verify persistent remote execution" } });
            var result = JsonDocument.Parse(executed.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!).RootElement;
            Assert.Equal("completed", result.GetProperty("status").GetString());
            Assert.True(Guid.TryParse(result.GetProperty("id").GetString(), out _));
            Assert.Contains("42", result.GetProperty("output").ToString());
            using (var otherChannel = server.Address.CreateChannel())
            {
                var busy = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => WindowsPowerShellRc.Transport.DiagnosticSession.OpenAsync(new(otherChannel), server.Address.Headers, default));
                Assert.Equal(Grpc.Core.StatusCode.AlreadyExists, busy.StatusCode);
            }
            string executionId = result.GetProperty("id").GetString()!;
            var page = await Send(8, "tools/call", new { name = "read_output", arguments = new { connectionId, id = executionId } });
            Assert.Contains("42", page.ToString());
            var search = await Send(9, "tools/call", new { name = "search_output", arguments = new { connectionId, id = executionId, q = "42" } });
            Assert.Contains("42", search.ToString());
            var cancelled = await Send(10, "tools/call", new { name = "cancel_execution", arguments = new { connectionId, id = executionId } });
            Assert.Contains("completed", cancelled.ToString());
            var bad = await Send(5, "tools/call", new { name = "connect", arguments = new { connectionString = "grpc://sensitive-input@host:8080" } });
            Assert.True(bad.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.DoesNotContain("sensitive-input", bad.ToString());
            Assert.Contains("Invalid connection URL", bad.ToString());
            var missing = await Send(6, "tools/call", new { name = "execution_status", arguments = new { connectionId, id = Guid.NewGuid().ToString() } });
            Assert.True(missing.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Contains("NotFound", missing.ToString());
            Assert.Contains("do not blindly retry", missing.ToString());
            var unknown = await Send(7, "tools/call", new { name = "server_info", arguments = new { connectionId = Guid.NewGuid().ToString() } });
            Assert.Contains("Unknown connection ID", unknown.ToString());
            await Send(11, "tools/call", new { name = "disconnect", arguments = new { connectionId } });
            JsonElement reconnected = default;
            for (int attempt=0; attempt<100; attempt++)
            {
                reconnected = await Send(12 + attempt, "tools/call", new { name = "connect", arguments = new { connectionString = server.Address.ToConnectionString() } });
                if (!reconnected.GetProperty("result").TryGetProperty("isError", out var error) || !error.GetBoolean()) break;
                await Task.Delay(10);
            }
            var reconnectText = reconnected.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.True(JsonDocument.Parse(reconnectText).RootElement.TryGetProperty("connectionId", out _));
            Assert.Contains(server.ConsoleLines, l => l.Contains("Client connected; session"));
            Assert.DoesNotContain(server.ConsoleLines, l => l.Contains("Bearer ") || l.Contains(server.Address.ToConnectionString()));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); await stderr;
        }
    }
}
