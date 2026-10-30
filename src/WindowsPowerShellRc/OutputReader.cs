using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WindowsPowerShellRc;

public record OutputLine(long Number, string Stream, string Text, int TotalCharacters, bool IsLongLine, bool Truncated, int CharOffset);
public record OutputPage(IReadOnlyList<OutputLine> Lines, long TotalLines, long? NextLine, int? NextCharOffset = null);
public record SearchOptions(string Q, bool IgnoreCase = false, int BeforeLines = 0, int AfterLines = 0,
    int BeforeChars = 0, int AfterChars = 0, int Limit = 20, string? Cursor = null);
public record SearchMatch(long Line, int CharOffset, int MatchLength, string Context, int ContextOffset, bool ContextTruncated, IReadOnlyList<OutputLine> Lines);
public record SearchPage(IReadOnlyList<SearchMatch> Matches, string? NextCursor);
internal record SearchCursor(Guid Id, string QueryHash, long Line, int Utf16Offset);

public sealed partial class StoredOutput
{
    private const int TextBudget = 256 * 1024;
    private static int Characters(string text) => text.EnumerateRunes().Count();
    private static string Slice(string text, int offset, int count) => string.Concat(text.EnumerateRunes().Skip(offset).Take(count).Select(r => r.ToString()));
    private static OutputLine Format(long number, OutputRecord record, int offset = 0, int count = 4096)
    {
        int length = Characters(record.Text);
        if (offset > length) throw new ArgumentException("Character offset exceeds line length.");
        string text = Slice(record.Text, offset, count);
        return new(number, record.Stream, text, length, length > 4096, offset + Characters(text) < length, offset);
    }

    public OutputPage ReadPage(long startLine = 1, int lineCount = 100, long? line = null, int charOffset = 0, int charCount = 4096)
    {
        if (startLine < 1 || lineCount is < 1 or > 200 || charOffset < 0 || charCount is < 1 or > 65536 || line < 1)
            throw new ArgumentException("Invalid output range; lineCount 1–200 and charCount 1–65536.");
        if (line is null && (charOffset != 0 || charCount != 4096)) throw new ArgumentException("Character ranges require line.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (line is not null)
            {
                var single = Format(line.Value, Read(line.Value), charOffset, charCount);
                int? nextChar = single.Truncated ? charOffset + Characters(single.Text) : null;
                return new([single], LineCount, single.Truncated ? line : null, nextChar);
            }
            var lines = new List<OutputLine>();
            int budget = TextBudget;
            long next = startLine;
            for (; next <= LineCount && lines.Count < lineCount; next++)
            {
                var item = Format(next, Read(next));
                int bytes = Encoding.UTF8.GetByteCount(item.Text);
                if (bytes > budget) break;
                lines.Add(item); budget -= bytes;
            }
            return new(lines, LineCount, next <= LineCount ? next : null);
        }
    }

    public SearchPage Search(SearchOptions options)
    {
        if (string.IsNullOrEmpty(options.Q) || options.Q.Contains('\n') || options.Q.Contains('\r') ||
            options.BeforeLines is < 0 or > 20 || options.AfterLines is < 0 or > 20 ||
            options.BeforeChars is < 0 or > 4096 || options.AfterChars is < 0 or > 4096 || options.Limit is < 1 or > 100)
            throw new ArgumentException("Invalid search parameters.");
        string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(options with { Cursor = null })));
        long nextLine = 1; int nextOffset = 0;
        if (options.Cursor is not null)
        {
            try
            {
                var cursor = JsonSerializer.Deserialize<SearchCursor>(Convert.FromBase64String(options.Cursor))!;
                if (cursor.Id != Id || cursor.QueryHash != hash || cursor.Line < 1 || cursor.Utf16Offset < 0) throw new FormatException();
                nextLine = cursor.Line; nextOffset = cursor.Utf16Offset;
            }
            catch (Exception ex) when (ex is FormatException or JsonException or NullReferenceException) { throw new ArgumentException("Cursor does not match this search."); }
        }
        string Cursor(long line, int offset) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new SearchCursor(Id, hash, line, offset)));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var matches = new List<SearchMatch>();
            int budget = TextBudget;
            for (long n = nextLine; n <= LineCount; n++, nextOffset = 0)
            {
                var record = Read(n);
                if (nextOffset > record.Text.Length) throw new ArgumentException("Invalid cursor position.");
                int pos = nextOffset;
                while ((pos = record.Text.IndexOf(options.Q, pos, options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) >= 0)
                {
                    int offset = Characters(record.Text[..pos]);
                    int length = Characters(record.Text.Substring(pos, options.Q.Length));
                    int contextOffset = Math.Max(0, offset - options.BeforeChars);
                    // Very large literals retain the full hit position/length; retrieve text via output segments.
                    int wanted = offset - contextOffset + length + options.AfterChars;
                    string context = Slice(record.Text, contextOffset, Math.Min(wanted, 16384));
                    var contextLines = new List<OutputLine>();
                    if (options.BeforeLines != 0 || options.AfterLines != 0)
                        for (long l = Math.Max(1, n - options.BeforeLines); l <= Math.Min(LineCount, n + options.AfterLines); l++)
                            contextLines.Add(Format(l, Read(l)));
                    int bytes = Encoding.UTF8.GetByteCount(context) + contextLines.Sum(l => Encoding.UTF8.GetByteCount(l.Text));
                    if (bytes > budget)
                    {
                        if (matches.Count > 0) return new(matches, Cursor(n, pos));
                        // Share the remaining text budget without dropping requested line coordinates.
                        int remaining = budget - Encoding.UTF8.GetByteCount(context);
                        for (int i = 0; i < contextLines.Count; i++)
                        {
                            var item = contextLines[i];
                            int share = remaining / (contextLines.Count - i);
                            string shorter = Encoding.UTF8.GetByteCount(item.Text) <= share ? item.Text : Slice(item.Text, 0, share / 4);
                            contextLines[i] = item with { Text = shorter, Truncated = shorter.Length < item.Text.Length || item.Truncated };
                            remaining -= Encoding.UTF8.GetByteCount(shorter);
                        }
                        bytes = Encoding.UTF8.GetByteCount(context) + contextLines.Sum(l => Encoding.UTF8.GetByteCount(l.Text));
                    }
                    matches.Add(new(n, offset, length, context, contextOffset, Characters(context) < Math.Min(wanted, Characters(record.Text) - contextOffset), contextLines));
                    budget -= bytes;
                    pos += options.Q.Length;
                    if (matches.Count == options.Limit) return new(matches, Cursor(n, pos));
                }
            }
            return new(matches, null);
        }
    }
}
