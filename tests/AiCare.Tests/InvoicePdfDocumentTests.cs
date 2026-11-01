using AiCare.Api;
using Xunit;

namespace AiCare.Tests;

public sealed class InvoicePdfDocumentTests
{
    private static InvoicePdfData Invoice(string provider = "Provider", string description = "Care") =>
        new("INV-1", new(2026, 10, 28), new(2026, 11, 28), "Issued", provider,
            "Address", "finance@example.invalid", "123", "COMPANY", "VAT", "Payer",
            "Billing address", "Funder", "30 days", "Account 123", "GBP",
            10m, 0m, 0m, 10m, 0m, 0m, "Approved treatment",
            [new(description, 1m, 10m, 10m)]);

    [Theory]
    [InlineData("Jos\u00e9")]
    [InlineData("\u6f22\u5b57")]
    [InlineData("\u0627\u062d\u0645\u062f")]
    public void ProviderNamesProduceAnEmbeddedUnicodePdf(string text) =>
        Assert.Contains("/FontFile", System.Text.Encoding.ASCII.GetString(InvoicePdfDocument.Create(Invoice(provider: text))));

    [Fact]
    public void LineDescriptionsSupportCurrencySymbols() =>
        Assert.StartsWith("%PDF-",System.Text.Encoding.ASCII.GetString(InvoicePdfDocument.Create(Invoice(description: "Care \u00a310"))));

    [Fact]
    public void UnsupportedInvoiceTextStillFailsBeforeArchival() =>
        Assert.Throws<FinancePdfTextException>(() => InvoicePdfDocument.Create(Invoice(provider: "\ud83d\ude00")));

    [Fact]
    public void SupportedInvoiceStillProducesPdf()
    {
        var bytes = InvoicePdfDocument.Create(Invoice());
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(bytes));
    }
}
