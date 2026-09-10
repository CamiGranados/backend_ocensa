using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardApi.Migrations
{
    /// <inheritdoc />
    public partial class AgregarMetasMensualesTanque : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Actual_volume",
                table: "Measurements",
                newName: "Real_Volume");

            migrationBuilder.CreateTable(
                name: "TargetScenarios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetScenarios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TankMonthlyTargets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    TankId = table.Column<long>(type: "bigint", nullable: false),
                    ScenarioId = table.Column<int>(type: "int", nullable: false),
                    Period = table.Column<DateOnly>(type: "date", nullable: false),
                    EstimatedWaterMin_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    EstimatedWaterMax_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Periodicity_BatchesPerMonth = table.Column<int>(type: "int", nullable: true),
                    Dose_ppm = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    EstimatedGallons_Month = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Tolerance_percent = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TankMonthlyTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TankMonthlyTargets_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TankMonthlyTargets_Tanks_TankId",
                        column: x => x.TankId,
                        principalTable: "Tanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TankMonthlyTargets_TargetScenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalTable: "TargetScenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TankMonthlyTargets_CompanyId_TankId_ScenarioId_Period",
                table: "TankMonthlyTargets",
                columns: new[] { "CompanyId", "TankId", "ScenarioId", "Period" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TankMonthlyTargets_ScenarioId",
                table: "TankMonthlyTargets",
                column: "ScenarioId");

            migrationBuilder.CreateIndex(
                name: "IX_TankMonthlyTargets_TankId",
                table: "TankMonthlyTargets",
                column: "TankId");

            migrationBuilder.CreateIndex(
                name: "IX_TargetScenarios_Name",
                table: "TargetScenarios",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TankMonthlyTargets");

            migrationBuilder.DropTable(
                name: "TargetScenarios");

            migrationBuilder.RenameColumn(
                name: "Real_Volume",
                table: "Measurements",
                newName: "Actual_volume");
        }
    }
}
