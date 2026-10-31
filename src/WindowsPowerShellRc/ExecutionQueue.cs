namespace WindowsPowerShellRc;

public sealed class ExecutionQueue : IAsyncDisposable
{
    private readonly IPowerShellEngine engine;
    private readonly IApproval approval;
    private readonly OutputStore store;
    private readonly Action<string, string>? mirror;
    private readonly object gate = new();
    private readonly LinkedList<Execution> waiting = new();
    private readonly Dictionary<Guid, Execution> executions = new();
    private readonly Queue<Execution> completed = new();
    private readonly SemaphoreSlim available = new(0);
    private readonly Task worker;
    private bool stopping;
    private Execution? active;

    public ExecutionQueue(IPowerShellEngine engine, IApproval approval, OutputStore store, Action<string, string>? mirror = null)
    {
        this.engine = engine; this.approval = approval; this.store = store; this.mirror = mirror;
        worker = Task.Run(WorkAsync);
    }

    public Execution? TrySubmit(ExecutionRequest request)
    {
        if (request.Commands is not { Length: > 0 } || request.Commands.Any(c => c is null) || string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("commands must be a nonempty string array and reason must be nonempty.");
        lock (gate)
        {
            if (stopping || waiting.Count >= 32) return null;
            var execution = new Execution(request, store);
            executions.Add(execution.Id, execution);
            execution.QueueNode = waiting.AddLast(execution);
            available.Release();
            return execution;
        }
    }

    public Execution? Find(Guid id) { lock (gate) return executions.GetValueOrDefault(id); }

    public bool Cancel(Guid id)
    {
        lock (gate)
        {
            if (!executions.TryGetValue(id, out var execution)) return false;
            if (execution.IsTerminal) return true;
            execution.Cancellation.Cancel();
            if (execution.QueueNode is not null)
            {
                waiting.Remove(execution.QueueNode); execution.QueueNode = null;
                available.Wait(0);
                Finish(execution, "cancelled");
            }
            return true;
        }
    }

    private void Finish(Execution execution, string status)
    {
        // Called under gate; completion is published only after retention has been updated.
        execution.FinishedAt = DateTimeOffset.UtcNow;
        execution.Status = status;
        completed.Enqueue(execution);
        while (completed.Count > 20)
        {
            var old = completed.Dequeue(); executions.Remove(old.Id);
            try { old.Output.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                execution.CleanupWarning = "An evicted result could not be fully removed; temporary files may remain until shutdown.";
            }
            finally { old.Cancellation.Dispose(); }
        }
        execution.Finished.TrySetResult();
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            await available.WaitAsync();
            Execution job;
            lock (gate)
            {
                if (stopping) return;
                if (waiting.First is null) continue;
                job = waiting.First.Value; waiting.RemoveFirst(); job.QueueNode = null;
                active = job; job.Status = "awaiting_approval";
            }
            string finalStatus;
            try
            {
                bool approved = await approval.ConfirmAsync(job, job.Cancellation.Token);
                job.Cancellation.Token.ThrowIfCancellationRequested();
                if (!approved) finalStatus = "denied";
                else
                {
                    lock (gate)
                    {
                        job.Cancellation.Token.ThrowIfCancellationRequested();
                        job.Status = "running";
                    }
                    var result = await engine.ExecuteAsync(job.Script, (stream, text) =>
                    {
                        job.Output.Append(stream, text);
                        mirror?.Invoke(stream, text);
                    }, job.Cancellation.Token);
                    job.NativeExitCode = result.NativeExitCode; job.OutputIncomplete = result.OutputIncomplete;
                    if (result.OutputIncomplete) job.Error = "Output storage failed; output is incomplete.";
                    finalStatus = result.OutputIncomplete ? "failed" : result.Cancelled ? "cancelled" : result.HadErrors ? "failed" : "completed";
                }
            }
            catch (OperationCanceledException) { finalStatus = "cancelled"; }
            catch (EngineUnavailableException ex)
            {
                job.Error = ex.Message; job.OutputIncomplete = true; finalStatus = "failed";
                lock (gate)
                {
                    stopping = true;
                    foreach (var queued in waiting.ToArray()) Cancel(queued.Id);
                    available.Release();
                }
            }
            catch (Exception ex) { job.Error = ex.Message; job.OutputIncomplete = true; finalStatus = "failed"; }
            lock (gate) { active = null; Finish(job, finalStatus); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            stopping = true;
            foreach (var job in waiting.ToArray()) Cancel(job.Id);
            active?.Cancellation.Cancel();
            available.Release();
        }
        await worker;
        lock (gate)
        {
            foreach (var job in executions.Values) { job.Output.Dispose(); job.Cancellation.Dispose(); }
            executions.Clear();
        }
        available.Dispose();
    }
}
