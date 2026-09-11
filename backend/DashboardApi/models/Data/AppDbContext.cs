using Microsoft.EntityFrameworkCore;
using DashboardApi.Models;

namespace DashboardApi.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Measurement> Measurements { get; set; }
        public DbSet<TankDailyOperation> TankDailyOperations { get; set; }
        public DbSet<Company> Companies { get; set; }
        public DbSet<Tank> Tanks { get; set; }
        public DbSet<Upload> Uploads { get; set; }
        public DbSet<PhysicalChemistry> PhysicalChemistries { get; set; }
        public DbSet<TargetScenario> TargetScenarios { get; set; }
        public DbSet<TankTargetPeriod> TankTargetPeriods { get; set; }
        public DbSet<Limits_Value> Limits_Values { get; set; }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            // Precision for every decimal property, instead of repeating HasPrecision per field
            configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Una operación diaria por empresa + tanque + fecha. Es la clave natural de la carga
            // y lo que hace rápida la consulta de cada vista (filtra por tanque + rango de fechas).
            modelBuilder.Entity<TankDailyOperation>()
                .HasIndex(o => new { o.CompanyId, o.TankId, o.Date })
                .IsUnique();

            // Borrar una empresa/tanque no arrastra sus operaciones (evita rutas de cascada múltiples).
            modelBuilder.Entity<TankDailyOperation>()
                .HasOne(o => o.Company)
                .WithMany()
                .HasForeignKey(o => o.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<TankDailyOperation>()
                .HasOne(o => o.Tank)
                .WithMany()
                .HasForeignKey(o => o.TankId)
                .OnDelete(DeleteBehavior.Restrict);

            // Un valor microbiológico por operación + punto de muestreo.
            modelBuilder.Entity<Measurement>()
                .HasIndex(m => new { m.OperationId, m.Sampling_Point })
                .IsUnique();

            modelBuilder.Entity<Measurement>()
                .HasOne(m => m.Operation)
                .WithMany(o => o.Measurements)
                .HasForeignKey(m => m.OperationId)
                .OnDelete(DeleteBehavior.Cascade);

            // Escenarios de meta: "Contractual", "Línea base", "Actual" (nombre único).
            modelBuilder.Entity<TargetScenario>()
                .HasIndex(s => s.Name)
                .IsUnique();

            // Un solo periodo por empresa + tanque + escenario + fecha de inicio.
            // Es la clave natural que usa la carga para no duplicar en re-cargas.
            modelBuilder.Entity<TankTargetPeriod>()
                .HasIndex(t => new { t.CompanyId, t.TankId, t.ScenarioId, t.ValidFrom })
                .IsUnique();

            modelBuilder.Entity<Tank>()
                .Property(t => t.FluidType)
                .HasMaxLength(200);

            // Categoría NACE SP0775-23: columna computada y persistida por SQL Server
            // a partir de General_Corrosion_Rate_ppm (no se calcula en C#).
            modelBuilder.Entity<PhysicalChemistry>()
                .Property(p => p.Category_Nace)
                .HasComputedColumnSql(
                    "CASE WHEN [General_Corrosion_Rate_ppm] IS NULL THEN NULL " +
                    "WHEN [General_Corrosion_Rate_ppm] < 0.025 THEN 'BAJA' " +
                    "WHEN [General_Corrosion_Rate_ppm] <= 0.12 THEN 'MODERADA' " +
                    "WHEN [General_Corrosion_Rate_ppm] <= 0.25 THEN 'ALTA' " +
                    "ELSE 'SEVERA' END",
                    stored: true);

            // Relaciones explícitas con Restrict: borrar una empresa/tanque/escenario
            // no debe arrastrar sus metas (además evita rutas de cascada múltiples en SQL Server).
            modelBuilder.Entity<TankTargetPeriod>()
                .HasOne(t => t.Company)
                .WithMany(c => c.TankTargetPeriods)
                .HasForeignKey(t => t.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<TankTargetPeriod>()
                .HasOne(t => t.Tank)
                .WithMany(t => t.TankTargetPeriods)
                .HasForeignKey(t => t.TankId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<TankTargetPeriod>()
                .HasOne(t => t.Scenario)
                .WithMany(s => s.TankTargetPeriods)
                .HasForeignKey(t => t.ScenarioId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
