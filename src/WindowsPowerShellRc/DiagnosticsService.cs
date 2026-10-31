using Grpc.Core;
using P = WindowsPowerShellRc.Protocol;

namespace WindowsPowerShellRc;

public sealed class DiagnosticsService(ServerState state, ClientSessions sessions) : P.Diagnostics.DiagnosticsBase
{
    public override Task Session(IAsyncStreamReader<P.SessionFrame> requests, IServerStreamWriter<P.SessionFrame> responses, ServerCallContext context) => SessionService.Run(sessions, requests, responses, context);
    public override Task<P.ServerInfo> GetInfo(P.Empty request, ServerCallContext context) => Task.FromResult(new P.ServerInfo
    {
        Instructions = AgentGuide.Render(state.Options, state.Declaration, state.EngineVersion),
        EngineVersion = state.EngineVersion, SessionId = state.Identity.SessionId
    });
    public override async Task<P.ExecutionReply> Execute(P.ExecuteRequest request, ServerCallContext context)
    {
        var job = state.Queue.TrySubmit(new(request.Commands.ToArray(), request.Reason)) ??
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Queue is full or stopping; request was not accepted."));
        try { await job.Completion.WaitAsync(state.Wait, context.CancellationToken); }
        catch (TimeoutException) { }
        return Reply(job);
    }
    public override Task<P.ExecutionReply> GetExecution(P.ExecutionId request, ServerCallContext context) => Task.FromResult(Reply(Find(request.Id)));
    public override Task<P.ExecutionReply> Cancel(P.ExecutionId request, ServerCallContext context)
    {
        var job = Find(request.Id); state.Queue.Cancel(job.Id); return Task.FromResult(Reply(job));
    }
    public override Task<P.OutputReply> ReadOutput(P.OutputRequest r, ServerCallContext context)
    {
        var job = Ready(r.Id);
        return Task.FromResult(new P.OutputReply { Id = r.Id, Output = Page(job.Output.ReadPage(r.HasStartLine ? r.StartLine : 1,
            r.HasLineCount ? r.LineCount : 100, r.HasLine ? r.Line : null, r.HasCharOffset ? r.CharOffset : 0, r.HasCharCount ? r.CharCount : 4096)) });
    }
    public override Task<P.SearchReply> Search(P.SearchRequest r, ServerCallContext context)
    {
        var job = Ready(r.Id);
        var page = job.Output.Search(new(r.Q, r.IgnoreCase, r.BeforeLines, r.AfterLines, r.BeforeChars, r.AfterChars, r.HasLimit ? r.Limit : 20, r.HasCursor ? r.Cursor : null));
        var reply = new P.SearchReply { Id = r.Id };
        if (page.NextCursor is not null) reply.NextCursor = page.NextCursor;
        foreach (var hit in page.Matches)
        {
            var match = new P.SearchMatch { Line = hit.Line, CharOffset = hit.CharOffset, MatchLength = hit.MatchLength,
                Context = hit.Context, ContextOffset = hit.ContextOffset, ContextTruncated = hit.ContextTruncated };
            match.Lines.Add(hit.Lines.Select(Line)); reply.Matches.Add(match);
        }
        return Task.FromResult(reply);
    }
    private Execution Find(string id)
    {
        if (!Guid.TryParse(id, out var parsed)) throw new ArgumentException("Execution ID must be a UUID.");
        return state.Queue.Find(parsed) ?? throw new RpcException(new Status(StatusCode.NotFound, "Unknown or evicted execution."));
    }
    private Execution Ready(string id)
    {
        var job = Find(id);
        if (!job.IsTerminal) throw new RpcException(new Status(StatusCode.FailedPrecondition, "Output is available after completion."));
        return job;
    }
    private static P.ExecutionReply Reply(Execution job)
    {
        var reply = new P.ExecutionReply { Id = job.Id.ToString(), Status = job.Status, OutputReady = job.IsTerminal,
            Message = job.IsTerminal ? "Execution finished." : "Result is not ready. Poll execution_status periodically.",
            CreatedAt = job.CreatedAt.ToString("O"), OutputIncomplete = job.OutputIncomplete };
        if (job.FinishedAt is { } finished) reply.FinishedAt = finished.ToString("O");
        if (job.NativeExitCode is { } exit) reply.NativeExitCode = exit;
        if (job.Error is { } error) reply.Error = error;
        if (job.CleanupWarning is { } warning) reply.CleanupWarning = warning;
        if (job.IsTerminal)
        {
            try { reply.Output = Page(job.Output.ReadPage()); }
            catch (IOException) { reply.OutputIncomplete = true; reply.Error = "Stored output cannot be read; execution metadata remains available."; }
        }
        return reply;
    }
    private static P.OutputLine Line(OutputLine l) => new() { Number = l.Number, Stream = l.Stream, Text = l.Text,
        TotalCharacters = l.TotalCharacters, IsLongLine = l.IsLongLine, Truncated = l.Truncated, CharOffset = l.CharOffset };
    private static P.OutputPage Page(OutputPage page)
    {
        var result = new P.OutputPage { TotalLines = page.TotalLines }; result.Lines.Add(page.Lines.Select(Line));
        if (page.NextLine is { } next) result.NextLine = next;
        if (page.NextCharOffset is { } offset) result.NextCharOffset = offset;
        return result;
    }
}
