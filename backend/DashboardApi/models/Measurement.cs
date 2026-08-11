using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DashboardApi.Models
{
    public class Measurement
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
        public decimal? BSR_planct { get; set; }
        public decimal? BPA_planct { get; set; }
        public decimal? BHT_planct { get; set; }
        public decimal? BAnT_planct { get; set; }
        public decimal? Biocida_percent { get; set; }
        public decimal? THPS_percent { get; set; }
        public string Sampling_Point { get; set; } = string.Empty;
        public DateTime? Injection_date { get; set; }
        public decimal? Residual_THPS { get; set; }
        public decimal? Last_Biocida_Injection { get; set; }
        public decimal? GSV_bls { get; set; }
        public decimal? Estimated_FWV { get; set; }
        public decimal? Reported_FWV { get; set; }
        public decimal? Calculated_FWV { get; set; }
        public decimal? Increased_FWV { get; set; }
        public decimal? API { get; set; }
        public decimal? Scheduled_Dose { get; set; }
        public decimal? Actual_Injected_Dose { get; set; }
        public decimal? Programmed_volume { get; set; }
        public decimal? Actual_volume { get; set; }
        public string Standard_Sampling_Type { get; set; } = string.Empty;
        public string Category_Nace { get; set; } = string.Empty;
        public string Level_Alarm { get; set; } = string.Empty;

        [ForeignKey("Upload")]
        public int UploadId { get; set; }
        public Upload? Upload { get; set; }
    }
}