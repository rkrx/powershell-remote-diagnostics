using System.Management.Automation.Language;
using System.Text;

namespace WindowsPowerShellRc;

public static class ScriptDisplay
{
    public static string Format(string script, string reason, int width)
    {
        script = script.Replace("\r\n", "\n");
        Parser.ParseInput(script, out var tokens, out var errors);
        var formatted = new StringBuilder();
        bool complex = tokens.Any(t => t.Kind is TokenKind.Semi or TokenKind.Pipe or TokenKind.LCurly or TokenKind.AtCurly);
        if (complex && !script.TrimStart().StartsWith('#') && !script.TrimStart().StartsWith("<#", StringComparison.Ordinal))
            foreach (string line in reason.Replace("\r\n", "\n").Split('\n')) formatted.Append("# ").Append(line).Append('\n');

        // This is a display projection only. Token text, including strings and
        // comments, is opaque; the submitted script is never rewritten for execution.
        if (errors.Length > 0) formatted.Append(script);
        else
        {
            int depth = 0, previousEnd = 0;
            bool continuation = false;
            var line = new StringBuilder();
            void Flush()
            {
                if (line.Length > 0) formatted.Append(line.ToString().TrimEnd()).Append('\n');
                line.Clear();
                continuation = false;
            }
            foreach (var token in tokens)
            {
                if (token.Kind == TokenKind.EndOfInput) break;
                if (token.Kind == TokenKind.NewLine) { Flush(); previousEnd = token.Extent.EndOffset; continue; }
                string gap = script[previousEnd..token.Extent.StartOffset];
                if (gap.Contains('\n')) Flush();
                if (token.Kind == TokenKind.RCurly) { Flush(); depth = Math.Max(0, depth - 1); }
                if (line.Length == 0) line.Append(' ', 4 * (depth + (continuation ? 1 : 0)));
                else if (gap.Length > 0) line.Append(' ');
                line.Append(token.Text);
                previousEnd = token.Extent.EndOffset;
                switch (token.Kind)
                {
                    case TokenKind.LCurly:
                    case TokenKind.AtCurly: Flush(); depth++; break;
                    case TokenKind.Semi: Flush(); break;
                    case TokenKind.Comment:
                        if (token.Text.StartsWith('#')) Flush();
                        break;
                    case TokenKind.Pipe: Flush(); continuation = true; break;
                }
            }
            Flush();
        }
        return Wrap(ConsoleApproval.Escape(formatted.ToString().TrimEnd('\r', '\n')).Replace("\t", "\\u{9}"), width);
    }

    private static string Wrap(string text, int width)
    {
        width = Math.Max(8, width);
        var result = new List<string>();
        foreach (string line in text.Split('\n'))
        {
            var runes = line.EnumerateRunes().ToArray();
            int offset = 0;
            bool continuation = false;
            while (offset < runes.Length)
            {
                string prefix = continuation ? "    ↪ " : "";
                int count = 0, cells = 0;
                while (offset + count < runes.Length && cells + CellWidth(runes[offset + count]) <= width - prefix.Length)
                    cells += CellWidth(runes[offset + count++]);
                // Prefer word boundaries; preserve every source character across wraps.
                if (offset + count < runes.Length)
                {
                    int boundary = count - 1;
                    while (boundary >= 0 && !Rune.IsWhiteSpace(runes[offset + boundary])) boundary--;
                    if (boundary >= count / 2) count = boundary + 1;
                }
                result.Add(prefix + string.Concat(runes.Skip(offset).Take(count).Select(r => r.ToString())));
                offset += count;
                continuation = true;
            }
            if (line.Length == 0) result.Add("");
        }
        return string.Join("\n", result);
    }

    private static int CellWidth(Rune rune)
    {
        if (Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark)
            return 0;
        int c = rune.Value;
        return c is >= 0x1100 and <= 0x115f or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3
            or >= 0xf900 and <= 0xfaff or >= 0xfe10 and <= 0xfe6f or >= 0xff01 and <= 0xff60
            or >= 0xffe0 and <= 0xffe6 or >= 0x1f300 and <= 0x1faff or >= 0x20000 and <= 0x3fffd ? 2 : 1;
    }
}
