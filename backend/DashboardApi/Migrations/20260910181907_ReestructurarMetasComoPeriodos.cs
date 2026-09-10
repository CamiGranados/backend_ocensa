using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardApi.Migrations
{
    /// <inheritdoc />
    public partial class ReestructurarMetasComoPeriodos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Esta carga cambia la estructura de metas (mensual → periodos de vigencia) y
            // agrega RowHash con índice ÚNICO en Measurements. Los datos actuales se subieron
            // con valores errados y hay que recargarlos desde cero, así que se vacían aquí:
            // sin esto, el índice único de RowHash fallaría sobre las filas existentes.
            migrationBuilder.Sql("DELETE FROM [PhysicalChemistries];");
            migrationBuilder.Sql("DELETE FROM [Measurements];");
            migrationBuilder.Sql("DELETE FROM [Uploads];");
            migrationBuilder.Sql("DBCC CHECKIDENT ('[PhysicalChemistries]', RESEED, 0) WITH NO_INFOMSGS;");
            migrationBuilder.Sql("DBCC CHECKIDENT ('[Measurements]', RESEED, 0) WITH NO_INFOMSGS;");
            migrationBuilder.Sql("DBCC CHECKIDENT ('[Uploads]', RESEED, 0) WITH NO_INFOMSGS;");

            migrationBuilder.DropTable(
                name: "TankMonthlyTargets");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowHash",
                table: "Measurements",
                type: "binary(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateTable(
                name: "MeasurementTargetSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MeasurementId = table.Column<long>(type: "bigint", nullable: false),
                    TankId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    Date = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WaterContractualMin_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    WaterContractualMax_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PeriodicityContractual = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DoseContractual_ppm = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    GallonsContractual_Month = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    WaterBaseline_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PeriodicityBaseline = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DoseBaseline_ppm = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    GallonsBaseline_Month = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    WaterActual_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DoseActual_ppm = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    GallonsActual_Month = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    NominalCapacity_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    FluidType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeasurementTargetSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeasurementTargetSamples_Measurements_MeasurementId",
                        column: x => x.MeasurementId,
                        principalTable: "Measurements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TankProfilePeriods",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TankId = table.Column<long>(type: "bigint", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    NominalCapacity_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    FluidType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TankProfilePeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TankProfilePeriods_Tanks_TankId",
                        column: x => x.TankId,
                        principalTable: "Tanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TankTargetPeriods",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    TankId = table.Column<long>(type: "bigint", nullable: false),
                    ScenarioId = table.Column<int>(type: "int", nullable: false),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true),
                    EstimatedWaterMin_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    EstimatedWaterMax_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Periodicity_BatchesPerMonth = table.Column<int>(type: "int", nullable: true),
                    Dose_ppm = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    EstimatedGallons_Month = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TankTargetPeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TankTargetPeriods_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TankTargetPeriods_Tanks_TankId",
                        column: x => x.TankId,
                        principalTable: "Tanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TankTargetPeriods_TargetScenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalTable: "TargetScenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Measurements_RowHash",
                table: "Measurements",
                column: "RowHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementTargetSamples_MeasurementId",
                table: "MeasurementTargetSamples",
                column: "MeasurementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeasurementTargetSamples_TankId_Date",
                table: "MeasurementTargetSamples",
                columns: new[] { "TankId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_TankProfilePeriods_TankId_ValidFrom",
                table: "TankProfilePeriods",
                columns: new[] { "TankId", "ValidFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TankTargetPeriods_CompanyId_TankId_ScenarioId_ValidFrom",
                table: "TankTargetPeriods",
                columns: new[] { "CompanyId", "TankId", "ScenarioId", "ValidFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TankTargetPeriods_ScenarioId",
                table: "TankTargetPeriods",
                column: "ScenarioId");

            migrationBuilder.CreateIndex(
                name: "IX_TankTargetPeriods_TankId",
                table: "TankTargetPeriods",
                column: "TankId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeasurementTargetSamples");

            migrationBuilder.DropTable(
                name: "TankProfilePeriods");

            migrationBuilder.DropTable(
                name: "TankTargetPeriods");

            migrationBuilder.DropIndex(
                name: "IX_Measurements_RowHash",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "RowHash",
                table: "Measurements");

            migrationBuilder.CreateTable(
                name: "TankMonthlyTargets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    ScenarioId = table.Column<int>(type: "int", nullable: false),
                    TankId = table.Column<long>(type: "bigint", nullable: false),
                    Dose_ppm = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    EstimatedGallons_Month = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    EstimatedWaterMax_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    EstimatedWaterMin_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Period = table.Column<DateOnly>(type: "date", nullable: false),
                    Periodicity_BatchesPerMonth = table.Column<int>(type: "int", nullable: true),
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
        }
    }
}
