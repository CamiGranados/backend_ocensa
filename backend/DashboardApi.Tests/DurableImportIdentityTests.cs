using DashboardApi.Imports;

namespace DashboardApi.Tests;

public sealed class DurableImportIdentityTests
{
    private const string FileHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Same_file_and_versions_are_idempotent()
    {
        var first = DurableImportIdentity.CreateBatchIdentity(FileHash, "schema-v1", "classifier-v1");
        var second = DurableImportIdentity.CreateBatchIdentity(FileHash, "schema-v1", "classifier-v1");

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    [Fact]
    public void Classifier_version_is_part_of_batch_and_release_identity()
    {
        var batchV1 = DurableImportIdentity.CreateBatchIdentity(FileHash, "schema-v1", "classifier-v1");
        var batchV2 = DurableImportIdentity.CreateBatchIdentity(FileHash, "schema-v1", "classifier-v2");
        var releaseV1 = DurableImportIdentity.CreateReleaseIdentity(batchV1, "schema-v1", "classifier-v1");
        var releaseV2 = DurableImportIdentity.CreateReleaseIdentity(batchV1, "schema-v1", "classifier-v2");

        Assert.NotEqual(batchV1, batchV2);
        Assert.NotEqual(releaseV1, releaseV2);
    }

    [Fact]
    public void Schema_version_is_part_of_identity()
    {
        var first = DurableImportIdentity.CreateBatchIdentity(FileHash, "schema-v1", "classifier-v1");
        var second = DurableImportIdentity.CreateBatchIdentity(FileHash, "schema-v2", "classifier-v1");

        Assert.NotEqual(first, second);
    }
}
