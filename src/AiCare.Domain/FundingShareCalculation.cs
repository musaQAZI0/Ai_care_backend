namespace AiCare.Domain;

public sealed record FundingShareRule(Guid FunderId,string Type,decimal Value,int Priority);
public sealed record CalculatedFundingShare(Guid FunderId,decimal Amount);

public static class FundingShareCalculation
{
    public const string Policy="FloorPercentThenDesignatedRemainder";
    public static IReadOnlyList<CalculatedFundingShare> Calculate(decimal total,string policy,IReadOnlyList<FundingShareRule> rules)
    {
        ValidateRules(policy,rules);
        if(total<0 || decimal.Round(total,2)!=total || total>9999999999.99m)throw new ArgumentException("An exact nonnegative ledger amount is required.");
        var ordered=rules.OrderBy(r=>r.Priority).ToList();
        var shares=ordered.Where(r=>r.Type!="Remainder").Select(r=>new CalculatedFundingShare(r.FunderId,r.Type=="FixedAmount"?r.Value:decimal.Floor(total*r.Value)/100m)).ToList();
        var used=shares.Sum(s=>s.Amount);
        if(used>total)throw new ArgumentException("Funding shares exceed the approved event amount.");
        shares.Add(new(ordered.Single(r=>r.Type=="Remainder").FunderId,total-used));
        return shares;
    }
    public static void ValidateRules(string policy,IReadOnlyList<FundingShareRule> rules)
    {
        if(rules is null || policy!=Policy || rules.Count<2 ||
           rules.Select(r=>r.FunderId).Distinct().Count()!=rules.Count || rules.Select(r=>r.Priority).Distinct().Count()!=rules.Count ||
           rules.Any(r=>r.FunderId==Guid.Empty||r.Type is not ("FixedAmount" or "Percentage" or "Remainder")||r.Value<0||decimal.Round(r.Value,4)!=r.Value||
             (r.Type=="Remainder"&&r.Value!=0)||(r.Type=="FixedAmount"&&decimal.Round(r.Value,2)!=r.Value)) ||
           rules.Count(r=>r.Type=="Remainder")!=1 || rules.Where(r=>r.Type=="Percentage").Sum(r=>r.Value)>100)
            throw new ArgumentException("Explicit rounding policy, distinct funders/priorities, valid values and one remainder payer are required.");
    }
}
