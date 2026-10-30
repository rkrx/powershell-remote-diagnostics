using System.Reflection;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using WindowsPowerShellRc;

namespace WindowsPowerShellRc.Tests;

public class StorageFaultTests
{
    [Fact]
    public async Task StatusRemainsReadableWhenStoredOutputIsDamaged()
    {
        using var store = new OutputStore(); using var engine = new FakeEngine();
        await using var queue = new ExecutionQueue(engine, new AutoApproval(), store);
        var job = queue.TrySubmit(new(["committed"], "test"))!;
        await job.Completion;
        var data = (FileStream)typeof(StoredOutput).GetField("data", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(job.Output)!;
        data.Position = 0; data.WriteByte((byte)'!'); data.Flush();
        var key = new BearerKey();
        await using var app = Api.Build(new(Auto: true), "test", key, queue, engine.Version, b => b.WebHost.UseTestServer());
        await app.StartAsync();
        using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", key.Value);
        var response = await client.GetAsync($"/executions/{job.Id}");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(job.Id, status.GetProperty("id").GetGuid());
        Assert.True(status.GetProperty("outputIncomplete").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("error").ValueKind);
    }

    [Fact]
    public void PartialUncommittedRecordCannotCorruptPriorOutput()
    {
        using var store = new OutputStore();
        var output = store.Create(Guid.NewGuid());
        output.Append("output", "committed");
        var data = (FileStream)typeof(StoredOutput).GetField("data", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(output)!;
        data.Position = data.Length;
        data.Write(Encoding.UTF8.GetBytes("{\"Stream\":\"output\",\"Text\":\"partial"));
        data.Flush();
        Assert.Equal("committed", Assert.Single(output.ReadPage().Lines).Text);
    }

    [Fact]
    public async Task CleanupFailureCannotKillTheWorkerOrHideCompletion()
    {
        // On Unix an open file may be replaced by a directory, simulating a real deletion fault.
        if (OperatingSystem.IsWindows()) return;
        using var store = new OutputStore(); using var engine = new FakeEngine();
        var queue = new ExecutionQueue(engine, new AutoApproval(), store);
        var first = queue.TrySubmit(new(["first"], "test"))!;
        await first.Completion;
        var path = (string)typeof(StoredOutput).GetField("dataPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(first.Output)!;
        File.Delete(path); Directory.CreateDirectory(path);
        try
        {
            for (int i = 0; i < 21; i++)
            {
                var next = queue.TrySubmit(new(["next"], "test"))!;
                await next.Completion.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal("completed", next.Status);
            }
        }
        finally
        {
            Directory.Delete(path);
            try { await queue.DisposeAsync(); } catch (UnauthorizedAccessException) { }
        }
    }
}
