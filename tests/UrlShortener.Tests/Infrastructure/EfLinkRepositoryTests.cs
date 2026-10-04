using UrlShortener.Core.Links;
using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.Tests.Infrastructure;

public sealed class EfLinkRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteDatabase _database = new();
    private readonly CancellationToken _ct = CancellationToken.None;

    [Fact]
    public async Task AddAsync_then_GetByCodeAsync_returns_saved_link()
    {
        await using (var context = _database.CreateContext())
        {
            var result = await new EfLinkRepository(context).AddAsync(NewLink("abc1234"), _ct);
            Assert.Equal(AddLinkResult.Added, result);
        }

        await using var readContext = _database.CreateContext();
        var link = await new EfLinkRepository(readContext).GetByCodeAsync("abc1234", _ct);

        Assert.NotNull(link);
        Assert.True(link.Id > 0);
        Assert.Equal("https://example.com/page", link.LongUrl);
        Assert.Equal(CreatedAt, link.CreatedAt);
        Assert.Equal(0, link.ClickCount);
    }

    [Fact]
    public async Task GetByCodeAsync_returns_null_for_unknown_code()
    {
        await using var context = _database.CreateContext();

        Assert.Null(await new EfLinkRepository(context).GetByCodeAsync("missing", _ct));
    }

    [Fact]
    public async Task GetByCodeAsync_is_case_sensitive()
    {
        await using var context = _database.CreateContext();
        var repository = new EfLinkRepository(context);
        await repository.AddAsync(NewLink("AbCdEfG"), _ct);

        Assert.Null(await repository.GetByCodeAsync("abcdefg", _ct));
    }

    [Fact]
    public async Task AddAsync_returns_DuplicateCode_when_code_exists()
    {
        await using var context = _database.CreateContext();
        var repository = new EfLinkRepository(context);
        await repository.AddAsync(NewLink("dup1234"), _ct);

        var result = await repository.AddAsync(NewLink("dup1234"), _ct);

        Assert.Equal(AddLinkResult.DuplicateCode, result);
    }

    [Fact]
    public async Task AddAsync_keeps_repository_usable_after_duplicate()
    {
        await using var context = _database.CreateContext();
        var repository = new EfLinkRepository(context);
        await repository.AddAsync(NewLink("dup1234"), _ct);
        await repository.AddAsync(NewLink("dup1234"), _ct);

        var result = await repository.AddAsync(NewLink("new1234"), _ct);

        Assert.Equal(AddLinkResult.Added, result);
    }

    [Fact]
    public async Task IncrementClicksAsync_twice_results_in_two_clicks()
    {
        await using (var context = _database.CreateContext())
        {
            var repository = new EfLinkRepository(context);
            await repository.AddAsync(NewLink("clk1234"), _ct);
            Assert.True(await repository.IncrementClicksAsync("clk1234", _ct));
            Assert.True(await repository.IncrementClicksAsync("clk1234", _ct));
        }

        await using var readContext = _database.CreateContext();
        var link = await new EfLinkRepository(readContext).GetByCodeAsync("clk1234", _ct);

        Assert.Equal(2, link!.ClickCount);
    }

    [Fact]
    public async Task IncrementClicksAsync_returns_false_for_unknown_code()
    {
        await using var context = _database.CreateContext();

        Assert.False(await new EfLinkRepository(context).IncrementClicksAsync("missing", _ct));
    }

    public void Dispose() => _database.Dispose();

    private static Link NewLink(string code) =>
        new() { Code = code, LongUrl = "https://example.com/page", CreatedAt = CreatedAt };
}
