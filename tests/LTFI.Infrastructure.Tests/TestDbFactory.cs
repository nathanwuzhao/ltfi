using Microsoft.EntityFrameworkCore;
using LTFI.Infrastructure.Persistence;

namespace LTFI.Infrastructure.Tests;

/// <summary>
/// A minimal <see cref="IDbContextFactory{TContext}"/> over a SQLite file, so the real
/// services can be exercised against a real database. Two instances pointed at the same
/// path simulate an app restart.
/// </summary>
internal sealed class TestDbFactory(string path) : IDbContextFactory<LtfiDbContext>
{
    private readonly DbContextOptions<LtfiDbContext> _options =
        new DbContextOptionsBuilder<LtfiDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

    public LtfiDbContext CreateDbContext() => new(_options);
}
