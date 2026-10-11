using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DataspaceDrive.Infrastructure.Persistence;

/// <summary>Nur für <c>dotnet ef migrations</c>; zur Laufzeit kommt die Verbindung aus der Konfiguration.</summary>
public sealed class DriveDbContextFactory : IDesignTimeDbContextFactory<DriveDbContext>
{
    public DriveDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DriveDbContext>()
            .UseNpgsql("Host=localhost;Database=drive;Username=drive;Password=design-time-only")
            .Options);
}
