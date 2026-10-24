using System.Net;
using System.Text;
using System.Text.Json;
using AiCare.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiCare.Tests;

public sealed class ResendEmailClientTests
{
    [Fact]
    public async Task InvoiceMessageIncludesBearerKeyIdempotencyAndBase64Pdf()
    {
        var handler = new CaptureHandler();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Resend:ApiKey"] = "test-api-key",
            ["Resend:FromAddress"] = "notifications@example.com",
            ["Resend:FromName"] = "AiCare",
            ["Resend:ReplyToAddress"] = "billing@example.com"
        }).Build();
        var sender = new ResendEmailClient(new HttpClient(handler), configuration);
        var pdf = Encoding.ASCII.GetBytes("%PDF-test");

        var id = await sender.SendMessageAsync(new ResendMessage(
            "client@example.com", "Invoice INV-1", "Invoice attached", "invoice:one",
            Attachment: new ResendAttachment("INV-1.pdf", pdf)), CancellationToken.None);

        Assert.Equal("provider-message-id", id);
        Assert.Equal("https://api.resend.com/emails", handler.Url);
        Assert.Equal("Bearer test-api-key", handler.Authorization);
        Assert.Equal("invoice:one", handler.IdempotencyKey);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("AiCare <notifications@example.com>", payload.RootElement.GetProperty("from").GetString());
        Assert.Equal("client@example.com", payload.RootElement.GetProperty("to")[0].GetString());
        Assert.Equal("billing@example.com", payload.RootElement.GetProperty("reply_to").GetString());
        var attachment = payload.RootElement.GetProperty("attachments")[0];
        Assert.Equal("INV-1.pdf", attachment.GetProperty("filename").GetString());
        Assert.Equal(pdf, Convert.FromBase64String(attachment.GetProperty("content").GetString()!));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"provider-message-id\"}", Encoding.UTF8, "application/json")
            };
        }
    }
}
