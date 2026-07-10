namespace UsageTracker.Tests;

public class DeterministicToolOutputCompressorTests
{
    private static readonly DeterministicToolOutputCompressor Compressor = new();

    [Fact]
    public async Task Leaves_short_output_unchanged()
    {
        var result = await Compressor.CompressAsync("too short", null);

        Assert.False(result.Compressed);
        Assert.Equal("too short", result.Output);
    }

    [Fact]
    public async Task Leaves_non_repetitive_text_unchanged()
    {
        var text = string.Join(' ', Enumerable.Range(0, 40).Select(i => $"unique-token-{i}"));

        var result = await Compressor.CompressAsync(text, null);

        Assert.False(result.Compressed);
        Assert.Equal(text, result.Output);
    }

    [Fact]
    public async Task Compresses_a_homogeneous_json_array_via_table_factoring()
    {
        var items = Enumerable.Range(0, 20)
            .Select(i => $"{{\"service\":\"api\",\"env\":\"prod\",\"status\":200,\"path\":\"/health\",\"ms\":{i}}}");
        var json = "[" + string.Join(',', items) + "]";

        var result = await Compressor.CompressAsync(json, null);

        Assert.True(result.Compressed);
        Assert.True(result.Output.Length < json.Length);
        Assert.Contains("@ut/json-table/v1", result.Output);
        Assert.True(result.TokensSaved > 0);
    }

    [Fact]
    public async Task Compresses_jsonl_via_table_factoring()
    {
        var lines = Enumerable.Range(0, 20)
            .Select(i => $"{{\"level\":\"info\",\"service\":\"worker\",\"count\":{i}}}");
        var jsonl = string.Join('\n', lines);

        var result = await Compressor.CompressAsync(jsonl, null);

        Assert.True(result.Compressed);
        Assert.Contains("@ut/jsonl-table/v1", result.Output);
    }

    [Fact]
    public async Task Compresses_repeated_log_lines_via_template_grouping()
    {
        var lines = Enumerable.Range(0, 30)
            .Select(i => $"2026-07-09T10:00:{i:00}Z INFO worker A processed {i} items");
        var log = string.Join('\n', lines);

        var result = await Compressor.CompressAsync(log, null);

        Assert.True(result.Compressed);
        Assert.Contains("@ut/log/v1", result.Output);
        Assert.True(result.Output.Length < log.Length);
    }

    [Fact]
    public async Task Compresses_repeated_lines_via_run_length_encoding()
    {
        var repeated = string.Concat(Enumerable.Repeat("Restore completed successfully for package.\n", 40));

        var result = await Compressor.CompressAsync(repeated, null);

        Assert.True(result.Compressed);
        Assert.True(result.Output.Length < repeated.Length);
    }

    [Fact]
    public async Task Compresses_a_delimited_table_via_column_dictionary()
    {
        // Repeated leading columns give the column dictionary something to factor; a unique,
        // digit-free suffix per row keeps every row distinct (defeating whole-line RLE/dictionary)
        // and gives the log-template strategy nothing to mask - so table factoring is the only
        // applicable strategy and this exercises it specifically.
        var rows = Enumerable.Range(0, 30)
            .Select(i => $"api,prod-region,healthy,health-check-endpoint,worker-{(char)('a' + i / 26)}{(char)('a' + i % 26)}");
        var csv = string.Join('\n', rows);

        var result = await Compressor.CompressAsync(csv, null);

        Assert.True(result.Compressed);
        Assert.Contains("@ut/table/v1", result.Output);
    }
}
