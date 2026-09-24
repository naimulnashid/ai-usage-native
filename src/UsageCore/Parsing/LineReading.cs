using System.Text.Json;

namespace UsageCore.Parsing;

/// <summary>
/// Reads a transcript as byte lines, without decoding them to strings.
/// </summary>
/// <remarks>
/// <para><b>Why not <c>ReadLine</c> and <c>JsonDocument</c>.</b> Transcript lines
/// can be megabytes long - a tool result is written into the line whole - and a
/// JsonDocument rents its buffers from the shared array pool, which then keeps
/// the largest of them for the life of the process. Measured on real data, that
/// held ~430 MB after the parse had finished and been collected: fatal for an
/// app that sits in the tray. This keeps one buffer per file, owned here and
/// dropped with the file, and the JSON is scanned in place (see
/// <see cref="Json"/>), so a message body is skipped without being materialised.</para>
/// </remarks>
internal sealed class ByteLineReader(Stream stream) : IDisposable
{
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;
    private bool _eof;
    private bool _first = true;

    /// <summary>The next line, trimmed, or false at the end of the file.</summary>
    public bool TryReadLine(out ReadOnlySpan<byte> line)
    {
        while (true)
        {
            var available = _buffer.AsSpan(_start, _end - _start);
            var newline = available.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                line = Trim(available[..newline]);
                _start += newline + 1;
                return true;
            }
            if (_eof)
            {
                if (_end > _start)
                {
                    line = Trim(available);
                    _start = _end;
                    return true;
                }
                line = default;
                return false;
            }
            Fill();
        }
    }

    private void Fill()
    {
        // Move what is left to the front, growing when one line fills the buffer.
        var pending = _end - _start;
        if (pending == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
        else if (_start > 0) Buffer.BlockCopy(_buffer, _start, _buffer, 0, pending);
        _start = 0;
        _end = pending;
        var read = stream.Read(_buffer, _end, _buffer.Length - _end);
        if (read == 0) _eof = true;
        _end += read;
        if (_first && _end >= 3 && _buffer[0] == 0xEF && _buffer[1] == 0xBB && _buffer[2] == 0xBF) _start = 3;
        _first = false;
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> span)
    {
        var s = 0;
        var e = span.Length;
        while (s < e && IsSpace(span[s])) s++;
        while (e > s && IsSpace(span[e - 1])) e--;
        return span[s..e];
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    public void Dispose() => stream.Dispose();

    public static ByteLineReader Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan));
}

/// <summary>
/// Forward-only helpers over <see cref="Utf8JsonReader"/>: read the few fields a
/// parser wants, skip everything else in place. A value that is not what a
/// field should be reads as "absent", the same degrade-never-crash rule the
/// guards apply; invalid JSON anywhere in the line throws JsonException, which
/// the caller counts as an unparseable line.
/// </summary>
internal static class Json
{
    /// <summary>Moves to the next property of the current object; false at its end.</summary>
    public static bool NextProperty(ref Utf8JsonReader r)
    {
        if (!r.Read()) throw new JsonException("Unexpected end of line.");
        if (r.TokenType == JsonTokenType.EndObject) return false;
        if (r.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a property.");
        return true;
    }

    /// <summary>Positions on the property's value, then skips it.</summary>
    public static void SkipValue(ref Utf8JsonReader r)
    {
        if (!r.Read()) throw new JsonException("Unexpected end of line.");
        r.Skip();
    }

    /// <summary>A non-empty string value, or null (the value is consumed either way).</summary>
    public static string? String(ref Utf8JsonReader r)
    {
        if (!r.Read()) throw new JsonException("Unexpected end of line.");
        if (r.TokenType == JsonTokenType.String)
        {
            var s = r.GetString();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        r.Skip();
        return null;
    }

    /// <summary>A token count by <see cref="Guards"/>' rules: non-negative whole number, else 0.</summary>
    public static long Count(ref Utf8JsonReader r)
    {
        if (!r.Read()) throw new JsonException("Unexpected end of line.");
        if (r.TokenType == JsonTokenType.Number)
        {
            if (!r.TryGetDouble(out var d) || !double.IsFinite(d) || d <= 0) return 0;
            return d >= long.MaxValue ? long.MaxValue : (long)Math.Floor(d);
        }
        r.Skip();
        return 0;
    }

    /// <summary>
    /// Moves onto the property's value; true when it is an object, positioned
    /// on its start. Anything else is skipped and reads as absent.
    /// </summary>
    public static bool EnterObject(ref Utf8JsonReader r)
    {
        if (!r.Read()) throw new JsonException("Unexpected end of line.");
        if (r.TokenType == JsonTokenType.StartObject) return true;
        r.Skip();
        return false;
    }

    /// <summary>Checks nothing follows the root value on the line.</summary>
    public static void End(ref Utf8JsonReader r)
    {
        if (r.Read()) throw new JsonException("Trailing content after the root value.");
    }
}
