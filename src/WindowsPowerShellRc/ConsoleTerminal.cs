namespace WindowsPowerShellRc;

public sealed class ConsoleTerminal
{
    private readonly object gate = new();

    private void Write(string text, ConsoleColor? color = null)
    {
        lock (gate)
        {
            try
            {
                if (!Console.IsOutputRedirected)
                {
                    Console.ResetColor();
                    if (color is { } foreground) Console.ForegroundColor = foreground;
                }
                Console.Write(text);
            }
            finally
            {
                if (!Console.IsOutputRedirected) Console.ResetColor();
            }
        }
    }

    public void Request(Execution execution)
    {
        Write($"\nRequest {execution.Id}\n", ConsoleColor.DarkMagenta);
        Write($"Reason: {ConsoleApproval.Escape(execution.Request.Reason)}\n", ConsoleColor.Cyan);
        int width = 100;
        if (!Console.IsOutputRedirected)
        {
            try { if (Console.WindowWidth > 1) width = Console.WindowWidth - 1; }
            catch (IOException) { }
        }
        Write($"Script:\n{ScriptDisplay.Format(execution.Script, execution.Request.Reason, width)}\n", ConsoleColor.Yellow);
    }

    public void Question(string text) => Write(text, ConsoleColor.Magenta);
    public void Decision(string text, bool success) => Write(text + Environment.NewLine, success ? ConsoleColor.Green : ConsoleColor.Red);
    public void Output(string stream, string text) => Write(ConsoleApproval.Escape(text) + Environment.NewLine);
}
