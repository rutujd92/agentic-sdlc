using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.Tests.Api;

/// <summary>
/// Hosts the real API with PostgreSQL swapped for a temporary SQLite file. A file (not in-memory) is used
/// so concurrent requests get their own connections, which the parallel-redirect test relies on.
/// </summary>
public sealed class ShortenerApiFactory : WebApplicationFactory<Program>
{
    public const string BaseUrl = "https://sho.rt";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"shortener-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Shortener", "Host=unused");
        builder.UseSetting("Shortener:BaseUrl", BaseUrl);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ShortenerDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ShortenerDbContext>>();
            services.AddDbContext<ShortenerDbContext>(options =>
                options.UseSqlite($"Data Source={_databasePath};Default Timeout=30;Pooling=False"));
        });
    }

    public async Task<ShortenerDbContext> CreateDbContextAsync()
    {
        var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ShortenerDbContext>();
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    public async Task InitializeDatabaseAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ShortenerDbContext>().Database.EnsureCreatedAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        File.Delete(_databasePath);
    }
}
