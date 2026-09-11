using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DashboardApi.Models
{
    // Datos operativos de un tanque en una fecha: una sola fila por (empresa, tanque, fecha).
    // Los valores microbiológicos por punto de muestreo cuelgan de aquí vía Measurement.OperationId.
    public class TankDailyOperation
    {
        [Key]
        public long Id { get; set; }

        [ForeignKey("Company")]
        public long CompanyId { get; set; }
        public Company? Company { get; set; }

        [ForeignKey("Tank")]
        public long TankId { get; set; }
        public Tank? Tank { get; set; }

        public DateTime Date { get; set; }

        public DateTime? Injection_date { get; set; }
        public decimal? Last_Biocida_Injection { get; set; }
        public decimal? Scheduled_Dose { get; set; }
        public decimal? Actual_Injected_Dose { get; set; }
        public decimal? Programmed_volume { get; set; }
        public decimal? Real_Volume { get; set; }
        public decimal? GSV_bls { get; set; }
        public decimal? API { get; set; }
        public decimal? Estimated_FWV { get; set; }
        public decimal? Reported_FWV { get; set; }
        public decimal? Calculated_FWV { get; set; }
        public decimal? Increased_FWV { get; set; }

        public ICollection<Measurement> Measurements { get; set; } = new List<Measurement>();
    }
}
