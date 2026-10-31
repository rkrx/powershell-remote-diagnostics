using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Themes.Fluent;
using WindowsPowerShellRc.Transport;

namespace WindowsPowerShellRc.Client;

public sealed class ConnectionDialog : IConnectionPrompt
{
    public async Task<string?> RequestAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            throw new InvalidOperationException("No desktop is available. Supply connectionString to the connect tool.");
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if ((Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "").Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--connection-dialog"); start.ArgumentList.Add(pipe.GetClientHandleAsString());
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not open the connection dialog. Supply connectionString instead.");
        pipe.DisposeLocalCopyOfClientHandle();
        var stdout = child.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = child.StandardError.ReadToEndAsync(cancellationToken);
        using var reader = new StreamReader(pipe);
        try
        {
            string? result = await reader.ReadLineAsync(cancellationToken);
            await child.WaitForExitAsync(cancellationToken);
            await stdout; await stderr; // Never forward UI output or exceptions containing input.
            if (child.ExitCode != 0) throw new InvalidOperationException("The connection dialog could not run. Supply connectionString to the connect tool.");
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }
    public static int Run(string handle)
    {
        try
        {
            DialogApp.ResultPipe = new AnonymousPipeClientStream(PipeDirection.Out, handle);
            AppBuilder.Configure<DialogApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
            DialogApp.ResultPipe.Dispose(); return 0;
        }
        catch { return 1; }
    }
}
public sealed class DialogApp : Application
{
    internal static Stream ResultPipe = Stream.Null;
    public override void Initialize()
    {
        Name = "PowerShell Remote Diagnostics";
        Styles.Add(new FluentTheme());
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var input = new TextBox { PlaceholderText = "grpc://<token>@<host>:<port>", PasswordChar = '●', HorizontalAlignment = HorizontalAlignment.Stretch };
            var message = new TextBlock { Text = "Paste the complete connection string printed by the Windows server. It contains a secret; do not share it publicly.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var connect = new Button { Content = "Connect", IsDefault = true };
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            var window = new Window { Title = "PowerShell Remote Diagnostics — Connect", Width = 620, Height = 280, MinWidth = 420, MinHeight = 240,
                Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = { message, input, error,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { connect, cancel } } } } };
            bool sent = false;
            connect.Click += (_, _) =>
            {
                try
                {
                    string url = input.Text?.Trim() ?? ""; ConnectionAddress.Parse(url);
                    using var writer = new StreamWriter(ResultPipe, leaveOpen: true); writer.WriteLine(url); writer.Flush();
                    sent = true; input.Text = ""; window.Close();
                }
                catch (FormatException) { error.Text = "Invalid connection string. Paste the complete grpc:// URL from the server."; }
            };
            cancel.Click += (_, _) => window.Close();
            window.Closed += (_, _) =>
            {
                input.Text = "";
                if (!sent) { using var writer = new StreamWriter(ResultPipe, leaveOpen: true); writer.WriteLine(); writer.Flush(); }
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
