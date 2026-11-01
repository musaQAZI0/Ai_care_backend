namespace AiCare.Domain;

public static class MeasurableCareGoalPolicy
{
    public const decimal MaximumValue = 999999999999.9999m;
    public static bool ValidValue(decimal value) => value >= -MaximumValue && value <= MaximumValue && decimal.Round(value, 4) == value;

    public static string? Definition(string title, string unit, string actions, string reason,
        decimal baseline, decimal target, DateOnly baselineDate, DateOnly reviewDate, DateOnly targetDate, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 500 || string.IsNullOrWhiteSpace(unit) || unit.Length > 80 ||
            string.IsNullOrWhiteSpace(actions) || actions.Length > 10000 || string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            return "Goal, measurement unit, planned actions and change reason are required.";
        if (!ValidValue(baseline) || !ValidValue(target)) return "Baseline and target must have at most four decimal places and fit the supported measurement range.";
        if (baselineDate == default || baselineDate > today || reviewDate < baselineDate || targetDate < reviewDate)
            return "Baseline cannot be in the future; review must follow baseline and not exceed the target date.";
        return null;
    }

    public static string? Progress(string status, decimal? value, string note, DateTimeOffset observedAt, DateOnly baselineDate, DateTimeOffset now)
    {
        if (status is not ("InProgress" or "Achieved" or "NeedsReview" or "OnHold") || string.IsNullOrWhiteSpace(note) || note.Length > 10000)
            return "A staff-reviewed progress status and evidence note are required.";
        if ((status is "InProgress" or "Achieved") && value is null || value is decimal number && !ValidValue(number))
            return "Measured progress requires a value with at most four decimal places.";
        if (observedAt == default || observedAt > now.AddMinutes(5)) return "Observed time must be at or after baseline and cannot be in the future.";
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(observedAt, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        if (date < baselineDate)
            return "Observed time must be at or after baseline and cannot be in the future.";
        return null;
    }
}
