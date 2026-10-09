using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardApi.Migrations
{
    /// <inheritdoc />
    public partial class AgregarEstacionYVelCorrosionElectroquimica : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category_Nace",
                table: "PhysicalChemistries");

            migrationBuilder.AddColumn<string>(
                name: "Station",
                table: "TankDailyOperations",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "electrochemical_corrosion_rate",
                table: "PhysicalChemistries",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Station",
                table: "TankDailyOperations");

            migrationBuilder.DropColumn(
                name: "electrochemical_corrosion_rate",
                table: "PhysicalChemistries");

            migrationBuilder.AddColumn<string>(
                name: "Category_Nace",
                table: "PhysicalChemistries",
                type: "nvarchar(max)",
                nullable: true,
                computedColumnSql: "CASE WHEN [General_Corrosion_Rate_ppm] IS NULL THEN NULL WHEN [General_Corrosion_Rate_ppm] < 0.025 THEN 'BAJA' WHEN [General_Corrosion_Rate_ppm] <= 0.12 THEN 'MODERADA' WHEN [General_Corrosion_Rate_ppm] <= 0.25 THEN 'ALTA' ELSE 'SEVERA' END",
                stored: true);
        }
    }
}
