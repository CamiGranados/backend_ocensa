using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;


namespace DashboardApi.Models
{
    public class PhysicalChemistry
    {
        [Key]
        public long Id { get; set; }
        [ForeignKey("Measurement")]
        public long MeasurementId { get; set; }
        public Measurement? Measurement { get; set; }
        public decimal? Temperature_C { get; set; }
        public decimal? H2S_mgL { get; set; }
        public decimal? pH { get; set; }
        public decimal? Conductivity_uScm { get; set; }
        public decimal? Alkalinity_mgL { get; set; }
        public decimal? calcium_mgL { get; set; }
        public decimal? BSW_percent { get; set; }
        public decimal? General_Corrosion_Rate_ppm { get; set; }
        public decimal? Maximum_Sting_Speed_ppm { get; set; }
    }

}