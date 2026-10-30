using System.Text;
using System.Text.Json;

namespace WindowsPowerShellRc;

public sealed class OutputStore : IDisposable
{
    private readonly string directory;
    public OutputStore()
    {
        directory = Path.Combine(Path.GetTempPath(), "windows-powershell-rc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public StoredOutput Create(Guid id) => new(directory, id);
    public void Dispose() => Directory.Delete(directory, recursive: true);
}

internal record OutputRecord(string Stream, string Text);

public sealed partial class StoredOutput : IDisposable
{
    private readonly FileStream data;
    private readonly FileStream index;
    private readonly string dataPath;
    private readonly string indexPath;
    public Guid Id { get; }
    public long LineCount { get; private set; }
    private readonly object gate = new();
    private bool disposed;
    private long committedLength;
    internal StoredOutput(string directory, Guid id)
    {
        Id = id;
        dataPath = Path.Combine(directory, id + ".jsonl");
        indexPath = Path.Combine(directory, id + ".idx");
        data = new FileStream(dataPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try { index = new FileStream(indexPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read); }
        catch { data.Dispose(); File.Delete(dataPath); throw; }
    }

    public void Append(string stream, string text)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // ReadLine preserves empty logical lines without inventing one for a trailing newline.
            using var reader = new StringReader(text);
            string? line;
            bool any = false;
            while ((line = reader.ReadLine()) is not null) { AppendLine(stream, line); any = true; }
            if (!any) AppendLine(stream, "");
        }
    }

    private void AppendLine(string stream, string line)
    {
        data.Position = committedLength;
        long offset = data.Position;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new OutputRecord(stream, line));
        try
        {
            data.Write(bytes);
            data.WriteByte((byte)'\n');
            data.SetLength(data.Position);
            data.Flush();
            long end = data.Position;
            index.Position = LineCount * 8;
            Span<byte> position = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(position, offset);
            index.Write(position);
            index.SetLength(index.Position);
            index.Flush();
            committedLength = end;
            LineCount++;
        }
        catch
        {
            // Readers use committedLength even if a full disk prevents rollback.
            try { data.SetLength(committedLength); index.SetLength(LineCount * 8); } catch (IOException) { }
            throw;
        }
    }

    private OutputRecord Read(long line)
    {
        if (line < 1 || line > LineCount) throw new ArgumentException("Line is outside the stored output.");
        index.Position = (line - 1) * 8;
        Span<byte> position = stackalloc byte[8];
        index.ReadExactly(position);
        long offset = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(position);
        long end = committedLength;
        if (line < LineCount)
        {
            index.ReadExactly(position);
            end = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(position);
        }
        data.Position = offset;
        using var slice = new MemoryStream();
        long remaining = end - offset;
        var buffer = new byte[8192];
        while (remaining > 0)
        {
            int n = data.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (n == 0) throw new EndOfStreamException("Incomplete output record.");
            slice.Write(buffer, 0, n);
            remaining -= n;
        }
        try { return JsonSerializer.Deserialize<OutputRecord>(slice.GetBuffer().AsSpan(0, (int)slice.Length))!; }
        catch (JsonException ex) { throw new IOException("Stored output record is damaged.", ex); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            data.Dispose(); index.Dispose();
            try { File.Delete(dataPath); }
            finally { File.Delete(indexPath); }
        }
    }
}
