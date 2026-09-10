using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;


namespace DashboardApi.Models
{
    public class Limits_Value
    {
        [Key]
        public long Id { get; set; }
        public string VariableName { get; set; } = string.Empty;
        public decimal? MinValue { get; set; }
        public decimal? MaxValue { get; set; }
        public DateTime DateRegistered { get; set; } = DateTime.UtcNow;
    }
}