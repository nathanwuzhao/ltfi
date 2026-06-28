using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LTFI.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> to build the model for migrations.
/// The connection string here is only used at design time; the running app supplies its own.
/// </summary>
public class LtfiDbContextFactory : IDesignTimeDbContextFactory<LtfiDbContext>
{
    public LtfiDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LtfiDbContext>()
            .UseSqlite("Data Source=ltfi-design.db")
            .Options;

        return new LtfiDbContext(options);
    }
}
