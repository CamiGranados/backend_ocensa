using System.Buffers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace DashboardApi.Imports;

public interface IMultipartWorkbookReader
{
    Task<IncomingWorkbook> ReadAsync(HttpRequest request, CancellationToken cancellationToken);
}

public sealed class MultipartWorkbookReader : IMultipartWorkbookReader
{
    private const string ExpectedFormFieldName = "file";

    public async Task<IncomingWorkbook> ReadAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ContentLength is > ImportLimits.MaxMultipartBodyBytes)
        {
            throw new ImportPreflightException(
                StatusCodes.Status413PayloadTooLarge,
                "MULTIPART_BODY_TOO_LARGE",
                $"La solicitud excede {ImportLimits.MaxMultipartBodyBytes} bytes.");
        }

        var boundary = GetBoundary(request.ContentType);
        var reader = new MultipartReader(boundary, request.Body)
        {
            HeadersCountLimit = ImportLimits.MaxHeadersPerSection,
            HeadersLengthLimit = ImportLimits.MaxHeadersLengthBytes,
            BodyLengthLimit = ImportLimits.MaxMultipartBodyBytes
        };

        IncomingWorkbook? incomingWorkbook = null;
        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
                {
                    throw BadMultipart("INVALID_CONTENT_DISPOSITION", "La sección multipart no tiene Content-Disposition válido.");
                }

                var fieldName = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                var submittedFileName = HeaderUtilities.RemoveQuotes(
                    disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;

                if (string.IsNullOrWhiteSpace(submittedFileName))
                {
                    throw BadMultipart(
                        "UNEXPECTED_MULTIPART_FIELD",
                        "El contrato admite una única sección de archivo llamada 'file'.");
                }

                if (!string.Equals(fieldName, ExpectedFormFieldName, StringComparison.Ordinal))
                {
                    throw BadMultipart(
                        "UNEXPECTED_FILE_FIELD",
                        "El archivo debe enviarse exactamente en el campo 'file'.");
                }

                if (incomingWorkbook is not null)
                {
                    throw BadMultipart("MULTIPLE_FILES_NOT_ALLOWED", "Cada lote admite exactamente un archivo XLSX.");
                }

                var safeFileName = Path.GetFileName(submittedFileName);
                if (string.IsNullOrWhiteSpace(safeFileName)
                    || !string.Equals(Path.GetExtension(safeFileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ImportPreflightException(
                        StatusCodes.Status415UnsupportedMediaType,
                        "XLSX_REQUIRED",
                        "La importación versionada solo admite archivos .xlsx.");
                }

                incomingWorkbook = await CopyToBoundedTemporaryFileAsync(
                    section.Body,
                    safeFileName,
                    section.ContentType,
                    cancellationToken);
            }

            if (incomingWorkbook is null)
            {
                throw BadMultipart("FILE_REQUIRED", "No se recibió el campo de archivo 'file'.");
            }

            return incomingWorkbook;
        }
        catch (InvalidDataException exception)
        {
            if (incomingWorkbook is not null)
            {
                await incomingWorkbook.DisposeAsync();
            }

            throw new ImportPreflightException(
                StatusCodes.Status400BadRequest,
                "INVALID_MULTIPART_BODY",
                "El cuerpo multipart está truncado o no cumple el formato esperado.",
                innerException: exception);
        }
        catch
        {
            if (incomingWorkbook is not null)
            {
                await incomingWorkbook.DisposeAsync();
            }

            throw;
        }
    }

    private static async Task<IncomingWorkbook> CopyToBoundedTemporaryFileAsync(
        Stream source,
        string fileName,
        string? contentType,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"thps-import-{Guid.NewGuid():N}.upload");

        FileStream? target = null;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            target = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long totalBytes = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                totalBytes = checked(totalBytes + read);
                if (totalBytes > ImportLimits.MaxWorkbookBytes)
                {
                    throw new ImportPreflightException(
                        StatusCodes.Status413PayloadTooLarge,
                        "WORKBOOK_TOO_LARGE",
                        $"El archivo excede el máximo de {ImportLimits.MaxWorkbookBytes} bytes.");
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (totalBytes == 0)
            {
                throw BadMultipart("EMPTY_WORKBOOK", "El archivo recibido está vacío.");
            }

            await target.FlushAsync(cancellationToken);
            target.Position = 0;
            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

            return new IncomingWorkbook(
                temporaryPath,
                target,
                fileName,
                contentType,
                totalBytes,
                sha256);
        }
        catch
        {
            if (target is not null)
            {
                await target.DisposeAsync();
            }

            TryDelete(temporaryPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static string GetBoundary(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            || !string.Equals(mediaType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportPreflightException(
                StatusCodes.Status415UnsupportedMediaType,
                "MULTIPART_FORM_DATA_REQUIRED",
                "Content-Type debe ser multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw BadMultipart("MISSING_MULTIPART_BOUNDARY", "Falta el boundary multipart.");
        }

        if (boundary.Length > ImportLimits.MaxBoundaryLength)
        {
            throw BadMultipart("MULTIPART_BOUNDARY_TOO_LONG", "El boundary multipart excede el límite permitido.");
        }

        return boundary;
    }

    private static ImportPreflightException BadMultipart(string code, string message)
    {
        return new ImportPreflightException(StatusCodes.Status400BadRequest, code, message);
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // DeleteOnClose remains the primary cleanup mechanism.
        }
        catch (UnauthorizedAccessException)
        {
            // Do not mask the original import result with a cleanup error.
        }
    }
}

public sealed class IncomingWorkbook : IAsyncDisposable
{
    private readonly string _temporaryPath;
    private bool _disposed;

    internal IncomingWorkbook(
        string temporaryPath,
        FileStream content,
        string originalFileName,
        string? contentType,
        long length,
        string sha256)
    {
        _temporaryPath = temporaryPath;
        Content = content;
        OriginalFileName = originalFileName;
        ContentType = contentType;
        Length = length;
        Sha256 = sha256;
    }

    public FileStream Content { get; }
    public string OriginalFileName { get; }
    public string? ContentType { get; }
    public long Length { get; }
    public string Sha256 { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Content.DisposeAsync();
        MultipartWorkbookReader.TryDelete(_temporaryPath);
        GC.SuppressFinalize(this);
    }
}
