using System.IO;
using System.Text;

namespace RevitJournalViewer;

/// <summary>One complete line read from the journal, with its 1-based line number in the file.</summary>
public readonly record struct JournalLine(long Number, string Text);

/// <summary>
/// Reads a file that another process is still writing, returning whole new lines on each poll.
/// Opened with full sharing so Revit is never blocked.
/// </summary>
public sealed class JournalTailer : IDisposable
{
    private readonly FileStream _stream;
    private readonly StreamReader _reader;
    private readonly StringBuilder _partial = new();
    private readonly char[] _buffer = new char[64 * 1024];

    private long _nextNumber;   // file line number of the next line to be returned
    private bool _discardFirst; // started mid-line: drop that partial line

    public string Path { get; }

    private static readonly Encoding AnsiEncoding = CreateAnsiEncoding();

    private static Encoding CreateAnsiEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    /// <param name="path">Journal file.</param>
    /// <param name="startBytesFromEnd">
    /// How much existing content to show first. 0 = start at the end (live only),
    /// long.MaxValue = whole file.
    /// </param>
    public JournalTailer(string path, long startBytesFromEnd)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);

        long length = _stream.Length;
        long start = startBytesFromEnd >= length ? 0 : length - startBytesFromEnd;

        // Count the lines we skip so line numbers match the file.
        long newlinesBefore = CountNewlines(_stream, start);
        bool atLineStart = start == 0 || ByteAt(_stream, start - 1) == (byte)'\n';
        _discardFirst = !atLineStart;
        _nextNumber = newlinesBefore + (atLineStart ? 1 : 2);

        _stream.Seek(start, SeekOrigin.Begin);
        // Revit journals are ANSI (Windows-1252), e.g. non-breaking spaces in add-in names.
        // A BOM, if present, still wins.
        _reader = new StreamReader(_stream, AnsiEncoding, detectEncodingFromByteOrderMarks: true);
    }

    /// <summary>Returns complete lines written since the last call (may be empty).</summary>
    public List<JournalLine> ReadNewLines()
    {
        var lines = new List<JournalLine>();
        int read;
        // StreamReader keeps returning new data as the file grows.
        while ((read = _reader.Read(_buffer, 0, _buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char c = _buffer[i];
                if (c != '\n')
                {
                    _partial.Append(c);
                    continue;
                }

                if (_partial.Length > 0 && _partial[^1] == '\r') _partial.Length--;
                if (_discardFirst)
                    _discardFirst = false;
                else
                    lines.Add(new JournalLine(_nextNumber++, _partial.ToString()));
                _partial.Clear();
            }
        }
        return lines;
    }

    private static long CountNewlines(FileStream s, long upTo)
    {
        if (upTo <= 0) return 0;
        s.Seek(0, SeekOrigin.Begin);
        var buf = new byte[1 << 20];
        long count = 0, remaining = upTo;
        while (remaining > 0)
        {
            int n = s.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
            if (n <= 0) break;
            count += buf.AsSpan(0, n).Count((byte)'\n');
            remaining -= n;
        }
        return count;
    }

    private static int ByteAt(FileStream s, long offset)
    {
        s.Seek(offset, SeekOrigin.Begin);
        return s.ReadByte();
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
