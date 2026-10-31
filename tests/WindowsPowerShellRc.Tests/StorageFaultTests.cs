using System.Reflection;
using System.Text;
using WindowsPowerShellRc.Protocol;
using WindowsPowerShellRc;

namespace WindowsPowerShellRc.Tests;

public class StorageFaultTests
{
    [Fact]
    public async Task StatusRemainsReadableWhenStoredOutputIsDamaged()
    {
        await using var server = await TestGrpcServer.Start();
        var job = server.Queue.TrySubmit(new(["committed"], "test"))!;
        await job.Completion;
        var data = (FileStream)typeof(StoredOutput).GetField("data", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(job.Output)!;
        data.Position = 0; data.WriteByte((byte)'!'); data.Flush();
        using var channel = server.Address.CreateChannel();
        var client = new Diagnostics.DiagnosticsClient(channel);
        using var admission = await WindowsPowerShellRc.Transport.DiagnosticSession.OpenAsync(client, server.Address.Headers, default);
        var status = await client.GetExecutionAsync(new() { Id = job.Id.ToString() }, admission.Headers);
        Assert.Equal(job.Id.ToString(), status.Id);
        Assert.True(status.OutputIncomplete);
        Assert.True(status.HasError);

    }

    [Fact]
    public void PartialUncommittedRecordCannotCorruptPriorOutput()
    {
        using var store = new OutputStore();
        using var output = store.Create(Guid.NewGuid());
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
