using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DashboardApi.Models
{
    // Meta de un tanque para un escenario (Contractual / Línea base / Actual) que
    // rige durante un TRAMO de tiempo, no un mes suelto.
    //
    // El tramo va desde ValidFrom (inclusive) hasta ValidTo (exclusivo). ValidTo == null
    // es el periodo abierto / vigente. Se cierra un periodo y se abre otro cuando el
    // Excel trae un juego de valores distinto para ese tanque + escenario.
    //
    // Para asociar una medición a su meta:
    //   ValidFrom <= m.Date && (ValidTo == null || m.Date < ValidTo)
    //
    // Los periodos se arman en la carga con las filas de ese Excel (no se recalcula
    // el histórico completo). Al re-cargar se omiten los tramos cuya clave ya existe.
    public class TankTargetPeriod
    {
        [Key]
        public long Id { get; set; }

        [ForeignKey("Company")]
        public long CompanyId { get; set; }
        [ForeignKey("Tank")]
        public long TankId { get; set; }
        [ForeignKey("Scenario")]
        public int ScenarioId { get; set; }

        // Inicio del tramo (inclusive).
        public DateOnly ValidFrom { get; set; }

        // Fin del tramo (exclusivo). null = periodo abierto (vigente).
        public DateOnly? ValidTo { get; set; }

        // "Agua estimada" del escenario.
        // Contractual llega como rango ("1.000 – 2.500") → Min/Max. Los demás: Min == Max.
        public decimal? EstimatedWaterMin_bbl { get; set; }
        public decimal? EstimatedWaterMax_bbl { get; set; }

        // "periodicidad" (baches/mes). El escenario Actual no la trae en el Excel.
        public int? Periodicity_BatchesPerMonth { get; set; }

        public decimal? Dose_ppm { get; set; }
        public decimal? EstimatedGallons_Month { get; set; }

        public Company Company { get; set; } = null!;
        public Tank Tank { get; set; } = null!;
        public TargetScenario Scenario { get; set; } = null!;
    }
}
