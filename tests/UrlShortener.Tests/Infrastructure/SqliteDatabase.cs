using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.Tests.Infrastructure;

/// <summary>
/// In-memory SQLite database that lives as long as this object. The connection must stay open,
/// otherwise SQLite discards the in-memory database. Uses EnsureCreated because the Npgsql
/// migrations cannot run on SQLite.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ShortenerDbContext> _options;

    public SqliteDatabase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ShortenerDbContext>().UseSqlite(_connection).Options;

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public ShortenerDbContext CreateContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
