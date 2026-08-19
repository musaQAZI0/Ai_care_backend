using System.IO.Compression;
using System.Text;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AiCare.Tests;

public sealed class DocumentUploadSecurityTests
{
    private readonly IDocumentMalwareScanner _scanner = new BasicDocumentMalwareScanner();

    [Fact]
    public async Task ValidPdfIsAccepted()
    {
        var file = File("care-plan.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nvalid-test-document"));

        var result = await DocumentUploadValidator.ValidateAsync(file, 10 * 1024 * 1024, _scanner);

        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task ValidPngIsAccepted()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 };
        var file = File("photo.png", "image/png", bytes);

        var result = await DocumentUploadValidator.ValidateAsync(file, 10 * 1024 * 1024, _scanner);

        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task ValidDocxPackageIsAccepted()
    {
        var file = File("care-plan.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", BuildDocx());

        var result = await DocumentUploadValidator.ValidateAsync(file, 10 * 1024 * 1024, _scanner);

        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task ExecutableExtensionIsRejected()
    {
        var file = File("payload.exe", "application/octet-stream", Encoding.ASCII.GetBytes("MZpayload"));

        var result = await DocumentUploadValidator.ValidateAsync(file, 10 * 1024 * 1024, _scanner);

        Assert.False(result.Accepted);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, result.StatusCode);
    }

    [Fact]
    public async Task ExecutableRenamedAsPdfIsRejectedBySignature()
    {
        var file = File("payload.pdf", "application/pdf", Encoding.ASCII.GetBytes("MZthis-is-not-a-pdf"));

        var result = await DocumentUploadValidator.ValidateAsync(file, 10 * 1024 * 1024, _scanner);

        Assert.False(result.Accepted);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, result.StatusCode);
        Assert.Contains("content", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongMimeTypeIsRejected()
    {
        var file = File("care-plan.pdf", "text/html", Encoding.ASCII.GetBytes("%PDF-1.7\nvalid"));

        var result = await DocumentUploadValidator.ValidateAsync(file, 10 * 1024 * 1024, _scanner);

        Assert.False(result.Accepted);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, result.StatusCode);
        Assert.Contains("MIME", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OversizedFileIsRejected()
    {
        var file = File("large.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nlarge"));

        var result = await DocumentUploadValidator.ValidateAsync(file, 4, _scanner);

        Assert.False(result.Accepted);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, result.StatusCode);
    }

    [Fact]
    public async Task PathTraversalFileNameIsRejected()
    {
        var file = File("../../care-plan.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.7\nvalid"));

        var result = await DocumentUploadValidator.ValidateAsync(file, 10 * 1024 * 1024, _scanner);

        Assert.False(result.Accepted);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task MalformedDocxIsRejected()
    {
        var file = File("care-plan.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Encoding.UTF8.GetBytes("not-a-zip"));

        var result = await DocumentUploadValidator.ValidateAsync(file, 10 * 1024 * 1024, _scanner);

        Assert.False(result.Accepted);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, result.StatusCode);
    }

    [Fact]
    public async Task EicarSignatureIsRejectedAfterFileValidation()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7\nEICAR-STANDARD-ANTIVIRUS-TEST-FILE");
        var file = File("infected.pdf", "application/pdf", bytes);

        var result = await DocumentUploadValidator.ValidateAsync(file, 10 * 1024 * 1024, _scanner);

        Assert.False(result.Accepted);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        Assert.Contains("malware", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyFileIsRejected()
    {
        var file = File("empty.pdf", "application/pdf", Array.Empty<byte>());

        var result = await DocumentUploadValidator.ValidateAsync(file, 10 * 1024 * 1024, _scanner);

        Assert.False(result.Accepted);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
    }

    private static FormFile File(string name, string contentType, byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    private static byte[] BuildDocx()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"></Types>");
            WriteEntry(archive, "word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"></w:document>");
        }
        return memory.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
        writer.Write(content);
    }
}
