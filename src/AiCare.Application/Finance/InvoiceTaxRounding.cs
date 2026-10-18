namespace AiCare.Application.Finance;

public enum InvoiceTaxRoundingPolicy
{
    Total,
    Line
}

public static class InvoiceTaxRounding
{
    public static decimal Calculate(IEnumerable<decimal> lineAmounts, decimal taxRate, InvoiceTaxRoundingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(lineAmounts);
        if (taxRate is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(taxRate));

        var amounts = lineAmounts.ToArray();
        return policy switch
        {
            InvoiceTaxRoundingPolicy.Line => amounts.Sum(amount => Round(amount * taxRate / 100m)),
            InvoiceTaxRoundingPolicy.Total => Round(amounts.Sum() * taxRate / 100m),
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };
    }

    private static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
