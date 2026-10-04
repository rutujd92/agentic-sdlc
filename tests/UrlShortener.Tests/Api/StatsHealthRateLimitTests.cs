using System.Net;
using System.Net.Http.Json;

namespace UrlShortener.Tests.Api;

public sealed class StatsHealthRateLimitTests : IAsyncLifetime
{
    private const int PermitLimit = 3;
    private readonly ShortenerApiFactory _factory = new(PermitLimit);
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _factory.InitializeDatabaseAsync();
        _client = _factory.CreateClient(new() { AllowAutoRedirect = false });
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Stats_returns_link_details_and_click_count()
    {
        await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com", alias = "stats-me" });
        await _client.GetAsync("/stats-me");
        await _client.GetAsync("/stats-me");

        var response = await _client.GetAsync("/api/links/stats-me/stats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stats = await response.Content.ReadFromJsonAsync<LinkStatsDto>();
        Assert.NotNull(stats);
        Assert.Equal("stats-me", stats.Code);
        Assert.Equal("https://example.com", stats.LongUrl);
        Assert.Equal(2, stats.ClickCount);
        Assert.True(stats.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task Stats_for_unknown_code_returns_404_problem_details()
    {
        var response = await _client.GetAsync("/api/links/unknown/stats");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Stats_does_not_count_as_a_click()
    {
        await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com", alias = "no-click" });

        await _client.GetAsync("/api/links/no-click/stats");
        var stats = await _client.GetFromJsonAsync<LinkStatsDto>("/api/links/no-click/stats");

        Assert.Equal(0, stats!.ClickCount);
    }

    [Fact]
    public async Task Health_returns_200_when_database_is_reachable()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Creating_more_links_than_the_limit_returns_429_problem_details()
    {
        for (var i = 0; i < PermitLimit; i++)
        {
            var ok = await _client.PostAsJsonAsync("/api/links", new { url = $"https://example.com/{i}" });
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        var limited = await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com/over" });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task Rate_limit_applies_only_to_link_creation()
    {
        await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com", alias = "hot-link" });

        for (var i = 0; i < PermitLimit * 3; i++)
        {
            Assert.Equal(HttpStatusCode.Found, (await _client.GetAsync("/hot-link")).StatusCode);
        }
    }

    private sealed record LinkStatsDto(string Code, string LongUrl, DateTimeOffset CreatedAt, long ClickCount);
}
