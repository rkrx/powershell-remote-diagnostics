namespace WindowsPowerShellRc.Tests;
public class PackagedHostTests
{
    [Fact]
    public async Task BundledModulesWorkOverPinnedGrpcAndOutputIsMirrored()
    {
        await using var server = await ServerProcess.Start();
        var body = await server.Execute("'bundled-engine-ready'; (Get-Module Microsoft.PowerShell.Utility).Path; " +
            "(Get-Item -LiteralPath $PSHOME).Exists; Write-Warning 'mirror-warning'; Write-Host 'mirror-host'; " +
            "Write-Output ([string][char]27 + '[31mcolored')");
        string output = string.Join("\n", body.Output.Lines.Select(l => l.Text));
        Assert.True(body.Status == "completed", output);
        Assert.Contains("bundled-engine-ready", output);
        Assert.True(output.Contains(server.PackageDirectory, StringComparison.OrdinalIgnoreCase), output);
        Assert.Contains("True", output);
        // Completion follows emission, but allow the stdout reader to catch up.
        for (int i=0; i<100 && !server.ConsoleLines.Contains("mirror-host"); i++) await Task.Delay(10);
        Assert.Contains("bundled-engine-ready", server.ConsoleLines);
        Assert.Contains("mirror-warning", server.ConsoleLines);
        Assert.Contains("mirror-host", server.ConsoleLines);
        Assert.Contains("\\u{1B}[31mcolored", server.ConsoleLines);
        Assert.DoesNotContain(server.ConsoleLines, line => line.Contains('\u001b'));
    }
}
