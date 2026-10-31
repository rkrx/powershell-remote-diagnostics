namespace WindowsPowerShellRc.Tests;
public class NativeInputTests
{
    [Fact]
    public async Task NativeProcessSeesEofEvenWhenParentStdinPipeRemainsOpen()
    {
        await using var server = await ServerProcess.Start();
        string command = OperatingSystem.IsWindows() ? "cmd /d /c 'set /p rcInput= & echo EOF'" : "/bin/sh -c 'read answer; printf EOF'";
        var body = await server.Execute(command).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(body.OutputReady); Assert.Contains("EOF", string.Join("\n", body.Output.Lines.Select(l => l.Text)));
    }
}
