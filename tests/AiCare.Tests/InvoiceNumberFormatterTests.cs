using AiCare.Api;
using Xunit;

namespace AiCare.Tests;

public sealed class InvoiceNumberFormatterTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("inv-", false)]
    [InlineData("INV-£", false)]
    [InlineData("INV.2026", false)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTU", false)]
    [InlineData(" INV- ", true)]
    [InlineData("BRANCH_2/INV-", true)]
    public void PrefixValidationMatchesThePersistedUppercaseAsciiConstraint(string? prefix, bool valid)
        => Assert.Equal(valid, InvoiceNumberFormatter.IsValidPrefix(prefix));

    [Theory]
    [InlineData(1, true, "CARE-", 4, "CARE-2026-0001")]
    [InlineData(9999, true, "CARE-", 4, "CARE-2026-9999")]
    [InlineData(10000, true, "CARE-", 4, "CARE-2026-10000")]
    [InlineData(1000000, false, "IGNORED-", 2, "INV-2026-1000000")]
    [InlineData(1, false, "IGNORED-", 2, "INV-2026-000001")]
    public void NumberPreservesAllDigitsAndUsesDefaultFormatWhenCustomNumberingIsDisabled(
        long value, bool enabled, string prefix, int padding, string expected)
    {
        Assert.Equal(expected, InvoiceNumberFormatter.Format(2026, value, enabled, prefix, padding));
    }
}
