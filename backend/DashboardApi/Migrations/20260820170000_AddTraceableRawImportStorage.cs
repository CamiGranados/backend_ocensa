using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardApi.Migrations;

public partial class AddTraceableRawImportStorage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ImportBatches",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                BatchIdentity = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                FileSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                OriginalFileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                SchemaVersion = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                ClassifierVersion = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                InspectedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                State = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                BlockedReasonsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                WarningsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                SheetCount = table.Column<int>(type: "int", nullable: false),
                InspectedCellCount = table.Column<long>(type: "bigint", nullable: false),
                Revision = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ImportBatches", x => x.Id);
                table.CheckConstraint("CK_ImportBatches_CellCount", "[InspectedCellCount] >= 0");
                table.CheckConstraint("CK_ImportBatches_FileSize", "[FileSizeBytes] > 0");
                table.CheckConstraint("CK_ImportBatches_Revision", "[Revision] >= 0");
            });

        migrationBuilder.CreateTable(
            name: "DatasetReleases",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                ImportBatchId = table.Column<long>(type: "bigint", nullable: false),
                ReleaseIdentity = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                SchemaVersion = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                ClassifierVersion = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                State = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                IsPublished = table.Column<bool>(type: "bit", nullable: false),
                ApprovedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                ApprovedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                BlockedReasonsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                Revision = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DatasetReleases", x => x.Id);
                table.CheckConstraint(
                    "CK_DatasetReleases_PublishedRequiresApproval",
                    "[IsPublished] = 0 OR ([State] = 'Published' AND [ApprovedBy] IS NOT NULL AND [ApprovedAtUtc] IS NOT NULL)");
                table.CheckConstraint("CK_DatasetReleases_Revision", "[Revision] >= 0");
                table.CheckConstraint(
                    "CK_DatasetReleases_StateMatchesPublication",
                    "([State] = 'Published' AND [IsPublished] = 1) OR ([State] <> 'Published' AND [IsPublished] = 0)");
                table.ForeignKey(
                    name: "FK_DatasetReleases_ImportBatches_ImportBatchId",
                    column: x => x.ImportBatchId,
                    principalTable: "ImportBatches",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "WorkbookSheets",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                ImportBatchId = table.Column<long>(type: "bigint", nullable: false),
                SheetIndex = table.Column<int>(type: "int", nullable: false),
                SheetName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                HeaderRowSource = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                HeadersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                DataRowCount = table.Column<int>(type: "int", nullable: false),
                InspectedCellCount = table.Column<long>(type: "bigint", nullable: false),
                StatusCountsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                WarningsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkbookSheets", x => x.Id);
                table.CheckConstraint("CK_WorkbookSheets_CellCount", "[InspectedCellCount] >= 0");
                table.CheckConstraint("CK_WorkbookSheets_Index", "[SheetIndex] > 0");
                table.ForeignKey(
                    name: "FK_WorkbookSheets_ImportBatches_ImportBatchId",
                    column: x => x.ImportBatchId,
                    principalTable: "ImportBatches",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "RawCells",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                WorkbookSheetId = table.Column<long>(type: "bigint", nullable: false),
                Sequence = table.Column<int>(type: "int", nullable: false),
                SourceCell = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                SourceRowNumber = table.Column<int>(type: "int", nullable: false),
                SourceColumnNumber = table.Column<int>(type: "int", nullable: false),
                HeaderText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                HeaderSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                RawText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                NumericValue = table.Column<decimal>(type: "decimal(38,18)", precision: 38, scale: 18, nullable: true),
                NumericValueExact = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                DateValue = table.Column<DateTime>(type: "datetime2", nullable: true),
                Qualifier = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                Unit = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                Status = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                ParseRuleId = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                CellDataType = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                FormulaA1 = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Warning = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: true),
                LineageSha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RawCells", x => x.Id);
                table.CheckConstraint("CK_RawCells_Column", "[SourceColumnNumber] > 0");
                table.CheckConstraint("CK_RawCells_Row", "[SourceRowNumber] > 0");
                table.CheckConstraint("CK_RawCells_Sequence", "[Sequence] >= 0");
                table.ForeignKey(
                    name: "FK_RawCells_WorkbookSheets_WorkbookSheetId",
                    column: x => x.WorkbookSheetId,
                    principalTable: "WorkbookSheets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_DatasetReleases_ImportBatchId",
            table: "DatasetReleases",
            column: "ImportBatchId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_DatasetReleases_IsPublished_State_CreatedAtUtc",
            table: "DatasetReleases",
            columns: new[] { "IsPublished", "State", "CreatedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_DatasetReleases_ReleaseIdentity",
            table: "DatasetReleases",
            column: "ReleaseIdentity",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ImportBatches_BatchIdentity",
            table: "ImportBatches",
            column: "BatchIdentity",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ImportBatches_FileSha256_SchemaVersion_ClassifierVersion",
            table: "ImportBatches",
            columns: new[] { "FileSha256", "SchemaVersion", "ClassifierVersion" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RawCells_Status",
            table: "RawCells",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_RawCells_WorkbookSheetId_DateValue",
            table: "RawCells",
            columns: new[] { "WorkbookSheetId", "DateValue" });

        migrationBuilder.CreateIndex(
            name: "IX_RawCells_WorkbookSheetId_HeaderSha256",
            table: "RawCells",
            columns: new[] { "WorkbookSheetId", "HeaderSha256" });

        migrationBuilder.CreateIndex(
            name: "IX_RawCells_WorkbookSheetId_Sequence",
            table: "RawCells",
            columns: new[] { "WorkbookSheetId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RawCells_WorkbookSheetId_SourceCell",
            table: "RawCells",
            columns: new[] { "WorkbookSheetId", "SourceCell" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RawCells_WorkbookSheetId_SourceRowNumber_SourceColumnNumber",
            table: "RawCells",
            columns: new[] { "WorkbookSheetId", "SourceRowNumber", "SourceColumnNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_WorkbookSheets_ImportBatchId_SheetIndex",
            table: "WorkbookSheets",
            columns: new[] { "ImportBatchId", "SheetIndex" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_WorkbookSheets_ImportBatchId_SheetName",
            table: "WorkbookSheets",
            columns: new[] { "ImportBatchId", "SheetName" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "DatasetReleases");
        migrationBuilder.DropTable(name: "RawCells");
        migrationBuilder.DropTable(name: "WorkbookSheets");
        migrationBuilder.DropTable(name: "ImportBatches");
    }
}
