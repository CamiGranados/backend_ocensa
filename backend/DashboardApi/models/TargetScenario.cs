using System.ComponentModel.DataAnnotations;

namespace DashboardApi.Models
{
    // Filas fijas: "Contractual", "Línea base", "Actual".
    // Cada una agrupa los periodos de meta (TankTargetPeriod) de ese escenario.
    public class TargetScenario
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; } = null!;

        public ICollection<TankTargetPeriod> TankTargetPeriods { get; set; } = new List<TankTargetPeriod>();
    }
}
