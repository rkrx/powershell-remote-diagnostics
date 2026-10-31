using System.ComponentModel;
using ModelContextProtocol.Server;
using ModelContextProtocol;
using P = WindowsPowerShellRc.Protocol;

namespace WindowsPowerShellRc.Client;

[McpServerToolType]
public sealed class ClientTools(ConnectionManager manager)
{
    [McpServerTool(Name = "connect"), Description("Connect to the Windows diagnostics server. Supply the complete grpc:// URL to connect without GUI. Omit it only when the human must enter the connection string in the local Avalonia dialog. Returns connectionId and mandatory server instructions/policy. Never print or repeat the secret URL.")]
    public Task<string> Connect(string? connectionString = null, CancellationToken cancellationToken = default) => Safe(() => manager.ConnectAsync(connectionString, cancellationToken));

    [McpServerTool(Name = "server_info"), Description("Read the connected server's environment, declaration and agent instructions before executing scripts.")]
    public Task<string> Info(string connectionId, CancellationToken cancellationToken) => Safe(() => manager.InvokeAsync(connectionId, async s => await s.Client.GetInfoAsync(new(), s.Headers, cancellationToken: cancellationToken)));

    [McpServerTool(Name = "execute"), Description("Submit one readable, commented PowerShell script as an array of lines, with a reason. Server-side operator approval applies. Returns one UUID; after 15 seconds pending work continues. Poll execution_status. Never retry an ambiguous submission automatically.")]
    public Task<string> Execute(string connectionId, string[] commands, string reason, CancellationToken cancellationToken) => Safe(() => manager.InvokeAsync(connectionId, async s =>
    {
        var request = new P.ExecuteRequest { Reason = reason }; request.Commands.Add(commands);
        return await s.Client.ExecuteAsync(request, s.Headers, cancellationToken: cancellationToken);
    }));

    [McpServerTool(Name = "execution_status"), Description("Poll an execution UUID about every two seconds. Completed responses include the first output page. Inspect error, outputIncomplete and nativeExitCode.")]
    public Task<string> Status(string connectionId, string id, CancellationToken cancellationToken) => Safe(() => manager.InvokeAsync(connectionId, async s => await s.Client.GetExecutionAsync(new() { Id = id }, s.Headers, cancellationToken: cancellationToken)));

    [McpServerTool(Name = "read_output"), Description("Read completed output: startLine is 1-based, lineCount 1-200. For a long line, set line and use charOffset/charCount (Unicode codepoints, count 1-65536); follow nextCharOffset. Normal pages may truncate lines at 4096 codepoints.")]
    public Task<string> Output(string connectionId, string id, long startLine = 1, int lineCount = 100, long? line = null, int charOffset = 0, int charCount = 4096, CancellationToken cancellationToken = default) => Safe(() => manager.InvokeAsync(connectionId, async s =>
    {
        var r = new P.OutputRequest { Id = id, StartLine = startLine, LineCount = lineCount, CharOffset = charOffset, CharCount = charCount };
        if (line is { } number) r.Line = number;
        return await s.Client.ReadOutputAsync(r, s.Headers, cancellationToken: cancellationToken);
    }));

    [McpServerTool(Name = "search_output"), Description("Search completed output for a literal string (not regex). Optional surrounding lines/characters and cursor pagination. Repeat all search parameters with nextCursor; offsets count Unicode codepoints.")]
    public Task<string> Search(string connectionId, string id, string q, bool ignoreCase = false, int beforeLines = 0, int afterLines = 0, int beforeChars = 0, int afterChars = 0, int limit = 20, string? cursor = null, CancellationToken cancellationToken = default) => Safe(() => manager.InvokeAsync(connectionId, async s =>
    {
        var r = new P.SearchRequest { Id = id, Q = q, IgnoreCase = ignoreCase, BeforeLines = beforeLines, AfterLines = afterLines, BeforeChars = beforeChars, AfterChars = afterChars, Limit = limit };
        if (cursor is not null) r.Cursor = cursor;
        return await s.Client.SearchAsync(r, s.Headers, cancellationToken: cancellationToken);
    }));

    [McpServerTool(Name = "cancel_execution"), Description("Withdraw queued work or request cooperative cancellation of the specified UUID. Does not undo changes or guarantee immediate termination of native processes.")]
    public Task<string> Cancel(string connectionId, string id, CancellationToken cancellationToken) => Safe(() => manager.InvokeAsync(connectionId, async s => await s.Client.CancelAsync(new() { Id = id }, s.Headers, cancellationToken: cancellationToken)));

    [McpServerTool(Name = "disconnect"), Description("Close a local connection. This does not cancel accepted remote work.")]
    public Task<string> Disconnect(string connectionId) => Safe(() => Task.FromResult(manager.Disconnect(connectionId)));

    private static async Task<string> Safe(Func<Task<string>> operation)
    {
        try { return await operation(); }
        catch (ClientOperationException ex) { throw new McpException(ex.Message); }
        catch (FormatException) { throw new McpException("Invalid connection URL. Paste the complete grpc:// connection string printed by the server."); }
        catch (OperationCanceledException) { throw new McpException("Connection entry or request was cancelled. Accepted remote work may still be running; do not blindly retry submission."); }
        catch (InvalidOperationException) { throw new McpException("Local connection entry could not complete. Supply a complete connectionString to connect without GUI."); }
    }
}
