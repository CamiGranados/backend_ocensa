namespace DashboardApi.Models
{
    public class Measurement
    {
        public long Id { get; set; }
        public int CompanyId { get; set; }
        public string TankId { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public string Variable { get; set; } = string.Empty;
        public decimal? NumericValue { get; set; }   // for numeric values
        public string? TextValue { get; set; }        // for text/categories
        public int UploadId { get; set; }
    }
}