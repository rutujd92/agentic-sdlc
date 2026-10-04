using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UrlShortener.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef` at design time, so migrations can be generated without running the API
/// or having a database. The connection string is never opened when adding migrations.
/// </summary>
public sealed class ShortenerDbContextFactory : IDesignTimeDbContextFactory<ShortenerDbContext>
{
    public ShortenerDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ShortenerDbContext>()
            .UseNpgsql("Host=localhost;Database=shortener")
            .Options);
}
