using System.Security.Cryptography;
using DashboardApi.Imports;
using Microsoft.AspNetCore.Http;

namespace DashboardApi.Tests;

public sealed class MultipartWorkbookReaderTests
{
    [Fact]
    public async Task Reader_streams_file_and_calculates_sha256()
    {
        var bytes = TestWorkbookFactory.Create("Datos");
        var context = await TestWorkbookFactory.CreateMultipartRequestAsync(bytes);
        var reader = new MultipartWorkbookReader();

        await using var result = await reader.ReadAsync(context.Request, CancellationToken.None);

        Assert.Equal(bytes.LongLength, result.Length);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            result.Sha256);
        Assert.Equal("dataset.xlsx", result.OriginalFileName);
        Assert.True(result.Content.CanSeek);
        Assert.Equal(0, result.Content.Position);
    }

    [Fact]
    public async Task Oversized_content_length_is_rejected_before_reading_body()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=test";
        context.Request.ContentLength = ImportLimits.MaxMultipartBodyBytes + 1;
        context.Request.Body = Stream.Null;
        var reader = new MultipartWorkbookReader();

        var exception = await Assert.ThrowsAsync<ImportPreflightException>(
            () => reader.ReadAsync(context.Request, CancellationToken.None));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, exception.StatusCode);
        Assert.Equal("MULTIPART_BODY_TOO_LARGE", exception.Code);
    }

    [Fact]
    public async Task Noncanonical_files_field_is_rejected()
    {
        var bytes = TestWorkbookFactory.Create("Datos");
        var context = await TestWorkbookFactory.CreateMultipartRequestAsync(bytes, fieldName: "files");
        var reader = new MultipartWorkbookReader();

        var exception = await Assert.ThrowsAsync<ImportPreflightException>(
            () => reader.ReadAsync(context.Request, CancellationToken.None));

        Assert.Equal("UNEXPECTED_FILE_FIELD", exception.Code);
    }

    [Fact]
    public async Task Unknown_form_field_is_rejected()
    {
        var bytes = TestWorkbookFactory.Create("Datos");
        var context = await TestWorkbookFactory.CreateMultipartRequestAsync(bytes, fieldName: "workbook");
        var reader = new MultipartWorkbookReader();

        var exception = await Assert.ThrowsAsync<ImportPreflightException>(
            () => reader.ReadAsync(context.Request, CancellationToken.None));

        Assert.Equal("UNEXPECTED_FILE_FIELD", exception.Code);
    }

    [Fact]
    public async Task Multiple_files_are_rejected_even_when_both_use_the_canonical_field()
    {
        var bytes = TestWorkbookFactory.Create("Datos");
        using var multipart = new MultipartFormDataContent("thps-test-boundary");
        multipart.Add(new ByteArrayContent(bytes), "file", "first.xlsx");
        multipart.Add(new ByteArrayContent(bytes), "file", "second.xlsx");
        var body = await multipart.ReadAsByteArrayAsync();
        var context = new DefaultHttpContext();
        context.Request.ContentType = multipart.Headers.ContentType!.ToString();
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);

        var exception = await Assert.ThrowsAsync<ImportPreflightException>(() =>
            new MultipartWorkbookReader().ReadAsync(context.Request, CancellationToken.None));

        Assert.Equal("MULTIPLE_FILES_NOT_ALLOWED", exception.Code);
    }
}
