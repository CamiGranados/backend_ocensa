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
        public DbSet<PhysicalChemistry> PhysicalChemistries { get; set; }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            // Precision for every decimal property, instead of repeating HasPrecision per field
            configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Combined index — this is what makes each view's query fast
            modelBuilder.Entity<Measurement>()
                .HasIndex(m => new { m.CompanyId, m.TankId, m.Date });
        }
    }
}