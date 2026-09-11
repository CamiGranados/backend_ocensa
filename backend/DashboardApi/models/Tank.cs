namespace DashboardApi.Models
{
    public class Tank
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;   // "TK7311"

        // Perfil físico del tanque. Es un dato fijo del activo, no varía por periodo/escenario.
        public decimal? NominalCapacity_bbl { get; set; }
        public string? FluidType { get; set; }

        public ICollection<TankTargetPeriod> TankTargetPeriods { get; set; } = new List<TankTargetPeriod>();
    }
}
