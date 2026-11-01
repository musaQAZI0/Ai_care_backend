using AiCare.Domain;
using Xunit;

namespace AiCare.Tests;

public sealed class FinanceRateSelectionTests
{
 [Fact]public void DimensionsAndHalfOpenDurationBandsAreExplicitAndDeterministic()
 {
  var start=new DateTimeOffset(2049,4,7,9,0,0,TimeSpan.Zero);var basic=new BillingRate(Guid.NewGuid(),Guid.NewGuid(),"Standard","Any","Hour",10,0,1,null,null);var specific=basic with{RuleId=Guid.NewGuid(),UnitRate=20,StaffLevel="SENIOR",LocationCode="ZONE-1",MinimumMinutes=30,MaximumMinutes=60};
  Assert.Throws<InvalidOperationException>(()=>FinanceRateSelection.Select([basic,specific],start,45,false));Assert.Equal(specific,FinanceRateSelection.Select([basic,specific],start,45,false,"SENIOR","ZONE-1"));Assert.Equal(basic,FinanceRateSelection.Select([basic,specific],start,60,false,"SENIOR","ZONE-1"));Assert.Throws<InvalidOperationException>(()=>FinanceRateSelection.Select([specific,specific with{RuleId=Guid.NewGuid()}],start,45,false,"SENIOR","ZONE-1"));
 }
    private static BillingRate Rate(string day="Any",int priority=0,TimeOnly? from=null,TimeOnly? to=null,string unit="Hour",string type="Standard",decimal price=20m)
        =>new(Guid.NewGuid(),Guid.NewGuid(),type,day,unit,price,0,priority,from,to);
    private static readonly DateTimeOffset Monday=new(2042,4,7,9,0,0,TimeSpan.Zero);

    [Fact] public void ExactDayThenPrioritySelectsOneRuleWithoutDependingOnInputOrder()
    {
        var exact=Rate("Weekday",1); var any=Rate("Any",100);
        Assert.Equal(exact,FinanceRateSelection.Select([any,exact],Monday,45,false));
        Assert.Equal(exact,FinanceRateSelection.Select([exact,any],Monday,45,false));
    }
    [Fact] public void EqualWinnersAreRejectedRatherThanBrokenByRowOrder()
        =>Assert.Contains("AMBIGUOUS_RATE",Assert.Throws<InvalidOperationException>(()=>FinanceRateSelection.Select([Rate(),Rate()],Monday,45,false)).Message);
    [Fact] public void BankHolidayDoesNotSilentlyUseAnOrdinaryWeekdayRate()
        =>Assert.Contains("MISSING_RATE",Assert.Throws<InvalidOperationException>(()=>FinanceRateSelection.Select([Rate("Weekday")],Monday,45,true)).Message);
    [Fact] public void UnsupportedMileageAndCancellationRulesAreNotTreatedAsHourlyCare()
    {
        Assert.Contains("UNSUPPORTED_RATE",Assert.Throws<InvalidOperationException>(()=>FinanceRateSelection.Select([Rate(unit:"Mile",type:"Mileage")],Monday,45,false)).Message);
        Assert.Contains("UNSUPPORTED_RATE",Assert.Throws<InvalidOperationException>(()=>FinanceRateSelection.Select([Rate(type:"Cancellation")],Monday,45,false)).Message);
    }
    [Fact] public void CrossingAConfiguredTimeWindowCannotFallBackToAnAllDayRate()
        =>Assert.Contains("SPLIT_TIME_REVIEW",Assert.Throws<InvalidOperationException>(()=>FinanceRateSelection.Select([Rate(),Rate(from:new(10,0),to:new(11,0))],Monday,120,false)).Message);
    [Theory]
    [InlineData(22, 0, 480)]
    [InlineData(23, 0, 120)]
    [InlineData(2, 0, 60)]
    public void OvernightAnyDayWindowCoversBothSidesOfMidnight(int hour,int minute,int duration)
    {
        var night=Rate(from:new(22,0),to:new(6,0),type:"Night");
        Assert.Equal(night,FinanceRateSelection.Select([night],new(2042,1,6,hour,minute,0,TimeSpan.Zero),duration,false));
    }
    [Fact] public void AServiceEndingAtMidnightDoesNotConsumeTheNextDaysRate()
    {
        var weekday=Rate("Weekday");
        Assert.Equal(weekday,FinanceRateSelection.Select([weekday],new(2026,10,30,23,0,0,TimeSpan.Zero),60,false));
    }
    [Fact] public void OvernightBoundaryAndAmbiguousDayTransitionsRequireReview()
    {
        var night=Rate(from:new(22,0),to:new(6,0),type:"Night");
        Assert.Contains("SPLIT_TIME_REVIEW",Assert.Throws<InvalidOperationException>(()=>FinanceRateSelection.Select([Rate(),night],new(2042,1,6,21,30,0,TimeSpan.Zero),60,false)).Message);
        Assert.Contains("SPLIT_TIME_REVIEW",Assert.Throws<InvalidOperationException>(()=>FinanceRateSelection.Select([night with{DayType="Weekday"}],new(2026,10,30,23,0,0,TimeSpan.Zero),120,false)).Message);
        Assert.Contains("SPLIT_TIME_REVIEW",Assert.Throws<InvalidOperationException>(()=>FinanceRateSelection.Select([night],new(2026,10,25,0,30,0,TimeSpan.Zero),120,false)).Message);
    }
    [Fact] public void ActualMinutesAreNotRoundedToHundredthsOfAnHourBeforePricing()
    {
        var result=FinanceRateSelection.Calculate(Rate(price:60m),1m,"AwayFromZero");
        Assert.Equal(1m,result.Amount); Assert.Equal(0.01666667m,result.Quantity);
        Assert.Equal(15m,FinanceRateSelection.Calculate(Rate(),45m,"AwayFromZero").Amount);
        Assert.Equal(20m,FinanceRateSelection.Calculate(Rate(),60m,"AwayFromZero").Amount);
    }
    [Fact] public void MonetaryMidpointsUseTheExplicitContractPolicy()
    {
        Assert.Equal(0.01m,FinanceRateSelection.Calculate(Rate(price:0.01m),30,"AwayFromZero").Amount);
        Assert.Equal(0m,FinanceRateSelection.Calculate(Rate(price:0.01m),30,"ToEven").Amount);
        Assert.Throws<InvalidOperationException>(()=>FinanceRateSelection.Calculate(Rate(),30,""));
    }
    [Theory]
    [InlineData(2026,3,29,23)] [InlineData(2026,10,25,25)]
    public void UkServiceCalendarUsesHalfOpenRangesAcrossClockChanges(int year,int month,int day,int hours)
    {
        var date=new DateOnly(year,month,day);var period=FinanceServicePeriod.FromInclusiveDates(date,date);
        Assert.Equal(hours,(period.End-period.Start).TotalHours);
    }
}
