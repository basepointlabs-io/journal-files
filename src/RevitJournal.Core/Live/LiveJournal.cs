using System.Text;
using RevitJournal.Core.Model;
using RevitJournal.Core.Parsing;
using RevitJournal.Core.Rules;

namespace RevitJournal.Core.Live;

/// <summary>
/// Follows a journal that Revit is still writing: each <see cref="Poll"/> reads only the bytes appended since the
/// previous one and feeds complete lines to the parser.
/// </summary>
public sealed class LiveJournal : IDisposable
{
    private readonly FileStream _stream;
    private readonly JournalParser _parser;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private readonly char[] _chars;
    private Decoder _decoder;
    private readonly StringBuilder _partial = new();
    private bool _first = true;

    static LiveJournal() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public LiveJournal(string path, RuleMatcher matcher)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        _parser = new JournalParser(path, matcher);
        _decoder = Encoding.GetEncoding(1252).GetDecoder();
        _chars = new char[Encoding.UTF8.GetMaxCharCount(_buffer.Length)];
    }

    public string Path { get; }
    public JournalSession Session => _parser.Session;

    /// <summary>When the file last grew (local time).</summary>
    public DateTime LastGrowth { get; private set; } = DateTime.Now;

    /// <summary>Reads what Revit appended since the last call. Returns the number of new complete lines.</summary>
    public int Poll()
    {
        int lines = 0;
        int read;
        while ((read = _stream.Read(_buffer, 0, _buffer.Length)) > 0)
        {
            int offset = 0;
            if (_first)
            {
                _first = false;
                // Journals are ANSI; honour a UTF-8 byte order mark if a future Revit writes one.
                if (read >= 3 && _buffer[0] == 0xEF && _buffer[1] == 0xBB && _buffer[2] == 0xBF)
                {
                    _decoder = Encoding.UTF8.GetDecoder();
                    offset = 3;
                }
            }
            int count = _decoder.GetChars(_buffer, offset, read - offset, _chars, 0);
            lines += Split(_chars.AsSpan(0, count));
            LastGrowth = DateTime.Now;
        }
        return lines;
    }

    /// <summary>Feeds every complete line; keeps the unfinished tail for the next poll.</summary>
    private int Split(ReadOnlySpan<char> text)
    {
        int lines = 0;
        while (true)
        {
            int nl = text.IndexOf('\n');
            if (nl < 0)
            {
                _partial.Append(text);
                return lines;
            }
            _partial.Append(text[..nl]);
            if (_partial.Length > 0 && _partial[^1] == '\r')
                _partial.Length--;
            _parser.Feed(_partial.ToString());
            _partial.Clear();
            lines++;
            text = text[(nl + 1)..];
        }
    }

    public void Dispose() => _stream.Dispose();
}
