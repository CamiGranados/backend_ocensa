namespace DashboardApi.Models
{
    public class Tank
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;   // "TK7311"
        public ICollection<TankMonthlyTarget> TankMonthlyTargets { get; set; } = new List<TankMonthlyTarget>();
    }
}