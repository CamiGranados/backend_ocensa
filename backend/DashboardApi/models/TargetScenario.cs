using System.ComponentModel.DataAnnotations;

namespace DashboardApi.Models
{
    // Filas fijas: "Contractual", "Línea base", "Actual".
    // Cada una agrupa las metas mensuales (TankMonthlyTarget) de ese escenario.
    public class TargetScenario
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; } = null!;

        public ICollection<TankMonthlyTarget> TankMonthlyTargets { get; set; } = new List<TankMonthlyTarget>();
    }
}
