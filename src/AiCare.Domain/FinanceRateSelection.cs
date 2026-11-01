namespace AiCare.Domain;

public sealed record BillingRate(Guid VersionId, Guid RuleId, string RuleType, string DayType,
    string Unit, decimal UnitRate, decimal Minimum, int Priority, TimeOnly? From, TimeOnly? To,
    string? StaffLevel=null,string? LocationCode=null,decimal? MinimumMinutes=null,decimal? MaximumMinutes=null);

public static class FinanceRateSelection
{
    public static BillingRate Select(IReadOnlyList<BillingRate> rates, DateTimeOffset start, decimal minutes, bool bankHoliday,string? staffLevel=null,string? locationCode=null)
    {
        if (minutes <= 0 || minutes > 1440) throw new InvalidOperationException("UNSUPPORTED_SERVICE_SPAN: Approve a positive service span of at most 24 elapsed hours; longer services need explicit segments.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var local = TimeZoneInfo.ConvertTime(start, zone);
        var end = TimeZoneInfo.ConvertTime(start.AddMinutes((double)minutes), zone);
        var day = bankHoliday ? "BankHoliday" : local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "Weekend" : "Weekday";
        if(rates.Any(r=>r.StaffLevel is not null)&&string.IsNullOrWhiteSpace(staffLevel)||rates.Any(r=>r.LocationCode is not null)&&string.IsNullOrWhiteSpace(locationCode))throw new InvalidOperationException("MISSING_BILLING_CONTEXT: Explicit staff level and location are required for dimensioned rates.");
        if(rates.Any(r=>r.MinimumMinutes<0||r.MaximumMinutes<=0||(r.MinimumMinutes.HasValue&&r.MaximumMinutes.HasValue&&r.MinimumMinutes>=r.MaximumMinutes)))throw new InvalidOperationException("UNSUPPORTED_RATE: Invalid duration band.");

        if (rates.Any(r => r.RuleType is not ("Standard" or "Weekend" or "BankHoliday" or "Night") || r.Unit is not ("Hour" or "Visit") ||
            (r.RuleType=="Night" && !r.From.HasValue) || (r.RuleType=="Weekend" && r.DayType!="Weekend") || (r.RuleType=="BankHoliday" && r.DayType!="BankHoliday") ||
            r.DayType is not ("Any" or "Weekday" or "Weekend" or "BankHoliday") || r.From.HasValue != r.To.HasValue ||
            (r.From.HasValue && r.From == r.To) || r.UnitRate < 0 || r.Minimum < 0))
            throw new InvalidOperationException("UNSUPPORTED_RATE: This card contains a rule that cannot be applied to care visit billing.");
        rates=rates.Where(r=>(r.StaffLevel is null||r.StaffLevel==staffLevel)&&(r.LocationCode is null||r.LocationCode==locationCode)&&(!r.MinimumMinutes.HasValue||minutes>=r.MinimumMinutes)&&(!r.MaximumMinutes.HasValue||minutes<r.MaximumMinutes)).ToArray();
        var lastInstant = TimeZoneInfo.ConvertTime(start.AddMinutes((double)minutes).AddTicks(-1), zone);
        if (local.Date != lastInstant.Date && rates.Any(r => r.DayType != "Any"))
            throw new InvalidOperationException("SPLIT_TIME_REVIEW: The service crosses a calendar day with different day-rate rules.");
        if (rates.Any(r => r.From.HasValue) && local.Offset != end.Offset)
            throw new InvalidOperationException("SPLIT_TIME_REVIEW: A time-window service spans a clock change; an explicit elapsed-time segmentation decision is required.");
        // An overnight window is anchored to its opening day. Use complete intervals rather than comparing clock values alone.
        bool Covers(BillingRate r, bool intersect)
        {
            if (!r.From.HasValue) return true;
            for (var offset = -1; offset <= 1; offset++)
            {
                var opening = local.Date.AddDays(offset).Add(r.From.Value.ToTimeSpan());
                var closing = local.Date.AddDays(offset + (r.To < r.From ? 1 : 0)).Add(r.To!.Value.ToTimeSpan());
                if (intersect ? local.DateTime < closing && end.DateTime > opening : local.DateTime >= opening && end.DateTime <= closing) return true;
            }
            return false;
        }
        var matches = rates.Where(r => (r.DayType == day || r.DayType == "Any") &&
            Covers(r, false)).ToList();
        // Partial intersections cannot silently fall back to a cheaper or more expensive all-day rule.
        if (rates.Any(r => (r.DayType == day || r.DayType == "Any") && r.From.HasValue && Covers(r, true) && !Covers(r, false)))
            throw new InvalidOperationException("SPLIT_TIME_REVIEW: The service crosses a configured rate boundary.");
        if (matches.Count == 0) throw new InvalidOperationException("MISSING_RATE: No rule covers the approved service.");
        static int Specificity(BillingRate r)=>(r.StaffLevel is null?0:1)+(r.LocationCode is null?0:1)+(r.MinimumMinutes.HasValue||r.MaximumMinutes.HasValue?1:0);
        var ranked = matches.OrderByDescending(r => r.DayType == day).ThenByDescending(Specificity).ThenByDescending(r => r.Priority).ToList();
        var best = ranked[0];
        if (ranked.Skip(1).Any(r => (r.DayType == day) == (best.DayType == day) && Specificity(r)==Specificity(best) && r.Priority == best.Priority))
            throw new InvalidOperationException("AMBIGUOUS_RATE: More than one rule has the winning day specificity and priority.");
        return best;
    }

    public static (decimal Quantity, decimal Amount) Calculate(BillingRate rate, decimal minutes, string rounding)
    {
        if (minutes <= 0) throw new InvalidOperationException("INVALID_ATTENDANCE: Approved billable time must be positive.");
        var mode = rounding switch { "AwayFromZero" => MidpointRounding.AwayFromZero, "ToEven" => MidpointRounding.ToEven,
            _ => throw new InvalidOperationException("MISSING_POLICY: An approved monetary rounding rule is required.") };
        var raw = rate.Unit == "Visit" ? 1m : minutes / 60m;
        var quantity = Math.Max(raw, rate.Minimum);
        var roundedQuantity=decimal.Round(quantity,8,mode); var amount=decimal.Round(quantity*rate.UnitRate,2,mode);
        if(roundedQuantity<=0 || roundedQuantity>99999999.99999999m || amount>9999999999.99m)
            throw new InvalidOperationException("INVALID_AMOUNT: Quantity or amount cannot be represented safely in the financial ledger.");
        return (roundedQuantity,amount);
    }
}
