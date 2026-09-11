using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DashboardApi.Models
{
    // Valores microbiológicos de un punto de muestreo dentro de una operación diaria.
    // Una fila por (OperationId, Sampling_Point).
    public class Measurement
    {
        [Key]
        public long Id { get; set; }

        [ForeignKey("Operation")]
        public long OperationId { get; set; }
        public TankDailyOperation? Operation { get; set; }

        [Required]
        [MaxLength(10)]
        public string Sampling_Point { get; set; } = string.Empty;

        public decimal? BSR_planct { get; set; }
        public decimal? BPA_planct { get; set; }
        public decimal? BHT_planct { get; set; }
        public decimal? BAnT_planct { get; set; }
        public decimal? Biocida_percent { get; set; }
        public decimal? THPS_percent { get; set; }
        public decimal? Residual_THPS { get; set; }

        // Tipo de muestreo estandarizado en la carga: Prebache / Postbache / Seguimiento / "No disponible por OPS".
        [Required]
        [MaxLength(30)]
        public string Standard_Sampling_Type { get; set; } = string.Empty;
    }
}
