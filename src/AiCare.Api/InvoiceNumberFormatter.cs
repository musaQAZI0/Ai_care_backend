using System.Globalization;

namespace AiCare.Api;

public static class InvoiceNumberFormatter
{
    public static bool IsValidPrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return false;
        var normalized = prefix.Trim();
        return normalized.Length <= 20 && normalized.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '/' or '_' or '-');
    }

    public static string Format(int year, long value, bool customEnabled, string prefix, int padding)
    {
        var digits = value.ToString(CultureInfo.InvariantCulture);
        return $"{(customEnabled ? prefix : "INV-")}{year.ToString(CultureInfo.InvariantCulture)}-{digits.PadLeft(customEnabled ? padding : 6, '0')}";
    }
}
