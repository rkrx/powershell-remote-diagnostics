using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Security;

namespace WindowsPowerShellRc;

public record EngineResult(bool HadErrors, bool Cancelled, int? NativeExitCode = null, bool OutputIncomplete = false);
public interface IPowerShellEngine : IDisposable
{
    string Version { get; }
    Task<EngineResult> ExecuteAsync(string script, Action<string, string> emit, CancellationToken cancellationToken);
}

public sealed class PowerShellEngine : IPowerShellEngine
{
    private readonly Runspace runspace;
    public string Version { get; }
    public PowerShellEngine()
    {
        runspace = RunspaceFactory.CreateRunspace(new NonInteractiveHost(), InitialSessionState.CreateDefault2());
        try
        {
            runspace.Open();
            using var query = PowerShell.Create();
            query.Runspace = runspace;
            // Exercise the same formatter as real requests before accepting gRPC traffic.
            var version = query.AddScript("$PSVersionTable.PSVersion.ToString()")
                .AddCommand("Out-String").Invoke();
            if (query.HadErrors || version.Count != 1)
                throw new InvalidOperationException("Embedded PowerShell initialization failed. Extract the complete package, including Modules.");
            Version = version[0].ToString().Trim();
        }
        catch
        {
            runspace.Dispose();
            throw;
        }
    }

    public Task<EngineResult> ExecuteAsync(string script, Action<string, string> emit, CancellationToken cancellationToken) => Task.Run(() =>
    {
        lock (NativeInputScope.Gate) return Execute(script, emit, cancellationToken);
    });

    private EngineResult Execute(string script, Action<string, string> emit, CancellationToken cancellationToken)
    {
        if (runspace.RunspaceStateInfo.State != RunspaceState.Opened)
            throw new EngineUnavailableException("PowerShell session is unavailable; restart required.");
        using var nativeInput = new NativeInputScope();
        using var shell = PowerShell.Create();
        shell.Runspace = runspace;
        runspace.SessionStateProxy.SetVariable("LASTEXITCODE", null);
        Exception? outputFailure = null;
        bool hadErrors = false;
        var emitLock = new object();
        void Emit(string stream, string text)
        {
            lock (emitLock)
            {
                if (outputFailure is not null) return;
                try { emit(stream, text); }
                catch (Exception ex)
                {
                    outputFailure = ex;
                    _ = Task.Run(() => { try { shell.Stop(); } catch (InvalidOperationException) { } });
                }
            }
        }
        shell.Streams.Error.DataAdded += (_, _) => { hadErrors = true; foreach (var item in shell.Streams.Error.ReadAll()) Emit("error", item.ToString()); };
        shell.Streams.Warning.DataAdded += (_, _) => { foreach (var item in shell.Streams.Warning.ReadAll()) Emit("warning", item.Message); };
        shell.Streams.Verbose.DataAdded += (_, _) => { foreach (var item in shell.Streams.Verbose.ReadAll()) Emit("verbose", item.Message); };
        shell.Streams.Debug.DataAdded += (_, _) => { foreach (var item in shell.Streams.Debug.ReadAll()) Emit("debug", item.Message); };
        shell.Streams.Information.DataAdded += (_, _) => { foreach (var item in shell.Streams.Information.ReadAll()) Emit("information", item.MessageData?.ToString() ?? ""); };
        using var output = new PSDataCollection<PSObject>();
        output.DataAdded += (_, _) =>
        {
            foreach (var item in output.ReadAll()) Emit("output", item?.ToString() ?? "");
        };
        using var input = new PSDataCollection<PSObject>();
        input.Complete();
        shell.AddScript(script, useLocalScope: false).AddCommand("Out-String").AddParameter("Stream").AddParameter("Width", int.MaxValue);
        bool stopped = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var invocation = shell.BeginInvoke<PSObject, PSObject>(input, output);
            using var registration = cancellationToken.Register(() =>
                _ = Task.Run(() => { try { shell.Stop(); } catch (InvalidOperationException) { } }));
            shell.EndInvoke(invocation);
        }
        catch (Exception ex) when (ex is PipelineStoppedException or OperationCanceledException) { stopped = true; }
        catch (RuntimeException ex) { hadErrors = true; Emit("error", ex.Message); }
        if (outputFailure is not null)
            return new EngineResult(true, cancellationToken.IsCancellationRequested, OutputIncomplete: true);
        int? exitCode = runspace.RunspaceStateInfo.State == RunspaceState.Opened && runspace.SessionStateProxy.GetVariable("LASTEXITCODE") is int code ? code : null;
        return new EngineResult(shell.HadErrors || hadErrors, stopped || cancellationToken.IsCancellationRequested, exitCode);
    }

    public void Dispose() => runspace.Dispose();
}

internal sealed class NonInteractiveHost : PSHost
{
    private readonly Guid id = Guid.NewGuid();
    public override Guid InstanceId => id;
    public override string Name => "WindowsPowerShellRc";
    public override Version Version => new(1, 0);
    public override PSHostUserInterface UI { get; } = new NonInteractiveUi();
    public override CultureInfo CurrentCulture => CultureInfo.InvariantCulture;
    public override CultureInfo CurrentUICulture => CultureInfo.InvariantCulture;
    public override void SetShouldExit(int exitCode) => throw new PSInvalidOperationException("Exit is not supported; stop the RC program explicitly.");
    public override void EnterNestedPrompt() => throw new PSInvalidOperationException("Interactive prompts are not supported.");
    public override void ExitNestedPrompt() { }
    public override void NotifyBeginApplication() { }
    public override void NotifyEndApplication() { }
}

internal sealed class NonInteractiveUi : PSHostUserInterface
{
    private static PSInvalidOperationException Blocked() => new("Interactive input is not available through this API.");
    public override PSHostRawUserInterface RawUI => null!;
    public override string ReadLine() => throw Blocked();
    public override SecureString ReadLineAsSecureString() => throw Blocked();
    public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions) => throw Blocked();
    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) => throw Blocked();
    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName, PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options) => throw Blocked();
    public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice) => throw Blocked();
    public override void Write(string value) { }
    public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) { }
    public override void WriteLine(string value) { }
    public override void WriteErrorLine(string value) { }
    public override void WriteDebugLine(string message) { }
    public override void WriteProgress(long sourceId, ProgressRecord record) { }
    public override void WriteVerboseLine(string message) { }
    public override void WriteWarningLine(string message) { }
}
