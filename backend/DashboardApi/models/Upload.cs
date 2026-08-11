namespace DashboardApi.Models
{
    public class Upload
    {
        public int Id { get; set; }
        public Guid LoteId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
        public string? DateRanges { get; set; }
    }
}