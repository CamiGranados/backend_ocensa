using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardApi.Migrations
{
    /// <inheritdoc />
    public partial class EliminarLevelAlarmYCalcularCategoryNace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category_Nace",
                table: "TankDailyOperations");

            migrationBuilder.DropColumn(
                name: "Level_Alarm",
                table: "TankDailyOperations");

            migrationBuilder.AddColumn<string>(
                name: "Category_Nace",
                table: "PhysicalChemistries",
                type: "nvarchar(max)",
                nullable: true,
                computedColumnSql: "CASE WHEN [General_Corrosion_Rate_ppm] IS NULL THEN NULL WHEN [General_Corrosion_Rate_ppm] < 0.025 THEN 'BAJA' WHEN [General_Corrosion_Rate_ppm] <= 0.12 THEN 'MODERADA' WHEN [General_Corrosion_Rate_ppm] <= 0.25 THEN 'ALTA' ELSE 'SEVERA' END",
                stored: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category_Nace",
                table: "PhysicalChemistries");

            migrationBuilder.AddColumn<string>(
                name: "Category_Nace",
                table: "TankDailyOperations",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Level_Alarm",
                table: "TankDailyOperations",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");
        }
    }
}
