using System.Text.Json;
using WindowsPowerShellRc.Client;
namespace WindowsPowerShellRc.Tests;
public class ClientTests
{
    [Fact]
    public async Task ExplicitWorkingUrlNeverPromptsAndMissingUrlPrompts()
    {
        await using var server = await TestGrpcServer.Start();
        var prompt = new RecordingPrompt(server.Address.ToConnectionString());
        using var manager = new ConnectionManager(prompt);
        string first = await manager.ConnectAsync(server.Address.ToConnectionString(), default);
        Assert.Equal(0, prompt.Calls);
        Assert.True(Guid.TryParse(JsonDocument.Parse(first).RootElement.GetProperty("connectionId").GetString(), out _));
        await manager.ConnectAsync(null, default);
        Assert.Equal(1, prompt.Calls);
    }
    [Fact]
    public async Task BadExplicitUrlDoesNotPromptOrLeakTheInput()
    {
        var prompt = new RecordingPrompt(null); using var manager = new ConnectionManager(prompt);
        var error = await Assert.ThrowsAsync<FormatException>(() => manager.ConnectAsync("grpc://sensitive-input@host:8080", default));
        Assert.Equal(0, prompt.Calls); Assert.DoesNotContain("sensitive-input", error.Message);
        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.ConnectAsync(null, default));
    }
    [Fact]
    public async Task FailedCompleteUrlDoesNotPromptOrExposeCredentials()
    {
        await using var server = await TestGrpcServer.Start();
        var prompt = new RecordingPrompt(null); using var manager = new ConnectionManager(prompt);
        string bad = server.Address.ToConnectionString().Replace(server.Identity.Key.Value, new string('0',64));
        var error = await Assert.ThrowsAsync<ClientOperationException>(() => manager.ConnectAsync(bad, default));
        Assert.Equal(0, prompt.Calls); Assert.DoesNotContain(bad, error.Message);
        Assert.DoesNotContain(server.Identity.Key.Value, error.Message);
        await manager.ConnectAsync(server.Address.ToConnectionString(), default);
        Assert.Equal(0, prompt.Calls);
    }

    private sealed class RecordingPrompt(string? value) : IConnectionPrompt
    {
        public int Calls;
        public Task<string?> RequestAsync(CancellationToken t) { Calls++; return Task.FromResult(value); }
    }
}
