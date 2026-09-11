using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardApi.Migrations
{
    /// <inheritdoc />
    public partial class ReestructurarMeasurementsYOperaciones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Measurements_Companies_CompanyId",
                table: "Measurements");

            migrationBuilder.DropForeignKey(
                name: "FK_Measurements_Tanks_TankId",
                table: "Measurements");

            migrationBuilder.DropForeignKey(
                name: "FK_Measurements_Uploads_UploadId",
                table: "Measurements");

            migrationBuilder.DropIndex(
                name: "IX_Measurements_CompanyId_TankId_Date",
                table: "Measurements");

            migrationBuilder.DropIndex(
                name: "IX_Measurements_TankId",
                table: "Measurements");

            migrationBuilder.DropIndex(
                name: "IX_Measurements_UploadId",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "API",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "Actual_Injected_Dose",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "Calculated_FWV",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "Category_Nace",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "Date",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "Estimated_FWV",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "GSV_bls",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "Increased_FWV",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "Injection_date",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "Last_Biocida_Injection",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "Level_Alarm",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "Programmed_volume",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "Real_Volume",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "Reported_FWV",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "Scheduled_Dose",
                table: "Measurements");

            migrationBuilder.DropColumn(
                name: "UploadId",
                table: "Measurements");

            migrationBuilder.RenameColumn(
                name: "TankId",
                table: "Measurements",
                newName: "OperationId");

            migrationBuilder.AlterColumn<string>(
                name: "Sampling_Point",
                table: "Measurements",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(30)",
                oldMaxLength: 30);

            migrationBuilder.CreateTable(
                name: "TankDailyOperations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    TankId = table.Column<long>(type: "bigint", nullable: false),
                    Date = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Injection_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Last_Biocida_Injection = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Scheduled_Dose = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Actual_Injected_Dose = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Programmed_volume = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Real_Volume = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    GSV_bls = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    API = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Estimated_FWV = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Reported_FWV = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Calculated_FWV = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Increased_FWV = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Category_Nace = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Level_Alarm = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TankDailyOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TankDailyOperations_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TankDailyOperations_Tanks_TankId",
                        column: x => x.TankId,
                        principalTable: "Tanks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Measurements_OperationId_Sampling_Point",
                table: "Measurements",
                columns: new[] { "OperationId", "Sampling_Point" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TankDailyOperations_CompanyId_TankId_Date",
                table: "TankDailyOperations",
                columns: new[] { "CompanyId", "TankId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TankDailyOperations_TankId",
                table: "TankDailyOperations",
                column: "TankId");

            migrationBuilder.AddForeignKey(
                name: "FK_Measurements_TankDailyOperations_OperationId",
                table: "Measurements",
                column: "OperationId",
                principalTable: "TankDailyOperations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Measurements_TankDailyOperations_OperationId",
                table: "Measurements");

            migrationBuilder.DropTable(
                name: "TankDailyOperations");

            migrationBuilder.DropIndex(
                name: "IX_Measurements_OperationId_Sampling_Point",
                table: "Measurements");

            migrationBuilder.RenameColumn(
                name: "OperationId",
                table: "Measurements",
                newName: "TankId");

            migrationBuilder.AlterColumn<string>(
                name: "Sampling_Point",
                table: "Measurements",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10);

            migrationBuilder.AddColumn<decimal>(
                name: "API",
                table: "Measurements",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Actual_Injected_Dose",
                table: "Measurements",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Calculated_FWV",
                table: "Measurements",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category_Nace",
                table: "Measurements",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "CompanyId",
                table: "Measurements",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "Date",
                table: "Measurements",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<decimal>(
                name: "Estimated_FWV",
                table: "Measurements",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "GSV_bls",
                table: "Measurements",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Increased_FWV",
                table: "Measurements",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "Injection_date",
                table: "Measurements",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Last_Biocida_Injection",
                table: "Measurements",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Level_Alarm",
                table: "Measurements",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "Programmed_volume",
                table: "Measurements",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Real_Volume",
                table: "Measurements",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Reported_FWV",
                table: "Measurements",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Scheduled_Dose",
                table: "Measurements",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UploadId",
                table: "Measurements",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Measurements_CompanyId_TankId_Date",
                table: "Measurements",
                columns: new[] { "CompanyId", "TankId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Measurements_TankId",
                table: "Measurements",
                column: "TankId");

            migrationBuilder.CreateIndex(
                name: "IX_Measurements_UploadId",
                table: "Measurements",
                column: "UploadId");

            migrationBuilder.AddForeignKey(
                name: "FK_Measurements_Companies_CompanyId",
                table: "Measurements",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Measurements_Tanks_TankId",
                table: "Measurements",
                column: "TankId",
                principalTable: "Tanks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Measurements_Uploads_UploadId",
                table: "Measurements",
                column: "UploadId",
                principalTable: "Uploads",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
