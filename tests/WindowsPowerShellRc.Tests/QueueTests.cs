using WindowsPowerShellRc;

namespace WindowsPowerShellRc.Tests;

public class QueueTests
{
    [Fact]
    public async Task QueueIsSerialAndRetainsOnlyTwentyCompletedExecutions()
    {
        using var store = new OutputStore();
        using var engine = new FakeEngine();
        await using var queue = new ExecutionQueue(engine, new AutoApproval(), store);
        var jobs = new List<Execution>();
        for (int i = 0; i < 22; i++)
        {
            var job = queue.TrySubmit(new([i.ToString()], "test"))!;
            jobs.Add(job);
            await job.Completion;
        }
        Assert.Equal(1, engine.MaxConcurrency);
        Assert.Null(queue.Find(jobs[0].Id));
        Assert.NotNull(queue.Find(jobs[2].Id));
        Assert.Equal("completed", jobs[^1].Status);
    }

    [Fact]
    public async Task CancellingApprovalCannotStartTheScriptLater()
    {
        using var store = new OutputStore();
        using var engine = new FakeEngine();
        var approval = new DeferredApproval();
        await using var queue = new ExecutionQueue(engine, approval, store);
        var job = queue.TrySubmit(new(["never"], "test"))!;
        await approval.Started.Task;
        queue.Cancel(job.Id);
        approval.Answer.TrySetResult(true);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("cancelled", job.Status);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task FullQueueRejectsBeforeAcceptanceAndDeniedJobsFinish()
    {
        using var store = new OutputStore();
        using var engine = new FakeEngine();
        var approval = new DeferredApproval();
        await using var queue = new ExecutionQueue(engine, approval, store);
        var active = queue.TrySubmit(new(["first"], "test"))!;
        await approval.Started.Task;
        for (int i = 0; i < 32; i++) Assert.NotNull(queue.TrySubmit(new(["wait"], "test")));
        Assert.Null(queue.TrySubmit(new(["overflow"], "test")));
        approval.Answer.SetResult(false);
        await active.Completion;
        Assert.Equal("denied", active.Status);
    }

    [Fact]
    public async Task EngineFailureIsStoredWithoutKillingWorker()
    {
        using var store = new OutputStore();
        using var engine = new FakeEngine { Throw = true };
        await using var queue = new ExecutionQueue(engine, new AutoApproval(), store);
        var job = queue.TrySubmit(new(["anything"], "test"))!;
        await job.Completion;
        Assert.Equal("failed", job.Status);
        Assert.True(job.OutputIncomplete);
    }

    [Fact]
    public async Task UnavailableEngineDisablesNewWorkRatherThanReplacingSession()
    {
        using var store = new OutputStore();
        using var engine = new FakeEngine { Fatal = true };
        await using var queue = new ExecutionQueue(engine, new AutoApproval(), store);
        var job = queue.TrySubmit(new(["anything"], "test"))!;
        await job.Completion;
        Assert.Equal("failed", job.Status);
        Assert.Contains("restart", job.Error);
        Assert.Null(queue.TrySubmit(new(["later"], "test")));
        Assert.Equal(1, engine.Calls);
    }
}

internal sealed class FakeEngine : IPowerShellEngine
{
    public string Version => "7.fake";
    public int Calls;
    private int active;
    public int MaxConcurrency;
    public bool Throw;
    public bool Fatal;
    public TaskCompletionSource? Release;
    public async Task<EngineResult> ExecuteAsync(string script, Action<string, string> emit, CancellationToken token)
    {
        Calls++;
        MaxConcurrency = Math.Max(MaxConcurrency, Interlocked.Increment(ref active));
        try
        {
            if (Throw) throw new IOException("disk or engine unavailable");
            if (Fatal) throw new EngineUnavailableException("Session is unavailable; restart required.");
            if (Release is not null) await Release.Task.WaitAsync(token);
            emit("output", script);
            return new(false, false);
        }
        finally { Interlocked.Decrement(ref active); }
    }
    public void Dispose() { }
}

internal sealed class DeferredApproval : IApproval
{
    public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> ConfirmAsync(Execution execution, CancellationToken token)
    {
        Started.TrySetResult();
        return Answer.Task.WaitAsync(token);
    }
}
