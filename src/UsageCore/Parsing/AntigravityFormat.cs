using Microsoft.Data.Sqlite;

namespace UsageCore.Parsing;

/// <summary>
/// What is known of Antigravity's conversation files: one SQLite database per
/// conversation, its rows protobuf blobs with no published schema. Every field
/// number the parser reads is named here, and nowhere else, together with the
/// writer the demo data and the tests use - so a field that moves is changed in
/// one place and the fixtures move with it.
/// </summary>
/// <remarks>
/// <para>Worked out on 2026-11-03 from real files, by decoding the blobs without a
/// schema and matching their numbers against the token counts Antigravity also
/// writes to <c>brain\&lt;id&gt;\.system_generated\logs\transcript_full.jsonl</c>.
/// That transcript carries no model and no project, which is why it is not read.</para>
/// <para>Tables used: <c>trajectory_metadata_blob</c> (one row: workspace and
/// start time), <c>steps</c> (one row per step: <c>metadata</c> holds its time and,
/// on a model step, the usage again) and <c>gen_metadata</c> (one row per model
/// request: model, usage, the steps it produced, and string key/values naming
/// the parent conversation of a subagent). <c>step_payload</c>, which holds the
/// conversation's content, is never read.</para>
/// </remarks>
public static class AntigravityFormat
{
    // trajectory_metadata_blob.data
    internal const int MetaWorkspace = 1;          // message, repeated
    internal const int WorkspaceUri = 1;           //   string, file:///d:/path
    internal const int MetaCreated = 2;            // timestamp

    // google.protobuf.Timestamp
    internal const int TimeSeconds = 1;
    internal const int TimeNanos = 2;

    // steps.metadata
    internal const int StepCreated = 1;            // timestamp
    internal const int StepUsage = 9;              // usage, model steps only

    // gen_metadata.data
    internal const int GenSteps = 2;               // packed step indices this request produced
    internal const int GenRequest = 1;             // message
    internal const int RequestUsage = 4;           //   usage
    internal const int RequestModel = 19;          //   string, e.g. gemini-3.7-flash
    internal const int RequestModelName = 21;      //   string, e.g. "Gemini 3.7 Flash (High)"
    internal const int RequestLabel = 20;          //   message, repeated: { 1: key, 2: value }
    internal const int LabelKey = 1;
    internal const int LabelValue = 2;

    // usage, in gen_metadata and steps alike
    internal const int UsageModel = 1;             // Antigravity's own number for the model
    internal const int UsageInput = 2;             // uncached prompt only
    internal const int UsageOutput = 3;            // thinking included
    internal const int UsageCacheRead = 5;
    internal const int UsageThinking = 9;          // inside UsageOutput

    /// <summary>The label naming the conversation a subagent was started from.</summary>
    internal const string ParentLabel = "parent_cascade_id";
    internal const string SubagentLabel = "subagent_type";

    /// <summary>A conversation's tables, as Antigravity creates them (columns the parser reads).</summary>
    private const string Schema = """
        CREATE TABLE trajectory_metadata_blob (id text DEFAULT "main", data blob, PRIMARY KEY (id));
        CREATE TABLE steps (idx integer, step_type integer NOT NULL DEFAULT 0, status integer NOT NULL DEFAULT 0,
            metadata blob, step_payload blob, PRIMARY KEY (idx));
        CREATE TABLE gen_metadata (idx integer, data blob, size integer NOT NULL DEFAULT 0, PRIMARY KEY (idx));
        """;

    /// <summary>Step type of a model response, the step a request's usage is also written on.</summary>
    internal const int ModelStepType = 15;

    /// <summary>
    /// Step type of a browser subagent's tool call. Its usage is the whole
    /// subagent's, which is recorded nowhere else.
    /// </summary>
    internal const int BrowserSubagentStepType = 85;

    /// <summary>One model request, for <see cref="WriteConversation"/>.</summary>
    /// <param name="Model">The id Antigravity logs, or null for a request that never ran.</param>
    /// <param name="ToolSteps">Steps the request produced after its own, each a little later.</param>
    public sealed record DemoRequest(
        DateTimeOffset At,
        string? Model,
        string? ModelName,
        long Input,
        long Output,
        long CacheRead,
        long Thinking,
        int ToolSteps = 0,
        int ModelNumber = 0,
        DemoSubagent? Browser = null);

    /// <summary>A browser subagent's run, written as its own tool step's usage.</summary>
    public sealed record DemoSubagent(int ModelNumber, long Input, long Output, long CacheRead);

    /// <summary>
    /// Writes a conversation database in Antigravity's shape, for the demo data and
    /// the tests. Only the fields the parser reads are filled in; no content.
    /// </summary>
    public static void WriteConversation(
        string path,
        string? workspace,
        DateTimeOffset created,
        IReadOnlyList<DemoRequest> requests,
        string? parentCascadeId = null,
        string? subagentType = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path)) File.Delete(path);
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var tx = connection.BeginTransaction();
        Exec(connection, Schema);

        var meta = new ProtoWriter();
        if (workspace is not null) meta.Message(MetaWorkspace, w => w.String(WorkspaceUri, new Uri(workspace).AbsoluteUri));
        meta.Message(MetaCreated, w => Time(w, created));
        Insert(connection, "INSERT INTO trajectory_metadata_blob (id, data) VALUES ('main', $d)", meta.ToArray());

        // Step 0 is the user's prompt, then each request's own step and its tool steps.
        var step = 0;
        InsertStep(connection, step++, 101, created, usage: null);
        for (var i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            var steps = new List<long>();
            var usage = r.Model is null ? null : Usage(r);
            steps.Add(step);
            InsertStep(connection, step++, ModelStepType, r.At, usage);
            for (var t = 0; t < r.ToolSteps; t++)
            {
                steps.Add(step);
                InsertStep(connection, step++, 132, r.At.AddSeconds(2 + 3 * t), usage: null);
            }
            if (r.Browser is { } b)
            {
                steps.Add(step);
                var bu = new ProtoWriter();
                bu.Varint(UsageModel, (ulong)b.ModelNumber);
                bu.Varint(UsageInput, (ulong)b.Input);
                bu.Varint(UsageOutput, (ulong)b.Output);
                bu.Varint(UsageCacheRead, (ulong)b.CacheRead);
                InsertStep(connection, step++, BrowserSubagentStepType, r.At.AddSeconds(5 + 3 * r.ToolSteps), bu.ToArray());
            }

            var gen = new ProtoWriter();
            gen.Message(GenRequest, w =>
            {
                if (usage is not null) w.Bytes(RequestUsage, usage);
                if (r.Model is not null) w.String(RequestModel, r.Model);
                if (parentCascadeId is not null) Label(w, ParentLabel, parentCascadeId);
                if (subagentType is not null) Label(w, SubagentLabel, subagentType);
                if (r.ModelName is not null) w.String(RequestModelName, r.ModelName);
            });
            gen.Packed(GenSteps, steps);
            Insert(connection, "INSERT INTO gen_metadata (idx, data) VALUES (" + i + ", $d)", gen.ToArray());
        }
        tx.Commit();
    }

    private static byte[] Usage(DemoRequest r)
    {
        var w = new ProtoWriter();
        if (r.ModelNumber > 0) w.Varint(UsageModel, (ulong)r.ModelNumber);
        w.Varint(UsageInput, (ulong)r.Input);
        w.Varint(UsageOutput, (ulong)r.Output);
        if (r.CacheRead > 0) w.Varint(UsageCacheRead, (ulong)r.CacheRead);
        if (r.Thinking > 0) w.Varint(UsageThinking, (ulong)r.Thinking);
        return w.ToArray();
    }

    private static void Label(ProtoWriter w, string key, string value) =>
        w.Message(RequestLabel, l => { l.String(LabelKey, key); l.String(LabelValue, value); });

    private static void Time(ProtoWriter w, DateTimeOffset at)
    {
        var ms = at.ToUnixTimeMilliseconds();
        w.Varint(TimeSeconds, (ulong)(ms / 1000));
        w.Varint(TimeNanos, (ulong)(ms % 1000 * 1_000_000));
    }

    private static void InsertStep(SqliteConnection c, int idx, int type, DateTimeOffset at, byte[]? usage)
    {
        var meta = new ProtoWriter();
        meta.Message(StepCreated, w => Time(w, at));
        if (usage is not null) meta.Bytes(StepUsage, usage);
        Insert(c, $"INSERT INTO steps (idx, step_type, status, metadata) VALUES ({idx}, {type}, 3, $d)", meta.ToArray());
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Insert(SqliteConnection c, string sql, byte[] data)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$d", data);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>A malformed protobuf blob: one row is skipped, never the file.</summary>
internal sealed class ProtoException(string message) : Exception(message);

/// <summary>
/// Forward-only protobuf reading without a schema: field number, wire type and
/// value, nothing more. Unknown fields are skipped, so a field Antigravity adds
/// costs nothing; a field it moves shows up as a reconciliation failure.
/// </summary>
internal ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;

    public int Field { get; private set; }
    public int WireType { get; private set; }

    /// <summary>Bytes left: a packed list is read with <see cref="Varint"/> while this holds.</summary>
    public readonly bool HasMore => _pos < _data.Length;

    /// <summary>Moves to the next field; false at the end.</summary>
    public bool Next()
    {
        if (_pos >= _data.Length) return false;
        var key = Varint();
        Field = (int)(key >> 3);
        WireType = (int)(key & 7);
        if (Field == 0) throw new ProtoException("Field number 0.");
        return true;
    }

    public ulong Varint()
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (_pos >= _data.Length) throw new ProtoException("Truncated varint.");
            var b = _data[_pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new ProtoException("Varint over 64 bits.");
    }

    /// <summary>A varint field's value as a token count: never negative, never past long.</summary>
    public long Count() => WireType == 0 ? (long)Math.Min(Varint(), long.MaxValue) : SkipAndZero();

    public ReadOnlySpan<byte> Bytes()
    {
        if (WireType != 2) throw new ProtoException($"Field {Field} is not length-delimited.");
        var length = Varint();
        if (length > (ulong)(_data.Length - _pos)) throw new ProtoException("Length past the end.");
        var span = _data.Slice(_pos, (int)length);
        _pos += (int)length;
        return span;
    }

    public string String() => System.Text.Encoding.UTF8.GetString(Bytes());

    public void Skip()
    {
        switch (WireType)
        {
            case 0: Varint(); break;
            case 1: Advance(8); break;
            case 2: Bytes(); break;
            case 5: Advance(4); break;
            default: throw new ProtoException($"Wire type {WireType}.");
        }
    }

    private long SkipAndZero()
    {
        Skip();
        return 0;
    }

    private void Advance(int n)
    {
        if (_pos + n > _data.Length) throw new ProtoException("Truncated fixed field.");
        _pos += n;
    }

    /// <summary>A Timestamp message as Unix milliseconds; null when it has no seconds.</summary>
    public static long? TimestampMs(ReadOnlySpan<byte> data)
    {
        long seconds = 0, nanos = 0;
        var r = new ProtoReader(data);
        while (r.Next())
        {
            if (r.Field == AntigravityFormat.TimeSeconds) seconds = r.Count();
            else if (r.Field == AntigravityFormat.TimeNanos) nanos = r.Count();
            else r.Skip();
        }
        return seconds > 0 ? seconds * 1000 + nanos / 1_000_000 : null;
    }
}

/// <summary>The writing half, for fixtures only.</summary>
internal sealed class ProtoWriter
{
    private readonly MemoryStream _out = new();

    public void Varint(int field, ulong value)
    {
        Key(field, 0);
        Raw(value);
    }

    public void Bytes(int field, ReadOnlySpan<byte> value)
    {
        Key(field, 2);
        Raw((ulong)value.Length);
        _out.Write(value);
    }

    public void String(int field, string value) => Bytes(field, System.Text.Encoding.UTF8.GetBytes(value));

    public void Message(int field, Action<ProtoWriter> build)
    {
        var inner = new ProtoWriter();
        build(inner);
        Bytes(field, inner.ToArray());
    }

    public void Packed(int field, IEnumerable<long> values)
    {
        var inner = new ProtoWriter();
        foreach (var v in values) inner.Raw((ulong)v);
        Bytes(field, inner.ToArray());
    }

    public byte[] ToArray() => _out.ToArray();

    private void Key(int field, int wireType) => Raw((ulong)field << 3 | (uint)wireType);

    private void Raw(ulong value)
    {
        while (value >= 0x80)
        {
            _out.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _out.WriteByte((byte)value);
    }
}
