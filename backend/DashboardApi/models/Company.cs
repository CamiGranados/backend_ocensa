namespace DashboardApi.Models
{
    public class Company
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public ICollection<TankMonthlyTarget> TankMonthlyTargets { get; set; } = new List<TankMonthlyTarget>();
    }
}