using UrlShortener.Core.Links;
using UrlShortener.Tests.Fakes;

namespace UrlShortener.Tests.Core;

public class LinkServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 9, 30, 0, TimeSpan.Zero);
    private readonly InMemoryLinkRepository _repository = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    private LinkService CreateService(params string[] generatedCodes) =>
        new(_repository, new SequenceShortCodeGenerator(generatedCodes), new LinkValidator("sho.rt"), new FixedTimeProvider(Now));

    [Fact]
    public async Task CreateAsync_with_valid_url_returns_Created_with_generated_code()
    {
        var result = await CreateService("aaaaaaa").CreateAsync("https://example.com", alias: null, _ct);

        var created = Assert.IsType<CreateLinkResult.Created>(result);
        Assert.Equal("aaaaaaa", created.Link.Code);
        Assert.Equal("https://example.com", created.Link.LongUrl);
        Assert.Equal(Now, created.Link.CreatedAt);
        Assert.Single(_repository.Links);
    }

    [Fact]
    public async Task CreateAsync_trims_surrounding_whitespace_from_url()
    {
        var result = await CreateService("aaaaaaa").CreateAsync("  https://example.com  ", alias: null, _ct);

        Assert.Equal("https://example.com", Assert.IsType<CreateLinkResult.Created>(result).Link.LongUrl);
    }

    [Fact]
    public async Task CreateAsync_with_invalid_url_returns_InvalidUrl_and_saves_nothing()
    {
        var result = await CreateService("aaaaaaa").CreateAsync("javascript:alert(1)", alias: null, _ct);

        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<CreateLinkResult.InvalidUrl>(result).Message));
        Assert.Equal(0, _repository.AddAttempts);
    }

    [Fact]
    public async Task CreateAsync_with_invalid_alias_returns_InvalidAlias_and_saves_nothing()
    {
        var result = await CreateService().CreateAsync("https://example.com", alias: "a!", _ct);

        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<CreateLinkResult.InvalidAlias>(result).Message));
        Assert.Equal(0, _repository.AddAttempts);
    }

    [Fact]
    public async Task CreateAsync_with_free_alias_uses_alias_as_code()
    {
        var result = await CreateService().CreateAsync("https://example.com", alias: "my-link", _ct);

        Assert.Equal("my-link", Assert.IsType<CreateLinkResult.Created>(result).Link.Code);
    }

    [Fact]
    public async Task CreateAsync_with_taken_alias_returns_AliasTaken_without_retrying()
    {
        _repository.Seed("my-link");

        var result = await CreateService().CreateAsync("https://example.com", alias: "my-link", _ct);

        Assert.IsType<CreateLinkResult.AliasTaken>(result);
        Assert.Equal(1, _repository.AddAttempts);
    }

    [Fact]
    public async Task CreateAsync_retries_when_generated_code_collides()
    {
        _repository.Seed("taken01");

        var result = await CreateService("taken01", "fresh01").CreateAsync("https://example.com", alias: null, _ct);

        Assert.Equal("fresh01", Assert.IsType<CreateLinkResult.Created>(result).Link.Code);
        Assert.Equal(2, _repository.AddAttempts);
    }

    [Fact]
    public async Task CreateAsync_gives_up_after_max_attempts_of_collisions()
    {
        var codes = Enumerable.Range(1, LinkService.MaxCodeGenerationAttempts).Select(i => $"taken{i:00}").ToArray();
        foreach (var code in codes)
        {
            _repository.Seed(code);
        }

        var result = await CreateService(codes).CreateAsync("https://example.com", alias: null, _ct);

        Assert.IsType<CreateLinkResult.CodeSpaceExhausted>(result);
        Assert.Equal(LinkService.MaxCodeGenerationAttempts, _repository.AddAttempts);
    }
}
