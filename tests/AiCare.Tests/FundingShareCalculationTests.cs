using AiCare.Domain;
using Xunit;
namespace AiCare.Tests;
public sealed class FundingShareCalculationTests
{
 [Theory][InlineData(0.01)][InlineData(1)][InlineData(15)][InlineData(100.01)]
 public void EveryPennyIsConservedAndTheNamedRemainderReceivesResidual(decimal total)
 {
  var first=Guid.NewGuid();var second=Guid.NewGuid();var third=Guid.NewGuid();
  var result=FundingShareCalculation.Calculate(total,FundingShareCalculation.Policy,[new(first,"Percentage",33.3333m,1),new(second,"Percentage",33.3333m,2),new(third,"Remainder",0,3)]);
  Assert.Equal(total,result.Sum(x=>x.Amount));Assert.All(result,x=>Assert.Equal(x.Amount,decimal.Round(x.Amount,2)));
  if(total==0.01m){Assert.Equal(0,result.Single(x=>x.FunderId==first).Amount);Assert.Equal(0.01m,result.Single(x=>x.FunderId==third).Amount);}
 }
 [Fact]public void FixedAmountsAndPercentagesApplyToTheDocumentedOriginalTotal()
 {
  var rules=new[]{new FundingShareRule(Guid.NewGuid(),"FixedAmount",5,1),new FundingShareRule(Guid.NewGuid(),"Percentage",33.3333m,2),new FundingShareRule(Guid.NewGuid(),"Remainder",0,3)};
  var result=FundingShareCalculation.Calculate(15,FundingShareCalculation.Policy,rules);Assert.Equal(new[]{5m,4.99m,5.01m},result.Select(x=>x.Amount));
  Assert.Throws<ArgumentException>(()=>FundingShareCalculation.Calculate(4,FundingShareCalculation.Policy,rules));
 }
 [Fact]public void MissingPolicyDuplicateFundersAndExcessPercentagesAreRejected()
 {
  var payer=Guid.NewGuid();Assert.Throws<ArgumentException>(()=>FundingShareCalculation.Calculate(10,"",[new(payer,"Percentage",50,1),new(Guid.NewGuid(),"Remainder",0,2)]));
  Assert.Throws<ArgumentException>(()=>FundingShareCalculation.Calculate(10,FundingShareCalculation.Policy,[new(payer,"Percentage",50,1),new(payer,"Remainder",0,2)]));
  Assert.Throws<ArgumentException>(()=>FundingShareCalculation.Calculate(10,FundingShareCalculation.Policy,[new(payer,"Percentage",101,1),new(Guid.NewGuid(),"Remainder",0,2)]));
 }
}
