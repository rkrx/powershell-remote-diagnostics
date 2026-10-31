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

    public void Request(Execution execution) => Write(
        $"\nRequest {execution.Id}\nReason: {ConsoleApproval.Escape(execution.Request.Reason)}\nScript:\n{ConsoleApproval.Escape(execution.Script)}\n",
        ConsoleColor.Magenta);

    public void Question(string text) => Write(text, ConsoleColor.Green);
    public void Decision(string text) => Write(text + Environment.NewLine, ConsoleColor.Cyan);
    public void Output(string stream, string text) => Write(ConsoleApproval.Escape(text) + Environment.NewLine);
}
