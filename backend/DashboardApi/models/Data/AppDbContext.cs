using Microsoft.EntityFrameworkCore;
using DashboardApi.Models;

namespace DashboardApi.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Measurement> Measurements { get; set; }
        public DbSet<Company> Companies { get; set; }
        public DbSet<Tank> Tanks { get; set; }
        public DbSet<Upload> Uploads { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Combined index — this is what makes each view's query fast
            modelBuilder.Entity<Measurement>()
                .HasIndex(m => new { m.CompanyId, m.TankId, m.Variable, m.Date });

            // Precision for decimal values
            modelBuilder.Entity<Measurement>()
                .Property(m => m.NumericValue)
                .HasPrecision(18, 4);
        }
    }
}