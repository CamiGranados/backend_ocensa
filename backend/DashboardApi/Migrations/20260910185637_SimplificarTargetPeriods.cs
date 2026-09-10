using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardApi.Migrations
{
    /// <inheritdoc />
    public partial class SimplificarTargetPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeasurementTargetSamples");

            migrationBuilder.DropTable(
                name: "TankProfilePeriods");

            migrationBuilder.DropIndex(
                name: "IX_Measurements_RowHash",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "RowHash",
                table: "Measurements");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FluidType",
                table: "TankTargetPeriods");

            migrationBuilder.DropColumn(
                name: "NominalCapacity_bbl",
                table: "TankTargetPeriods");

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
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    Date = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DoseActual_ppm = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DoseBaseline_ppm = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DoseContractual_ppm = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    FluidType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GallonsActual_Month = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    GallonsBaseline_Month = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    GallonsContractual_Month = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    NominalCapacity_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PeriodicityBaseline = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PeriodicityContractual = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    TankId = table.Column<long>(type: "bigint", nullable: false),
                    WaterActual_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    WaterBaseline_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    WaterContractualMax_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    WaterContractualMin_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true)
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
                    FluidType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NominalCapacity_bbl = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    ValidFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidTo = table.Column<DateOnly>(type: "date", nullable: true)
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
        }
    }
}
