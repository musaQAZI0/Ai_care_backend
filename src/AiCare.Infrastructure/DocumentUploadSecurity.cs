using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AiCare.Infrastructure;

public interface IDocumentMalwareScanner
{
    Task<DocumentMalwareScanResult> ScanAsync(Stream stream, CancellationToken cancellationToken = default);
}

public sealed record DocumentMalwareScanResult(bool Safe, string Reason)
{
    public static DocumentMalwareScanResult Clean() => new(true, string.Empty);
    public static DocumentMalwareScanResult Blocked(string reason) => new(false, reason);
}

public sealed class BasicDocumentMalwareScanner : IDocumentMalwareScanner
{
    private static readonly byte[] EicarMarker = Encoding.ASCII.GetBytes("EICAR-STANDARD-ANTIVIRUS-TEST-FILE");

    public async Task<DocumentMalwareScanResult> ScanAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        if (Contains(bytes, EicarMarker))
        {
            return DocumentMalwareScanResult.Blocked("Known malware test signature detected.");
        }

        if (bytes.Length >= 2 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z')
        {
            return DocumentMalwareScanResult.Blocked("Executable file content is not permitted.");
        }

        return DocumentMalwareScanResult.Clean();
    }

    private static bool Contains(byte[] source, byte[] pattern)
    {
        if (pattern.Length == 0 || source.Length < pattern.Length) return false;
        for (var index = 0; index <= source.Length - pattern.Length; index++)
        {
            var match = true;
            for (var offset = 0; offset < pattern.Length; offset++)
            {
                if (source[index + offset] == pattern[offset]) continue;
                match = false;
                break;
            }
            if (match) return true;
        }
        return false;
    }
}

public sealed class DocumentUploadSecurityStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<DocumentUploadSecurityMiddleware>();
        next(app);
    };
}

public sealed class DocumentUploadSecurityMiddleware(
    RequestDelegate next,
    IConfiguration configuration,
    IDocumentMalwareScanner malwareScanner)
{
    private const long DefaultMaxUploadBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".jpg", ".jpeg", ".png"
    };

    private static readonly Dictionary<string, HashSet<string>> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = new(StringComparer.OrdinalIgnoreCase) { "application/pdf" },
        [".doc"] = new(StringComparer.OrdinalIgnoreCase) { "application/msword", "application/octet-stream" },
        [".docx"] = new(StringComparer.OrdinalIgnoreCase) { "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/zip", "application/octet-stream" },
        [".jpg"] = new(StringComparer.OrdinalIgnoreCase) { "image/jpeg" },
        [".jpeg"] = new(StringComparer.OrdinalIgnoreCase) { "image/jpeg" },
        [".png"] = new(StringComparer.OrdinalIgnoreCase) { "image/png" }
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var isDocumentUpload = string.Equals(
            context.Request.Path.Value,
            "/api/phase1/documents/upload",
            StringComparison.OrdinalIgnoreCase);

        if (!HttpMethods.IsPost(context.Request.Method) || !isDocumentUpload)
        {
            await next(context);
            return;
        }

        if (!context.Request.HasFormContentType)
        {
            await next(context);
            return;
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (InvalidDataException)
        {
            await Reject(context, StatusCodes.Status400BadRequest, "Document upload form data is invalid.");
            return;
        }

        var file = form.Files.GetFile("file");
        if (file is null)
        {
            await next(context);
            return;
        }

        var result = await DocumentUploadValidator.ValidateAsync(
            file,
            GetMaxUploadBytes(configuration),
            malwareScanner,
            context.RequestAborted);

        if (!result.Accepted)
        {
            await Reject(context, result.StatusCode, result.Message);
            return;
        }

        await next(context);
    }

    private static long GetMaxUploadBytes(IConfiguration configuration)
    {
        var configured = configuration.GetValue<long?>("Documents:MaxUploadBytes");
        return configured is > 0 and <= 50 * 1024 * 1024 ? configured.Value : DefaultMaxUploadBytes;
    }

    private static async Task Reject(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { message }, context.RequestAborted);
    }

    internal static IReadOnlySet<string> Extensions => AllowedExtensions;
    internal static IReadOnlyDictionary<string, HashSet<string>> MimeTypes => AllowedMimeTypes;
}

public sealed record DocumentUploadValidationResult(bool Accepted, int StatusCode, string Message)
{
    public static DocumentUploadValidationResult Ok() => new(true, StatusCodes.Status200OK, string.Empty);
    public static DocumentUploadValidationResult Reject(int statusCode, string message) => new(false, statusCode, message);
}

public static class DocumentUploadValidator
{
    public static async Task<DocumentUploadValidationResult> ValidateAsync(
        IFormFile file,
        long maxUploadBytes,
        IDocumentMalwareScanner malwareScanner,
        CancellationToken cancellationToken = default)
    {
        if (file.Length <= 0)
        {
            return DocumentUploadValidationResult.Reject(StatusCodes.Status400BadRequest, "Empty files are not permitted.");
        }

        if (file.Length > maxUploadBytes)
        {
            return DocumentUploadValidationResult.Reject(StatusCodes.Status413PayloadTooLarge, "Document exceeds the configured upload size limit.");
        }

        var suppliedName = file.FileName?.Trim() ?? string.Empty;
        if (!SafeFileName(suppliedName))
        {
            return DocumentUploadValidationResult.Reject(StatusCodes.Status400BadRequest, "Document file name is invalid.");
        }

        var extension = Path.GetExtension(suppliedName);
        if (!DocumentUploadSecurityMiddleware.Extensions.Contains(extension))
        {
            return DocumentUploadValidationResult.Reject(StatusCodes.Status415UnsupportedMediaType, "Document file type is not permitted.");
        }

        var contentType = NormalizeContentType(file.ContentType);
        if (!DocumentUploadSecurityMiddleware.MimeTypes[extension].Contains(contentType))
        {
            return DocumentUploadValidationResult.Reject(StatusCodes.Status415UnsupportedMediaType, "Document MIME type does not match the permitted file type.");
        }

        await using (var signatureStream = file.OpenReadStream())
        {
            if (!await HasExpectedSignature(signatureStream, extension, cancellationToken))
            {
                return DocumentUploadValidationResult.Reject(StatusCodes.Status415UnsupportedMediaType, "Document content does not match its file extension.");
            }
        }

        await using var malwareStream = file.OpenReadStream();
        var scan = await malwareScanner.ScanAsync(malwareStream, cancellationToken);
        if (!scan.Safe)
        {
            return DocumentUploadValidationResult.Reject(StatusCodes.Status422UnprocessableEntity, "Document failed malware screening.");
        }

        return DocumentUploadValidationResult.Ok();
    }

    internal static bool SafeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 180) return false;
        if (fileName.Any(char.IsControl) || fileName.Contains('\0')) return false;
        if (fileName.Contains('/') || fileName.Contains('\\')) return false;
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)) return false;
        if (fileName is "." or "..") return false;
        return true;
    }

    private static string NormalizeContentType(string? value)
    {
        var contentType = value?.Split(';', 2)[0].Trim();
        return string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
    }

    private static async Task<bool> HasExpectedSignature(Stream stream, string extension, CancellationToken cancellationToken)
    {
        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return await IsWordOpenXml(stream, cancellationToken);
        }

        var header = new byte[8];
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
        return extension.ToLowerInvariant() switch
        {
            ".pdf" => StartsWith(header, read, "%PDF-"u8),
            ".jpg" or ".jpeg" => read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".png" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".doc" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }),
            _ => false
        };
    }

    private static bool StartsWith(byte[] source, int sourceLength, ReadOnlySpan<byte> prefix) =>
        sourceLength >= prefix.Length && source.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static Task<bool> IsWordOpenXml(Stream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var hasContentTypes = archive.Entries.Any(entry =>
                string.Equals(entry.FullName, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase));
            var hasWordDocument = archive.Entries.Any(entry =>
                string.Equals(entry.FullName, "word/document.xml", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(hasContentTypes && hasWordDocument);
        }
        catch (InvalidDataException)
        {
            return Task.FromResult(false);
        }
    }
}
