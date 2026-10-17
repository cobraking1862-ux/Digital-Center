using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DigitalCenter.Core.Data;

/// <summary>
/// Used by the "dotnet ef" tooling at design time (migrations). Not used by
/// the running application, which supplies its own connection string.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DigitalCenterDbContext>
{
    public DigitalCenterDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DigitalCenterDbContext>()
            .UseSqlite("Data Source=design-time-placeholder.db")
            .Options;

        return new DigitalCenterDbContext(options);
    }
}
