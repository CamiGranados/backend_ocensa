namespace DashboardApi.Models
{
    public class Tank
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;   // "TK7311"

        public ICollection<TankTargetPeriod> TankTargetPeriods { get; set; } = new List<TankTargetPeriod>();
    }
}
