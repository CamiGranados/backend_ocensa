using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardApi.Migrations
{
    /// <inheritdoc />
    public partial class MoverCapacidadYFluidoATanks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FluidType",
                table: "TankTargetPeriods");

            migrationBuilder.DropColumn(
                name: "NominalCapacity_bbl",
                table: "TankTargetPeriods");

            migrationBuilder.AddColumn<string>(
                name: "FluidType",
                table: "Tanks",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NominalCapacity_bbl",
                table: "Tanks",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FluidType",
                table: "Tanks");

            migrationBuilder.DropColumn(
                name: "NominalCapacity_bbl",
                table: "Tanks");

            migrationBuilder.AddColumn<string>(
                name: "FluidType",
                table: "TankTargetPeriods",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NominalCapacity_bbl",
                table: "TankTargetPeriods",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);
        }
    }
}
