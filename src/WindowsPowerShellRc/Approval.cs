using System.Text;

namespace WindowsPowerShellRc;

public interface IApproval
{
    Task<bool> ConfirmAsync(Execution execution, CancellationToken cancellationToken);
}

public sealed class AutoApproval : IApproval
{
    public Task<bool> ConfirmAsync(Execution execution, CancellationToken cancellationToken) => Task.FromResult(true);
}

public sealed class ConsoleApproval(bool auto) : IApproval
{
    public static string Escape(string text)
    {
        var result = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value is not (10 or 9) && (Rune.IsControl(rune) || category == System.Globalization.UnicodeCategory.Format))
                result.Append($"\\u{{{rune.Value:X}}}");
            else result.Append(rune.ToString());
        }
        return result.ToString();
    }

    public async Task<bool> ConfirmAsync(Execution execution, CancellationToken cancellationToken)
    {
        Console.WriteLine($"\nRequest {execution.Id}\nReason: {Escape(execution.Request.Reason)}\nScript:\n{Escape(execution.Script)}");
        if (auto) { Console.WriteLine("Auto mode: approved."); return true; }
        while (Console.KeyAvailable) Console.ReadKey(intercept: true);
        var input = new StringBuilder();
        Console.Write($"Approve {execution.Id}? [y/n] ");
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Console.KeyAvailable) { await Task.Delay(25, cancellationToken); continue; }
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    string answer = input.ToString().Trim(); input.Clear();
                    if (answer.Equals("y", StringComparison.OrdinalIgnoreCase)) return true;
                    if (answer.Equals("n", StringComparison.OrdinalIgnoreCase)) return false;
                    Console.Write($"Approve {execution.Id}? Enter y or n: ");
                }
                else if (key.Key == ConsoleKey.Backspace && input.Length > 0) { input.Length--; Console.Write("\b \b"); }
                else if (!char.IsControl(key.KeyChar) && input.Length < 32) { input.Append(key.KeyChar); Console.Write(key.KeyChar); }
            }
        }
        catch (OperationCanceledException) { Console.WriteLine($"\nApproval cancelled: {execution.Id}"); throw; }
    }
}
