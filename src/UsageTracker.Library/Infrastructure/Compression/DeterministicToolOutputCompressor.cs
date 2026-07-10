using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UsageTracker;

/// <summary>
/// Default <see cref="IToolOutputCompressor"/>: a single-file, deterministic, lossless-by-policy
/// compactor. It never truncates, samples, drops, or redacts - it only rewrites redundancy into a
/// more compact, information-complete representation (run-length encoding, dictionary/reference
/// encoding, and JSON table/columnar factoring). Every strategy below is self-verifying: it decodes
/// its own candidate text and compares it against the original before accepting it, and rejects
/// (falls through to another strategy, or <see cref="ToolOutputCompression.Unchanged"/>) on any
/// mismatch, parse failure, or when the candidate isn't actually smaller. Content-type detection is
/// deliberately dumb (regex/parse-based, no ML, no scoring model) - see docs/v2 for the design
/// rationale that ruled out lossy/summarizing approaches.
/// </summary>
public sealed class DeterministicToolOutputCompressor : IToolOutputCompressor
{
    private const int MinimumInputLength = 64;

    private static readonly Regex LogTokenPattern = new(
        @"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?\b" +
        @"|\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b" +
        @"|""[^""]*""" +
        @"|\b\d+(?:\.\d+)?\b",
        RegexOptions.Compiled);

    private static readonly Regex CodeMarkerPattern = new(
        @"\b(namespace|using|class|public|private|function|def|import|const)\b|=>|;\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly char[] TableDelimiterCandidates = [',', '\t', '|', ';'];

    public Task<ToolOutputCompression> CompressAsync(string toolOutput, string? model, CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(Compress(toolOutput));
        }
        catch
        {
            return Task.FromResult(ToolOutputCompression.Unchanged(toolOutput));
        }
    }

    private static ToolOutputCompression Compress(string input)
    {
        if (string.IsNullOrEmpty(input) || input.Length < MinimumInputLength)
            return ToolOutputCompression.Unchanged(input);

        var label = CodeMarkerPattern.IsMatch(input) ? "code" : "text";

        string? best = null;
        foreach (var candidate in new[]
        {
            SafeTry(() => TryCompressJson(input)),
            SafeTry(() => TryCompressJsonLines(input)),
            SafeTry(() => TryCompressDelimitedTable(input)),
            SafeTry(() => TryCompressLog(input)),
            SafeTry(() => TryCompressLines(input, label)),
        })
        {
            if (candidate is null || candidate.Length >= input.Length)
                continue;
            if (best is null || candidate.Length < best.Length)
                best = candidate;
        }

        return best is null
            ? ToolOutputCompression.Unchanged(input)
            : new ToolOutputCompression(true, best, EstimateTokens(input), EstimateTokens(best));
    }

    // No tokenizer dependency exists in this project; ~4 chars/token is a standard rough estimate
    // used only to size TokensBefore/TokensAfter for observability, not for correctness.
    private static long EstimateTokens(string text) => Math.Max(1, text.Length / 4);

    private static string? SafeTry(Func<string?> fn)
    {
        try { return fn(); }
        catch { return null; }
    }

    // ---------------------------------------------------------------------------------------
    // JSON: table factoring for arrays of homogeneous objects, reference encoding otherwise.
    // ---------------------------------------------------------------------------------------

    private static string? TryCompressJson(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(trimmed); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object && root.ValueKind != JsonValueKind.Array)
                return null;

            string? table = root.ValueKind == JsonValueKind.Array
                ? TryBuildJsonTable(root.EnumerateArray().ToList())
                : null;

            var reference = TryBuildJsonReferenceEncoding(root);

            if (table is not null && (reference is null || table.Length <= reference.Length))
                return table;
            return reference;
        }
    }

    private static string? TryBuildJsonTable(List<JsonElement> items, string tag = "json-table")
    {
        if (items.Count < 2 || items.Any(i => i.ValueKind != JsonValueKind.Object))
            return null;

        var keys = new List<string>();
        var keySet = new HashSet<string>();
        foreach (var item in items)
            foreach (var prop in item.EnumerateObject())
                if (keySet.Add(prop.Name)) keys.Add(prop.Name);

        if (keys.Count == 0 || keys.Any(k => k.Contains('\t') || k.Contains('\n')))
            return null;

        var valueFreq = new Dictionary<string, int>();
        foreach (var item in items)
            foreach (var prop in item.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var raw = prop.Value.GetRawText();
                    if (raw.Length >= 6)
                        valueFreq[raw] = valueFreq.GetValueOrDefault(raw) + 1;
                }

        var dict = valueFreq.Where(kv => kv.Value >= 2)
            .OrderByDescending(kv => kv.Value * kv.Key.Length)
            .Take(256)
            .Select((kv, idx) => (kv.Key, Id: idx))
            .ToDictionary(x => x.Key, x => x.Id);

        string EncodeCell(JsonElement value)
        {
            var raw = value.GetRawText();
            return dict.TryGetValue(raw, out var id) ? $"#{id}" : raw;
        }

        var sb = new StringBuilder();
        sb.Append("@ut/").Append(tag).Append("/v1\n--keys--\n").Append(string.Join('\t', keys)).Append('\n');

        sb.Append("--dict--\n");
        foreach (var kv in dict.OrderBy(x => x.Value))
            sb.Append(kv.Value).Append('\t').Append(kv.Key).Append('\n');

        sb.Append("--rows--\n");
        foreach (var item in items)
        {
            var cells = new string[keys.Count];
            for (var i = 0; i < keys.Count; i++)
                cells[i] = item.TryGetProperty(keys[i], out var value) ? EncodeCell(value) : "null";
            sb.Append(string.Join('\t', cells)).Append('\n');
        }

        var text = sb.ToString();
        return VerifyJsonTable(text, items) ? text : null;
    }

    private static bool VerifyJsonTable(string text, List<JsonElement> originalItems)
    {
        if (!TryParseJsonTable(text, out var keys, out var dict, out var rows))
            return false;
        if (rows.Count != originalItems.Count)
            return false;

        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Length != keys.Length)
                return false;

            var pairs = new string[keys.Length];
            for (var k = 0; k < keys.Length; k++)
            {
                var cell = rows[i][k];
                var raw = cell.Length > 1 && cell[0] == '#' && int.TryParse(cell.AsSpan(1), out var refId) && dict.TryGetValue(refId, out var dv)
                    ? dv
                    : cell;
                pairs[k] = JsonSerializer.Serialize(keys[k]) + ":" + raw;
            }

            JsonDocument reconstructed;
            try { reconstructed = JsonDocument.Parse("{" + string.Join(',', pairs) + "}"); }
            catch (JsonException) { return false; }

            using (reconstructed)
            {
                if (!JsonElementDeepEquals(reconstructed.RootElement, originalItems[i]))
                    return false;
            }
        }

        return true;
    }

    private static bool TryParseJsonTable(string text, out string[] keys, out Dictionary<int, string> dict, out List<string[]> rows)
    {
        keys = Array.Empty<string>();
        dict = new Dictionary<int, string>();
        rows = new List<string[]>();
        var section = "";

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line is "--keys--" or "--dict--" or "--rows--") { section = line; continue; }

            switch (section)
            {
                case "--keys--":
                    keys = line.Split('\t');
                    break;
                case "--dict--":
                {
                    var idx = line.IndexOf('\t');
                    if (idx < 0 || !int.TryParse(line[..idx], out var id)) return false;
                    dict[id] = line[(idx + 1)..];
                    break;
                }
                case "--rows--":
                    rows.Add(line.Split('\t'));
                    break;
            }
        }

        return keys.Length > 0;
    }

    /// <summary>
    /// Reference-encodes repeated object/array subtrees and repeated string values by exact,
    /// whitespace-normalized text match. Safe by construction: JSON scalar raw text can never start
    /// with '#' (JSON tokens start with one of <c>{[\"-0-9tfn</c>), so the "#N" reference token can
    /// never collide with real JSON content - and the final decode-and-compare still guards against
    /// any residual mistake.
    /// </summary>
    private static string? TryBuildJsonReferenceEncoding(JsonElement root)
    {
        var canonicalRoot = Canonical(root);
        var freq = new Dictionary<string, int>();
        CollectCandidates(root, freq, isRoot: true);

        var refs = freq.Where(kv => kv.Value >= 2 && kv.Key.Length >= 12)
            .OrderByDescending(kv => kv.Key.Length)
            .Take(64)
            .Select((kv, idx) => (Text: kv.Key, Id: idx))
            .ToList();

        if (refs.Count == 0)
            return null;

        var body = canonicalRoot;
        foreach (var r in refs)
            body = body.Replace(r.Text, $"#{r.Id}");

        var sb = new StringBuilder();
        sb.Append("@ut/json-ref/v1\n--refs--\n");
        foreach (var r in refs)
            sb.Append(r.Id).Append('\t').Append(r.Text).Append('\n');
        sb.Append("--data--\n").Append(body).Append('\n');

        var text = sb.ToString();
        return VerifyJsonRef(text, canonicalRoot) ? text : null;
    }

    private static void CollectCandidates(JsonElement e, Dictionary<string, int> freq, bool isRoot)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (!isRoot) Track(Canonical(e));
                foreach (var prop in e.EnumerateObject())
                    CollectCandidates(prop.Value, freq, false);
                break;
            case JsonValueKind.Array:
                if (!isRoot) Track(Canonical(e));
                foreach (var item in e.EnumerateArray())
                    CollectCandidates(item, freq, false);
                break;
            case JsonValueKind.String:
                var raw = e.GetRawText();
                if (raw.Length >= 6) Track(raw);
                break;
        }

        void Track(string text) => freq[text] = freq.GetValueOrDefault(text) + 1;
    }

    private static string Canonical(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var sb = new StringBuilder("{");
                var first = true;
                foreach (var prop in e.EnumerateObject())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(prop.Name)).Append(':').Append(Canonical(prop.Value));
                }
                return sb.Append('}').ToString();
            }
            case JsonValueKind.Array:
            {
                var sb = new StringBuilder("[");
                var first = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Canonical(item));
                }
                return sb.Append(']').ToString();
            }
            default:
                return e.GetRawText();
        }
    }

    private static bool VerifyJsonRef(string text, string expectedCanonical)
    {
        var refs = new Dictionary<int, string>();
        string? body = null;
        var section = "";

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line is "--refs--" or "--data--") { section = line; continue; }

            if (section == "--refs--")
            {
                var idx = line.IndexOf('\t');
                if (idx < 0 || !int.TryParse(line[..idx], out var id)) return false;
                refs[id] = line[(idx + 1)..];
            }
            else if (section == "--data--")
            {
                body = line;
            }
        }

        if (body is null) return false;

        var reconstructed = body;
        foreach (var kv in refs.OrderBy(x => x.Key))
            reconstructed = reconstructed.Replace($"#{kv.Key}", kv.Value);

        return reconstructed == expectedCanonical;
    }

    /// <summary>
    /// Structural equality treating an absent object key as equivalent to a present key with a
    /// JSON null value - matches how <see cref="TryBuildJsonTable"/> represents optional keys.
    /// </summary>
    private static bool JsonElementDeepEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
            return false;

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var keys = a.EnumerateObject().Select(p => p.Name)
                    .Union(b.EnumerateObject().Select(p => p.Name));
                foreach (var key in keys)
                {
                    var hasA = a.TryGetProperty(key, out var av);
                    var hasB = b.TryGetProperty(key, out var bv);
                    var aIsNull = !hasA || av.ValueKind == JsonValueKind.Null;
                    var bIsNull = !hasB || bv.ValueKind == JsonValueKind.Null;
                    if (aIsNull || bIsNull)
                    {
                        if (aIsNull != bIsNull) return false;
                        continue;
                    }
                    if (!JsonElementDeepEquals(av, bv)) return false;
                }
                return true;
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength()) return false;
                return a.EnumerateArray().Zip(b.EnumerateArray(), JsonElementDeepEquals).All(x => x);
            default:
                return a.GetRawText() == b.GetRawText();
        }
    }

    // ---------------------------------------------------------------------------------------
    // JSON Lines / NDJSON: same table factoring as JSON arrays, applied across parsed lines.
    // ---------------------------------------------------------------------------------------

    private static string? TryCompressJsonLines(string input)
    {
        var lines = input.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 3) return null;

        var docs = new List<JsonDocument>();
        try
        {
            var parsed = new List<JsonElement>();
            foreach (var line in lines)
            {
                try
                {
                    var doc = JsonDocument.Parse(line.Trim());
                    docs.Add(doc);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        parsed.Add(doc.RootElement);
                }
                catch (JsonException) { /* not a JSON line - ignore */ }
            }

            if (parsed.Count < 3 || parsed.Count < lines.Count * 0.7)
                return null;

            return TryBuildJsonTable(parsed, "jsonl-table");
        }
        finally
        {
            foreach (var d in docs) d.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Delimited tables (CSV/TSV/PSV): column-wise value dictionary + row run-length encoding.
    // ---------------------------------------------------------------------------------------

    private static string? TryCompressDelimitedTable(string input)
    {
        // Quoted-field CSV escaping isn't handled in this v1 - skip anything with quotes rather
        // than risk misreading a delimiter that's actually inside a quoted field.
        if (input.Contains('"'))
            return null;

        var lines = input.Split('\n');
        var nonEmpty = lines.Where(l => l.Length > 0).ToList();
        if (nonEmpty.Count < 3) return null;

        foreach (var delimiter in TableDelimiterCandidates)
        {
            var fieldCounts = nonEmpty.Select(l => l.Count(c => c == delimiter) + 1).ToList();
            var mode = fieldCounts.GroupBy(c => c).OrderByDescending(g => g.Count()).First();
            if (mode.Key < 2 || mode.Count() < nonEmpty.Count * 0.8)
                continue;

            var table = BuildDelimitedTable(lines, delimiter);
            if (table is not null)
                return table;
        }

        return null;
    }

    private static string? BuildDelimitedTable(string[] lines, char delimiter)
    {
        var rowsFields = lines.Select(l => l.Split(delimiter)).ToList();

        var freq = new Dictionary<string, int>();
        foreach (var fields in rowsFields)
            foreach (var f in fields)
                if (f.Length >= 3)
                    freq[f] = freq.GetValueOrDefault(f) + 1;

        var dict = freq.Where(kv => kv.Value >= 2)
            .OrderByDescending(kv => kv.Value * kv.Key.Length)
            .Take(256)
            .Select((kv, idx) => (kv.Key, Id: idx))
            .ToDictionary(x => x.Key, x => x.Id);

        string EncodeRow(string[] fields) => string.Join('\t', fields.Select(f =>
            dict.TryGetValue(f, out var id) ? $"R\t{id}" : $"L\t{EscapeField(f)}"));

        var encodedRows = rowsFields.Select(EncodeRow).ToList();
        var runs = RunLengthEncode(encodedRows);

        var sb = new StringBuilder();
        sb.Append("@ut/table/v1\n--delimiter--\n").Append(DescribeDelimiter(delimiter)).Append('\n');

        sb.Append("--dict--\n");
        foreach (var kv in dict.OrderBy(x => x.Value))
            sb.Append(kv.Value).Append('\t').Append(EscapeField(kv.Key)).Append('\n');

        sb.Append("--rows--\n");
        foreach (var (count, content) in runs)
            sb.Append(count).Append('\t').Append(content).Append('\n');

        var text = sb.ToString();
        return VerifyDelimitedTable(text, lines, delimiter) ? text : null;
    }

    private static bool VerifyDelimitedTable(string text, string[] originalLines, char delimiter)
    {
        var dict = new Dictionary<int, string>();
        var runs = new List<(int Count, string[] Parts)>();
        var section = "";

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line is "--delimiter--" or "--dict--" or "--rows--") { section = line; continue; }

            if (section == "--dict--")
            {
                var idx = line.IndexOf('\t');
                if (idx < 0 || !int.TryParse(line[..idx], out var id)) return false;
                dict[id] = UnescapeField(line[(idx + 1)..]);
            }
            else if (section == "--rows--")
            {
                var parts = line.Split('\t');
                if (parts.Length < 1 || !int.TryParse(parts[0], out var count)) return false;
                runs.Add((count, parts[1..]));
            }
        }

        var reconstructed = new List<string>();
        foreach (var (count, parts) in runs)
        {
            if (parts.Length % 2 != 0) return false;

            var fields = new string[parts.Length / 2];
            for (var i = 0; i < fields.Length; i++)
            {
                var kind = parts[i * 2];
                var payload = parts[i * 2 + 1];
                fields[i] = kind == "R"
                    ? (dict.TryGetValue(int.Parse(payload), out var v) ? v : "")
                    : UnescapeField(payload);
            }

            var row = string.Join(delimiter, fields);
            for (var k = 0; k < count; k++) reconstructed.Add(row);
        }

        return reconstructed.SequenceEqual(originalLines);
    }

    private static string DescribeDelimiter(char c) => c == '\t' ? "\\t" : c.ToString();

    // ---------------------------------------------------------------------------------------
    // Logs: split each line into a template (static text) and captured variables (dynamic
    // tokens), then group lines that share a template. Every timestamp/id/value stays present -
    // only the repeated static structure is factored out.
    // ---------------------------------------------------------------------------------------

    private static string? TryCompressLog(string input)
    {
        var lines = input.Split('\n');
        if (lines.Length < 4) return null;

        var perLine = new (string Template, List<string> Values)[lines.Length];
        var templateCounts = new Dictionary<string, int>();

        for (var i = 0; i < lines.Length; i++)
        {
            var values = new List<string>();
            var template = LogTokenPattern.Replace(lines[i], m => { values.Add(m.Value); return "{}"; });
            perLine[i] = (template, values);
            if (values.Count > 0)
                templateCounts[template] = templateCounts.GetValueOrDefault(template) + 1;
        }

        var templateIds = templateCounts.Where(kv => kv.Value >= 2).Select(kv => kv.Key).ToList();
        if (templateIds.Count == 0) return null;

        var templateIndex = templateIds.Select((t, idx) => (t, idx)).ToDictionary(x => x.t, x => x.idx);

        var sb = new StringBuilder();
        sb.Append("@ut/log/v1\n--templates--\n");
        foreach (var t in templateIds)
            sb.Append(templateIndex[t]).Append('\t').Append(EscapeField(t)).Append('\n');

        sb.Append("--events--\n");
        for (var i = 0; i < lines.Length; i++)
        {
            var (template, values) = perLine[i];
            if (values.Count > 0 && templateIndex.TryGetValue(template, out var tid))
            {
                sb.Append("T\t").Append(tid);
                foreach (var v in values) sb.Append('\t').Append(EscapeField(v));
                sb.Append('\n');
            }
            else
            {
                sb.Append("L\t").Append(EscapeField(lines[i])).Append('\n');
            }
        }

        var text = sb.ToString();
        return VerifyLog(text, lines) ? text : null;
    }

    private static bool VerifyLog(string text, string[] originalLines)
    {
        try
        {
            var templates = new Dictionary<int, string>();
            var reconstructed = new List<string>();
            var section = "";

            foreach (var line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                if (line is "--templates--" or "--events--") { section = line; continue; }

                if (section == "--templates--")
                {
                    var idx = line.IndexOf('\t');
                    templates[int.Parse(line[..idx])] = UnescapeField(line[(idx + 1)..]);
                }
                else if (section == "--events--")
                {
                    var parts = line.Split('\t');
                    if (parts[0] == "L")
                    {
                        reconstructed.Add(UnescapeField(parts[1]));
                    }
                    else
                    {
                        var template = templates[int.Parse(parts[1])];
                        var values = parts[2..].Select(UnescapeField).ToArray();
                        var segments = template.Split("{}");
                        var lineSb = new StringBuilder(segments[0]);
                        for (var k = 0; k < values.Length; k++)
                            lineSb.Append(values[k]).Append(segments[k + 1]);
                        reconstructed.Add(lineSb.ToString());
                    }
                }
            }

            return reconstructed.SequenceEqual(originalLines);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Generic fallback (code and plain text): run-length encoding of consecutive repeated lines,
    // plus a dictionary for non-adjacent repeats.
    // ---------------------------------------------------------------------------------------

    private static string? TryCompressLines(string input, string label)
    {
        var lines = input.Split('\n');
        if (lines.Length < 4) return null;

        var runs = RunLengthEncode(lines.ToList());

        var contentFreq = new Dictionary<string, int>();
        foreach (var r in runs)
            if (r.Content.Length >= 12)
                contentFreq[r.Content] = contentFreq.GetValueOrDefault(r.Content) + 1;

        var dict = contentFreq.Where(kv => kv.Value >= 2)
            .OrderByDescending(kv => kv.Value * kv.Key.Length)
            .Take(256)
            .Select((kv, idx) => (kv.Key, Id: idx))
            .ToDictionary(x => x.Key, x => x.Id);

        if (dict.Count == 0 && runs.Count == lines.Length)
            return null; // nothing collapsed and nothing shared - guaranteed not to shrink

        var sb = new StringBuilder();
        sb.Append("@ut/").Append(label).Append("/v1\n--dict--\n");
        foreach (var kv in dict.OrderBy(x => x.Value))
            sb.Append(kv.Value).Append('\t').Append(EscapeField(kv.Key)).Append('\n');

        sb.Append("--runs--\n");
        foreach (var (count, content) in runs)
        {
            if (dict.TryGetValue(content, out var id))
                sb.Append(count).Append("\tR\t").Append(id).Append('\n');
            else
                sb.Append(count).Append("\tL\t").Append(EscapeField(content)).Append('\n');
        }

        var text = sb.ToString();
        return VerifyLines(text, lines) ? text : null;
    }

    private static bool VerifyLines(string text, string[] originalLines)
    {
        var dict = new Dictionary<int, string>();
        var runs = new List<(int Count, string Kind, string Payload)>();
        var section = "";

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line is "--dict--" or "--runs--") { section = line; continue; }

            if (section == "--dict--")
            {
                var idx = line.IndexOf('\t');
                if (idx < 0 || !int.TryParse(line[..idx], out var id)) return false;
                dict[id] = UnescapeField(line[(idx + 1)..]);
            }
            else if (section == "--runs--")
            {
                var parts = line.Split('\t', 3);
                if (parts.Length < 2 || !int.TryParse(parts[0], out var count)) return false;
                runs.Add((count, parts[1], parts.Length > 2 ? parts[2] : ""));
            }
        }

        var reconstructed = new List<string>();
        foreach (var (count, kind, payload) in runs)
        {
            var content = kind == "R"
                ? (dict.TryGetValue(int.Parse(payload), out var v) ? v : "")
                : UnescapeField(payload);
            for (var k = 0; k < count; k++) reconstructed.Add(content);
        }

        return reconstructed.SequenceEqual(originalLines);
    }

    private static List<(int Count, string Content)> RunLengthEncode(List<string> lines)
    {
        var runs = new List<(int, string)>();
        var i = 0;
        while (i < lines.Count)
        {
            var j = i + 1;
            while (j < lines.Count && lines[j] == lines[i]) j++;
            runs.Add((j - i, lines[i]));
            i = j;
        }
        return runs;
    }

    // ---------------------------------------------------------------------------------------
    // Shared field escaping: every format below frames fields with tab characters, so any literal
    // tab in real content must be neutralized to keep field counts unambiguous on decode.
    // ---------------------------------------------------------------------------------------

    private static string EscapeField(string s) => s.Replace("\\", "\\\\").Replace("\t", "\\t");

    private static string UnescapeField(string s) => s.Replace("\\t", "\t").Replace("\\\\", "\\");
}
