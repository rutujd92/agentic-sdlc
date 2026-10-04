using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Anthropic;
using Orchestrator.Core.Agents;
using Orchestrator.Infrastructure.Agents;

namespace Orchestrator.Tests.Agents;

/// <summary>
/// Verifies the live request on the wire without network access or API credit: the SDK is pointed at a local
/// listener that captures the request and returns a canned Messages API response.
/// </summary>
public sealed class ClaudeChatProviderWireTests : IDisposable
{
    private const string CannedResponse = """
        {"id":"msg_test","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[{"type":"text","text":"{\"problem\":\"p\"}"}],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":120,"output_tokens":30,"cache_read_input_tokens":2000,"cache_creation_input_tokens":0}}
        """;

    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;

    public ClaudeChatProviderWireTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
    }

    [Fact]
    public async Task Sends_model_schema_effort_cached_context_and_fallbacks_and_parses_the_reply()
    {
        var captured = CaptureOneRequestAsync();
        var provider = new ClaudeChatProvider(new AnthropicClient { BaseUrl = _baseUrl, ApiKey = "test-key", MaxRetries = 0 });
        var schema = """{"type":"object","additionalProperties":false,"required":["problem"],"properties":{"problem":{"type":"string"}}}""";

        var response = await provider.CompleteAsync(
            new ChatRequest("run", "requirements", "SYSTEM", "REPO CONTEXT", "USER PROMPT", schema, AgentEffort.Medium) { Feedback = "GUIDANCE" },
            CancellationToken.None);

        var (path, headers, body) = await captured;
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        Assert.Equal("/v1/messages", path.Split('?')[0]);
        Assert.Contains("server-side-fallback-2026-07-01", headers["anthropic-beta"], StringComparison.Ordinal);
        Assert.Equal("test-key", headers["x-api-key"]);
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal("medium", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("json_schema", root.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("problem", root.GetProperty("output_config").GetProperty("format").GetProperty("schema").GetProperty("required")[0].GetString());
        var system = root.GetProperty("system");
        Assert.Equal("SYSTEM", system[0].GetProperty("text").GetString());
        Assert.Contains("REPO CONTEXT", system[1].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("ephemeral", system[1].GetProperty("cache_control").GetProperty("type").GetString());
        var user = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("USER PROMPT", user, StringComparison.Ordinal);
        Assert.Contains("GUIDANCE", user, StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("thinking", out _));

        Assert.Equal("{\"problem\":\"p\"}", response.Json);
        Assert.Equal(2120, response.InputTokens);
        Assert.Equal(30, response.OutputTokens);
    }

    public void Dispose() => _listener.Close();

    private async Task<(string Path, Dictionary<string, string> Headers, string Body)> CaptureOneRequestAsync()
    {
        var context = await _listener.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var headers = context.Request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!.ToLowerInvariant(), k => context.Request.Headers[k]!);

        var bytes = Encoding.UTF8.GetBytes(CannedResponse);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
        return (context.Request.RawUrl ?? string.Empty, headers, body);
    }
}
