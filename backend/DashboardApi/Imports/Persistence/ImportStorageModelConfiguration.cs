using Microsoft.EntityFrameworkCore;

namespace DashboardApi.Imports.Persistence;

public static class ImportStorageModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        var batch = modelBuilder.Entity<ImportBatchEntity>();
        batch.ToTable("ImportBatches", table =>
        {
            table.HasCheckConstraint("CK_ImportBatches_FileSize", "[FileSizeBytes] > 0");
            table.HasCheckConstraint("CK_ImportBatches_CellCount", "[InspectedCellCount] >= 0");
            table.HasCheckConstraint("CK_ImportBatches_Revision", "[Revision] >= 0");
        });
        batch.HasKey(entity => entity.Id);
        batch.Property(entity => entity.BatchIdentity).HasMaxLength(64).IsUnicode(false).IsRequired();
        batch.Property(entity => entity.FileSha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        batch.Property(entity => entity.OriginalFileName).HasMaxLength(260).IsRequired();
        batch.Property(entity => entity.SchemaVersion).HasMaxLength(64).IsUnicode(false).IsRequired();
        batch.Property(entity => entity.ClassifierVersion).HasMaxLength(64).IsUnicode(false).IsRequired();
        batch.Property(entity => entity.State).HasConversion<string>().HasMaxLength(32).IsUnicode(false).IsRequired();
        batch.Property(entity => entity.BlockedReasonsJson).IsRequired();
        batch.Property(entity => entity.WarningsJson).IsRequired();
        batch.Property(entity => entity.Revision).IsConcurrencyToken();
        batch.HasIndex(entity => entity.BatchIdentity).IsUnique();
        batch.HasIndex(entity => new
        {
            entity.FileSha256,
            entity.SchemaVersion,
            entity.ClassifierVersion
        }).IsUnique();

        var sheet = modelBuilder.Entity<WorkbookSheetEntity>();
        sheet.ToTable("WorkbookSheets", table =>
        {
            table.HasCheckConstraint("CK_WorkbookSheets_Index", "[SheetIndex] > 0");
            table.HasCheckConstraint("CK_WorkbookSheets_CellCount", "[InspectedCellCount] >= 0");
        });
        sheet.HasKey(entity => entity.Id);
        sheet.Property(entity => entity.SheetName).HasMaxLength(128).IsRequired();
        sheet.Property(entity => entity.HeaderRowSource).HasMaxLength(32).IsUnicode(false);
        sheet.Property(entity => entity.HeadersJson).IsRequired();
        sheet.Property(entity => entity.StatusCountsJson).IsRequired();
        sheet.Property(entity => entity.WarningsJson).IsRequired();
        sheet.HasIndex(entity => new { entity.ImportBatchId, entity.SheetIndex }).IsUnique();
        sheet.HasIndex(entity => new { entity.ImportBatchId, entity.SheetName }).IsUnique();
        sheet.HasOne(entity => entity.ImportBatch)
            .WithMany(entity => entity.Sheets)
            .HasForeignKey(entity => entity.ImportBatchId)
            .OnDelete(DeleteBehavior.Cascade);

        var rawCell = modelBuilder.Entity<RawCellEntity>();
        rawCell.ToTable("RawCells", table =>
        {
            table.HasCheckConstraint("CK_RawCells_Sequence", "[Sequence] >= 0");
            table.HasCheckConstraint("CK_RawCells_Row", "[SourceRowNumber] > 0");
            table.HasCheckConstraint("CK_RawCells_Column", "[SourceColumnNumber] > 0");
        });
        rawCell.HasKey(entity => entity.Id);
        rawCell.Property(entity => entity.SourceCell).HasMaxLength(32).IsUnicode(false).IsRequired();
        rawCell.Property(entity => entity.HeaderText);
        rawCell.Property(entity => entity.HeaderSha256).HasMaxLength(64).IsUnicode(false);
        rawCell.Property(entity => entity.RawText).IsRequired();
        rawCell.Property(entity => entity.NumericValue).HasPrecision(38, 18);
        rawCell.Property(entity => entity.NumericValueExact).HasMaxLength(64).IsUnicode(false);
        rawCell.Property(entity => entity.Qualifier).HasMaxLength(32);
        rawCell.Property(entity => entity.Unit).HasMaxLength(128);
        rawCell.Property(entity => entity.Status).HasConversion<string>().HasMaxLength(32).IsUnicode(false).IsRequired();
        rawCell.Property(entity => entity.ParseRuleId).HasMaxLength(128).IsUnicode(false).IsRequired();
        rawCell.Property(entity => entity.CellDataType).HasMaxLength(32).IsUnicode(false).IsRequired();
        rawCell.Property(entity => entity.FormulaA1);
        rawCell.Property(entity => entity.Warning).HasMaxLength(256).IsUnicode(false);
        rawCell.Property(entity => entity.LineageSha256).HasMaxLength(64).IsUnicode(false).IsRequired();
        rawCell.HasIndex(entity => new { entity.WorkbookSheetId, entity.SourceCell }).IsUnique();
        rawCell.HasIndex(entity => new { entity.WorkbookSheetId, entity.Sequence }).IsUnique();
        rawCell.HasIndex(entity => new
        {
            entity.WorkbookSheetId,
            entity.SourceRowNumber,
            entity.SourceColumnNumber
        }).IsUnique();
        rawCell.HasIndex(entity => new { entity.WorkbookSheetId, entity.HeaderSha256 });
        rawCell.HasIndex(entity => new { entity.WorkbookSheetId, entity.DateValue });
        rawCell.HasIndex(entity => entity.Status);
        rawCell.HasOne(entity => entity.WorkbookSheet)
            .WithMany(entity => entity.RawCells)
            .HasForeignKey(entity => entity.WorkbookSheetId)
            .OnDelete(DeleteBehavior.Cascade);

        var release = modelBuilder.Entity<DatasetReleaseEntity>();
        release.ToTable("DatasetReleases", table =>
        {
            table.HasCheckConstraint(
                "CK_DatasetReleases_PublishedRequiresApproval",
                "[IsPublished] = 0 OR ([State] = 'Published' AND [ApprovedBy] IS NOT NULL AND [ApprovedAtUtc] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_DatasetReleases_StateMatchesPublication",
                "([State] = 'Published' AND [IsPublished] = 1) OR ([State] <> 'Published' AND [IsPublished] = 0)");
            table.HasCheckConstraint("CK_DatasetReleases_Revision", "[Revision] >= 0");
        });
        release.HasKey(entity => entity.Id);
        release.Property(entity => entity.ReleaseIdentity).HasMaxLength(64).IsUnicode(false).IsRequired();
        release.Property(entity => entity.SchemaVersion).HasMaxLength(64).IsUnicode(false).IsRequired();
        release.Property(entity => entity.ClassifierVersion).HasMaxLength(64).IsUnicode(false).IsRequired();
        release.Property(entity => entity.State).HasConversion<string>().HasMaxLength(32).IsUnicode(false).IsRequired();
        release.Property(entity => entity.ApprovedBy).HasMaxLength(256);
        release.Property(entity => entity.BlockedReasonsJson).IsRequired();
        release.Property(entity => entity.Revision).IsConcurrencyToken();
        release.HasIndex(entity => entity.ReleaseIdentity).IsUnique();
        release.HasIndex(entity => entity.ImportBatchId).IsUnique();
        release.HasIndex(entity => new { entity.IsPublished, entity.State, entity.CreatedAtUtc });
        release.HasOne(entity => entity.ImportBatch)
            .WithOne(entity => entity.DatasetRelease)
            .HasForeignKey<DatasetReleaseEntity>(entity => entity.ImportBatchId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
