using System.ComponentModel.DataAnnotations;

namespace DashboardApi.Models
{
    // Meta mensual de un tanque para un escenario (Contractual / Línea base / Actual).
    // Se deriva del Excel de carga: una fila por empresa + tanque + escenario + mes.
    public class TankMonthlyTarget
    {
        [Key]
        public long Id { get; set; }

        public long CompanyId { get; set; }
        public long TankId { get; set; }
        public int ScenarioId { get; set; }

        // Primer día del mes al que aplica la meta.
        public DateOnly Period { get; set; }

        // "Agua estimada" del escenario.
        // Contractual llega como rango ("1.000 – 2.500") → se parte en Min/Max.
        // Línea base y Actual son un solo valor → Min == Max.
        public decimal? EstimatedWaterMin_bbl { get; set; }
        public decimal? EstimatedWaterMax_bbl { get; set; }

        // "periodicidad" (baches/mes). El escenario Actual no la trae en el Excel.
        public int? Periodicity_BatchesPerMonth { get; set; }

        public decimal? Dose_ppm { get; set; }
        public decimal? EstimatedGallons_Month { get; set; }

        // Sin columna de origen por ahora: queda para ajuste manual.
        public decimal? Tolerance_percent { get; set; }

        public Company Company { get; set; } = null!;
        public Tank Tank { get; set; } = null!;
        public TargetScenario Scenario { get; set; } = null!;
    }
}
