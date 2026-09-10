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
        public DbSet<TargetScenario> TargetScenarios { get; set; }
        public DbSet<TankMonthlyTarget> TankMonthlyTargets { get; set; }
        public DbSet<Limits_Value> Limits_Values { get; set; }

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

            // Escenarios de meta: "Contractual", "Línea base", "Actual" (nombre único).
            modelBuilder.Entity<TargetScenario>()
                .HasIndex(s => s.Name)
                .IsUnique();

            // Una sola meta por empresa + tanque + escenario + mes.
            // Es la clave natural que usa la carga para no duplicar en re-cargas.
            modelBuilder.Entity<TankMonthlyTarget>()
                .HasIndex(t => new { t.CompanyId, t.TankId, t.ScenarioId, t.Period })
                .IsUnique();

            // Relaciones explícitas con Restrict: borrar una empresa/tanque/escenario
            // no debe arrastrar sus metas (además evita rutas de cascada múltiples en SQL Server).
            modelBuilder.Entity<TankMonthlyTarget>()
                .HasOne(t => t.Company)
                .WithMany(c => c.TankMonthlyTargets)
                .HasForeignKey(t => t.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<TankMonthlyTarget>()
                .HasOne(t => t.Tank)
                .WithMany(t => t.TankMonthlyTargets)
                .HasForeignKey(t => t.TankId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<TankMonthlyTarget>()
                .HasOne(t => t.Scenario)
                .WithMany(s => s.TankMonthlyTargets)
                .HasForeignKey(t => t.ScenarioId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}