namespace AiCare.Domain;

public static class FinanceServicePeriod
{
    public static (DateTimeOffset Start, DateTimeOffset End) FromInclusiveDates(DateOnly from,DateOnly to)
    {
        if(from==default || to<from || to==DateOnly.MaxValue) throw new ArgumentException("Valid inclusive service dates are required.");
        var zone=TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        DateTimeOffset Boundary(DateOnly date)=>new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Unspecified),zone),TimeSpan.Zero);
        return (Boundary(from),Boundary(to.AddDays(1)));
    }
}
