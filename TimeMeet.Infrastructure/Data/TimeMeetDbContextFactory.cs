using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TimeMeet.Infrastructure.Data;

public sealed class TimeMeetDbContextFactory : IDesignTimeDbContextFactory<TimeMeetDbContext>
{
    public TimeMeetDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<TimeMeetDbContext>();
        builder.UseNpgsql("Host=localhost;Port=5432;Database=timemeetapp;Username=postgres;Password=postgres");
        return new TimeMeetDbContext(builder.Options);
    }
}
