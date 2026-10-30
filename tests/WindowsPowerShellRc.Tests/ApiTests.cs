using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using WindowsPowerShellRc;

namespace WindowsPowerShellRc.Tests;

public class ApiTests
{
    [Fact]
    public async Task EveryEndpointRequiresToken()
    {
        using var store = new OutputStore();
        using var engine = new FakeEngine();
        await using var queue = new ExecutionQueue(engine, new AutoApproval(), store);
        var key = new BearerKey();
        await using var app = Api.Build(new(Auto: true), "read only", key, queue, engine.Version, b => b.WebHost.UseTestServer());
        await app.StartAsync();
        var client = app.GetTestClient();
        var id = Guid.NewGuid();
        foreach (var (method, path) in new[] { ("GET", "/"), ("POST", "/executions"), ("GET", $"/executions/{id}"), ("GET", $"/executions/{id}/output"), ("GET", $"/executions/{id}/search?q=x"), ("POST", $"/executions/{id}/cancel") })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(new(new HttpMethod(method), path))).StatusCode);
    }

    [Fact]
    public async Task PendingResponseAndPollingKeepUuidAndDisconnectDoesNotCancelJob()
    {
        using var store = new OutputStore();
        using var engine = new FakeEngine { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var queue = new ExecutionQueue(engine, new AutoApproval(), store);
        var key = new BearerKey();
        await using var app = Api.Build(new(Auto: true), "read only", key, queue, engine.Version,
            b => b.WebHost.UseTestServer(), TimeSpan.FromMilliseconds(30));
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Value);
        var response = await client.PostAsJsonAsync("/executions", new { commands = new[] { "$x=1", "$x" }, reason = "check" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var pending = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = pending.GetProperty("id").GetGuid();
        Assert.Equal($"/executions/{id}", response.Headers.Location!.ToString());
        Assert.Equal("2", response.Headers.RetryAfter!.ToString());
        engine.Release.SetResult();
        await queue.Find(id)!.Completion;
        var ready = await client.GetFromJsonAsync<JsonElement>($"/executions/{id}");
        Assert.Equal(id, ready.GetProperty("id").GetGuid());
        Assert.True(ready.GetProperty("outputReady").GetBoolean());
        Assert.Equal("$x=1\n$x", string.Join("\n", ready.GetProperty("output").GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString())));

        engine.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var post = client.PostAsJsonAsync("/executions", new { commands = new[] { "still runs" }, reason = "disconnect" }, cancel.Token);
        while (engine.Calls < 2) await Task.Delay(5);
        cancel.Cancel();
        try { await post; } catch (OperationCanceledException) { }
        engine.Release.SetResult();
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task BadInputsAreProblemsAndGuideExplainsRules()
    {
        using var store = new OutputStore(); using var engine = new FakeEngine();
        await using var queue = new ExecutionQueue(engine, new AutoApproval(), store);
        var key = new BearerKey();
        await using var app = Api.Build(new(Auto: true), "CUSTOM POLICY", key, queue, engine.Version, b => b.WebHost.UseTestServer());
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", key.Value);
        var guide = await client.GetStringAsync("/");
        Assert.Contains("CUSTOM POLICY", guide);
        Assert.Contains("production", guide);
        Assert.Contains("## Runtime environment", guide);
        Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.OSDescription, guide);
        Assert.Contains(Environment.OSVersion.Version.ToString(), guide);
        Assert.Contains(Environment.MachineName, guide);
        Assert.Contains(Environment.UserName, guide);
        Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, guide);
        Assert.Contains($"OS architecture: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}", guide);
        Assert.Contains($"Process architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}", guide);
        Assert.DoesNotContain(key.Value, guide);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/executions", new { commands = Array.Empty<string>(), reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/executions/{Guid.NewGuid()}")).StatusCode);
    }
}
