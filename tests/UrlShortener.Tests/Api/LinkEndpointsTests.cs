using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace UrlShortener.Tests.Api;

public sealed class LinkEndpointsTests : IAsyncLifetime
{
    private readonly ShortenerApiFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _factory.InitializeDatabaseAsync();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Create_with_valid_url_returns_201_with_short_url()
    {
        var response = await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com/page" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateLinkResponseDto>();
        Assert.NotNull(body);
        Assert.Equal(7, body.Code.Length);
        Assert.Equal($"{ShortenerApiFactory.BaseUrl}/{body.Code}", body.ShortUrl);
        Assert.Equal("https://example.com/page", body.LongUrl);
        Assert.Equal(new Uri(body.ShortUrl), response.Headers.Location);
    }

    [Fact]
    public async Task Create_with_alias_uses_alias_as_code()
    {
        var response = await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com", alias = "my-link" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("my-link", (await response.Content.ReadFromJsonAsync<CreateLinkResponseDto>())!.Code);
    }

    [Theory]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("https://example.com", "x")]
    public async Task Create_with_invalid_input_returns_400_problem_details(string url, string? alias)
    {
        var response = await _client.PostAsJsonAsync("/api/links", new { url, alias });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.False(string.IsNullOrWhiteSpace(problem?.Detail));
    }

    [Fact]
    public async Task Create_with_taken_alias_returns_409_problem_details()
    {
        await _client.PostAsJsonAsync("/api/links", new { url = "https://example.com", alias = "taken" });

        var response = await _client.PostAsJsonAsync("/api/links", new { url = "https://other.example", alias = "taken" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Redirect_known_code_returns_302_to_long_url_without_caching()
    {
        var code = await CreateLinkAsync("https://example.com/target");

        var response = await _client.GetAsync($"/{code}");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(new Uri("https://example.com/target"), response.Headers.Location);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Redirect_unknown_code_returns_404_problem_details()
    {
        var response = await _client.GetAsync("/nope123");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Two_redirects_result_in_click_count_of_two()
    {
        var code = await CreateLinkAsync("https://example.com");

        await _client.GetAsync($"/{code}");
        await _client.GetAsync($"/{code}");

        Assert.Equal(2, await ClickCountAsync(code));
    }

    [Fact]
    public async Task Concurrent_redirects_are_all_counted()
    {
        var code = await CreateLinkAsync("https://example.com");

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _client.GetAsync($"/{code}")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Found, r.StatusCode));
        Assert.Equal(20, await ClickCountAsync(code));
    }

    private async Task<string> CreateLinkAsync(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/links", new { url });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreateLinkResponseDto>())!.Code;
    }

    private async Task<long> ClickCountAsync(string code)
    {
        await using var context = await _factory.CreateDbContextAsync();
        return await context.Links.Where(l => l.Code == code).Select(l => l.ClickCount).SingleAsync();
    }

    private sealed record CreateLinkResponseDto(string Code, string ShortUrl, string LongUrl);
}
