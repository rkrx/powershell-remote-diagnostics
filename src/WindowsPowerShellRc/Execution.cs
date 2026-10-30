namespace WindowsPowerShellRc;

public record ExecutionRequest(string[] Commands, string Reason);

public sealed class Execution
{
    public Guid Id { get; } = Guid.NewGuid();
    public ExecutionRequest Request { get; }
    public string Script => string.Join("\n", Request.Commands);
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; internal set; }
    private string status = "queued";
    public string Status { get => Volatile.Read(ref status); internal set => Volatile.Write(ref status, value); }
    public bool IsTerminal => Status is "completed" or "failed" or "denied" or "cancelled";
    public bool OutputIncomplete { get; internal set; }
    public string? Error { get; internal set; }
    public string? CleanupWarning { get; internal set; }
    public int? NativeExitCode { get; internal set; }
    public StoredOutput Output { get; }
    internal CancellationTokenSource Cancellation { get; } = new();
    internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => Finished.Task;
    internal LinkedListNode<Execution>? QueueNode;
    internal Execution(ExecutionRequest request, OutputStore store)
    {
        Request = request with { Commands = (string[])request.Commands.Clone() };
        Output = store.Create(Id);
    }
}
