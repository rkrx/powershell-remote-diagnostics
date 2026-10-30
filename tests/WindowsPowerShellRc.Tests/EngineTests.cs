using WindowsPowerShellRc;

namespace WindowsPowerShellRc.Tests;

public class EngineTests
{
    [Fact]
    public async Task VariablesFunctionsAndLocationPersist()
    {
        using var engine = new PowerShellEngine();
        var lines = new List<string>();
        await engine.ExecuteAsync("$rcAnswer = 42; function Get-RcAnswer { $rcAnswer }", (_, _) => { }, default);
        var result = await engine.ExecuteAsync("Get-RcAnswer", (_, text) => lines.Add(text), default);
        Assert.False(result.HadErrors);
        Assert.Contains("42", string.Join("\n", lines));
        Assert.StartsWith("7.", engine.Version);
    }

    [Fact]
    public async Task ErrorsAndInformationAreCapturedAndPromptsDoNotBlock()
    {
        using var engine = new PowerShellEngine();
        var streams = new List<string>();
        var result = await engine.ExecuteAsync("Write-Host 'hello'; Write-Error 'bad'; 'after'", (stream, _) => streams.Add(stream), default);
        Assert.True(result.HadErrors);
        Assert.Contains("information", streams);
        Assert.Contains("error", streams);
        Assert.Contains("output", streams);
        var prompt = await engine.ExecuteAsync("Read-Host 'blocked'", (_, _) => { }, default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(prompt.HadErrors);
    }

    [Fact]
    public async Task CancellationStopsPipelineAndSessionCanBeReused()
    {
        using var engine = new PowerShellEngine();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var result = await engine.ExecuteAsync("Start-Sleep -Seconds 60", (_, _) => { }, cancel.Token);
        Assert.True(result.Cancelled);
        Assert.False((await engine.ExecuteAsync("1+1", (_, _) => { }, default)).HadErrors);
    }

    [Fact]
    public async Task TerminatingErrorsAndStorageFailuresAreExplicit()
    {
        using var engine = new PowerShellEngine();
        var failed = await engine.ExecuteAsync("throw 'broken'", (_, _) => { }, default);
        Assert.True(failed.HadErrors);
        var disk = await engine.ExecuteAsync("1..10000", (_, _) => throw new IOException("Disk full"), default);
        Assert.True(disk.HadErrors);
        Assert.True(disk.OutputIncomplete);
    }

    [Fact]
    public async Task ManyOutputsAndErrorsAreCapturedAndNativeExitDoesNotLeak()
    {
        using var engine = new PowerShellEngine();
        var lines = new List<string>();
        var many = await engine.ExecuteAsync("1..1000 | ForEach-Object { Write-Error \"e$_\"; \"o$_\" }", (stream, text) => lines.Add(stream + ":" + text), default);
        Assert.True(many.HadErrors);
        Assert.Equal(1000, lines.Count(l => l.StartsWith("error:")));
        Assert.Equal(1000, lines.Count(l => l.StartsWith("output:")));
        string native = OperatingSystem.IsWindows() ? "cmd /c exit 7" : "/bin/sh -c 'exit 7'";
        Assert.Equal(7, (await engine.ExecuteAsync(native, (_, _) => { }, default)).NativeExitCode);
        Assert.Null((await engine.ExecuteAsync("'no native process'", (_, _) => { }, default)).NativeExitCode);
    }
}
