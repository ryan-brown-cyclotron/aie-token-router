using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UsageTracker;
using UsageTracker.Functions;

namespace UsageTracker.Tests;

public class RemoteCompressionForwarderTests
{
    [Fact]
    public async Task Uses_local_fallback_when_no_endpoint_is_configured()
    {
        var forwarder = Build(handler: null, endpoint: null, fallback: StubCompressor.Returning("LOCAL"));

        var result = await forwarder.CompressAsync("original tool output", model: null);

        Assert.True(result.Compressed);
        Assert.Equal("LOCAL", result.Output);
    }

    [Fact]
    public async Task Falls_back_to_local_when_the_headroom_call_throws()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var forwarder = Build(handler, endpoint: "https://headroom.example", fallback: StubCompressor.Returning("LOCAL"));

        var result = await forwarder.CompressAsync("original tool output", model: null);

        Assert.True(result.Compressed);
        Assert.Equal("LOCAL", result.Output);
    }

    [Fact]
    public async Task Maps_headroom_response_to_compressed_result()
    {
        var handler = new StubHandler(_ => Json(
            "{\"messages\":[{\"role\":\"user\",\"content\":\"COMPRESSED\"}],\"tokens_saved\":42,\"compression_ratio\":0.3}"));
        var forwarder = Build(handler, endpoint: "https://headroom.example", fallback: StubCompressor.Returning("LOCAL"));

        var result = await forwarder.CompressAsync("original tool output", model: "gpt-4o");

        Assert.True(result.Compressed);
        Assert.Equal("COMPRESSED", result.Output);
        Assert.Equal(42, result.TokensSaved);
    }

    [Fact]
    public async Task Leaves_output_unchanged_when_no_endpoint_and_no_fallback()
    {
        var forwarder = Build(handler: null, endpoint: null, fallback: null);

        var result = await forwarder.CompressAsync("original tool output", model: null);

        Assert.False(result.Compressed);
        Assert.Equal("original tool output", result.Output);
    }

    private static RemoteCompressionForwarder Build(StubHandler? handler, string? endpoint, IToolOutputCompressor? fallback)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CompressionEndpoint"] = endpoint })
            .Build();

        var factory = new StubHttpClientFactory(handler ?? new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        return new RemoteCompressionForwarder(
            factory,
            config,
            new UsageTrackerMetrics(),
            NullLogger<RemoteCompressionForwarder>.Instance,
            fallback);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubCompressor : IToolOutputCompressor
    {
        private readonly string _output;
        private StubCompressor(string output) => _output = output;

        public static StubCompressor Returning(string output) => new(output);

        public Task<ToolOutputCompression> CompressAsync(string toolOutput, string? model, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolOutputCompression(true, _output, toolOutput.Length, _output.Length));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
