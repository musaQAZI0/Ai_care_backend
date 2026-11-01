using System.Globalization;

namespace AiCare.Api;

internal static class MedicationDoseTimes
{
    internal static bool TryNormalize(IEnumerable<string>? values, out string[] times)
    {
        times = [];
        if (values is null) return true;
        var supplied = values.ToArray();
        if (supplied.Length > 12) return false;
        var parsed = new List<string>(supplied.Length);
        foreach (var value in supplied)
        {
            if (value is null || !TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time)) return false;
            parsed.Add(time.ToString("HH:mm", CultureInfo.InvariantCulture));
        }
        if (parsed.Count != parsed.Distinct(StringComparer.Ordinal).Count()) return false;
        times = parsed.Order(StringComparer.Ordinal).ToArray();
        return true;
    }
}
