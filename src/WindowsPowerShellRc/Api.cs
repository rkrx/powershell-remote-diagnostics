using System.Text.Json;

namespace WindowsPowerShellRc;

public static class Api
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(15);
    public static WebApplication Build(CliOptions options, string declaration, BearerKey key, ExecutionQueue queue,
        string engineVersion, Action<WebApplicationBuilder>? configure = null, TimeSpan? wait = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 1024 * 1024);
        configure?.Invoke(builder);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!key.IsValid(context.Request.Headers.Authorization.ToString()))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await Results.Problem(statusCode: 401, title: "A valid Bearer token is required.").ExecuteAsync(context);
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            try { await next(); }
            catch (Exception ex) when (!context.Response.HasStarted && ex is ArgumentException or JsonException or BadHttpRequestException or ObjectDisposedException)
            {
                int status = ex is ObjectDisposedException ? 404 : ex is BadHttpRequestException bad ? bad.StatusCode : 400;
                await Results.Problem(statusCode: status, title: status == 404 ? "Unknown or evicted execution." : ex.Message).ExecuteAsync(context);
            }
            catch (IOException) when (!context.Response.HasStarted)
            {
                await Results.Problem(statusCode: 500, title: "Output storage is unavailable.").ExecuteAsync(context);
            }
        });
        app.MapGet("/", () => Results.Text(AgentGuide.Render(options, declaration, engineVersion), "text/markdown; charset=utf-8"));
        app.MapPost("/executions", async (HttpContext context) =>
        {
            if (!context.Request.HasJsonContentType()) return Results.Problem(statusCode: 415, title: "Content-Type must be application/json.");
            var request = await context.Request.ReadFromJsonAsync<ExecutionRequest>(context.RequestAborted)
                ?? throw new ArgumentException("A JSON execution request is required.");
            var execution = queue.TrySubmit(request);
            if (execution is null) return Results.Problem(statusCode: 429, title: "Queue is full or stopping; request was not accepted.");
            try { await execution.Completion.WaitAsync(wait ?? DefaultWait, context.RequestAborted); }
            catch (TimeoutException) { }
            return ExecutionResponse(context, execution);
        });
        app.MapGet("/executions/{id:guid}", (Guid id, HttpContext context) =>
            queue.Find(id) is { } execution ? ExecutionResponse(context, execution) : Missing());
        app.MapPost("/executions/{id:guid}/cancel", (Guid id, HttpContext context) =>
            queue.Cancel(id) && queue.Find(id) is { } execution ? ExecutionResponse(context, execution) : Missing());
        app.MapGet("/executions/{id:guid}/output", (Guid id, HttpContext context) =>
        {
            var job = queue.Find(id);
            if (job is null) return Missing();
            if (!job.IsTerminal) return Results.Problem(statusCode: 409, title: "Output is available after completion.");
            var q = context.Request.Query;
            long? line = q.ContainsKey("line") ? Long(q, "line", 1) : null;
            var page = job.Output.ReadPage(Long(q, "startLine", 1), Int(q, "lineCount", 100), line, Int(q, "charOffset", 0), Int(q, "charCount", 4096));
            return Results.Ok(new { id, output = page });
        });
        app.MapGet("/executions/{id:guid}/search", (Guid id, HttpContext context) =>
        {
            var job = queue.Find(id);
            if (job is null) return Missing();
            if (!job.IsTerminal) return Results.Problem(statusCode: 409, title: "Search is available after completion.");
            var q = context.Request.Query;
            var page = job.Output.Search(new(q["q"].ToString(), Bool(q, "ignoreCase"), Int(q, "beforeLines", 0), Int(q, "afterLines", 0),
                Int(q, "beforeChars", 0), Int(q, "afterChars", 0), Int(q, "limit", 20), q.ContainsKey("cursor") ? q["cursor"].ToString() : null));
            return Results.Ok(new { id, search = page });
        });
        return app;
    }

    private static IResult Missing() => Results.Problem(statusCode: 404, title: "Unknown or evicted execution.");
    private static IResult ExecutionResponse(HttpContext context, Execution job)
    {
        bool done = job.IsTerminal;
        OutputPage? page = null;
        string? outputError = null;
        if (done)
        {
            try { page = job.Output.ReadPage(); }
            catch (IOException) { outputError = "Stored output cannot be read; execution metadata remains available."; }
        }
        string url = $"/executions/{job.Id}";
        if (!done) { context.Response.Headers.Location = url; context.Response.Headers.RetryAfter = "2"; }
        var body = new
        {
            job.Id, job.Status, statusUrl = url, outputReady = done,
            message = done ? "Execution finished." : "Result is not ready. Poll statusUrl periodically.",
            job.CreatedAt, job.FinishedAt, job.NativeExitCode, outputIncomplete = job.OutputIncomplete || outputError is not null,
            error = outputError ?? job.Error, job.CleanupWarning, output = page
        };
        return Results.Json(body, statusCode: done ? 200 : 202);
    }
    private static int Int(IQueryCollection q, string name, int fallback) => !q.ContainsKey(name) ? fallback :
        int.TryParse(q[name], out int value) ? value : throw new ArgumentException($"Invalid {name}.");
    private static long Long(IQueryCollection q, string name, long fallback) => !q.ContainsKey(name) ? fallback :
        long.TryParse(q[name], out long value) ? value : throw new ArgumentException($"Invalid {name}.");
    private static bool Bool(IQueryCollection q, string name) => !q.ContainsKey(name) ? false :
        bool.TryParse(q[name], out bool value) ? value : throw new ArgumentException($"Invalid {name}.");
}
