using WindowsPowerShellRc;

namespace WindowsPowerShellRc.Tests;

public class CliTests
{
    [Fact]
    public void HelpShowsTheWindowsExecutableEntryPoint() => Assert.Contains("powershell-rc", CliOptions.Usage);

    [Fact]
    public void DefaultsAreManualProductionAndAllInterfaces()
    {
        var options = CliOptions.Parse([]);
        Assert.False(options.Auto);
        Assert.False(options.Test);
        Assert.Equal(8080, options.Port);
        Assert.Equal("All", options.Bind);
        Assert.Equal(CliOptions.DefaultDeclaration, options.ReadDeclaration());
    }

    [Fact]
    public void OptionsAndInvalidInputsAreValidated()
    {
        var options = CliOptions.Parse(["-a", "--test", "-p", "8090", "--bind", "127.0.0.1"]);
        Assert.True(options.Auto && options.Test);
        Assert.Equal(8090, options.Port);
        Assert.Single(options.ResolveAddresses());
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--port", "0"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--unknown"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["-d"]));
    }

    [Fact]
    public void YoloPermitsWritesAndExecutionInTheAgentInstructions()
    {
        var options = CliOptions.Parse(["--yolo"]);
        var instructions = AgentGuide.Render(options, options.ReadDeclaration(), "7");
        Assert.Contains("Writing changes and running commands and programs are allowed", instructions);
        Assert.DoesNotContain("Only read-only diagnostic operations are allowed", instructions);
        Assert.Contains("operator approval for every script", instructions);
    }

    [Fact]
    public void ExplicitDeclarationTakesPrecedenceOverYolo()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "Read event logs only.");
            var options = CliOptions.Parse(["--yolo", "--declaration", path]);
            Assert.Equal("Read event logs only.", options.ReadDeclaration());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void KeyRequiresExactBearerToken()
    {
        var key = new BearerKey();
        Assert.True(key.IsValid("Bearer " + key.Value));
        Assert.False(key.IsValid(null));
        Assert.False(key.IsValid("Bearer wrong"));
        Assert.False(key.IsValid("Basic " + key.Value));
        Assert.NotEqual(key.Value, new BearerKey().Value);
    }

    [Fact]
    public void ConsoleMasksTerminalAndBidirectionalControls()
    {
        Assert.Equal("\\u{1B}[31m\\u{202E}text\n", ConsoleApproval.Escape("\u001b[31m\u202etext\n"));
    }
}
