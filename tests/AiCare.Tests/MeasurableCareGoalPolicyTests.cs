using AiCare.Domain;
using Xunit;

namespace AiCare.Tests;

public sealed class MeasurableCareGoalPolicyTests
{
    private static readonly DateOnly Today = new(2026, 11, 1);
    [Fact]
    public void GoalDatesAndMeasurementPrecisionAreValidatedWithoutAssumingTargetDirection()
    {
        Assert.Null(MeasurableCareGoalPolicy.Definition("Walk independently", "metres", "Agreed walking practice", "Initial goal", 10, 30, Today, Today.AddDays(7), Today.AddDays(30), Today));
        Assert.Null(MeasurableCareGoalPolicy.Definition("Reduce support prompts", "prompts", "Review independence", "Initial goal", 10, 5, Today, Today.AddDays(7), Today.AddDays(30), Today));
        Assert.NotNull(MeasurableCareGoalPolicy.Definition("Goal", "metres", "Actions", "Reason", 0.12345m, 1, Today, Today, Today, Today));
        Assert.NotNull(MeasurableCareGoalPolicy.Definition("Goal", "metres", "Actions", "Reason", 0, 1, Today.AddDays(1), Today.AddDays(2), Today.AddDays(3), Today));
        Assert.NotNull(MeasurableCareGoalPolicy.Definition("Goal", "metres", "Actions", "Reason", 0, 1, Today, Today.AddDays(3), Today.AddDays(2), Today));
        Assert.False(MeasurableCareGoalPolicy.ValidValue(decimal.MinValue));
    }

    [Fact]
    public void StaffReviewMayExplicitlyRecordNoMeasurementWithoutInventingOne()
    {
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Null(MeasurableCareGoalPolicy.Progress("OnHold", null, "Person declined measurement", now, Today, now));
        Assert.Null(MeasurableCareGoalPolicy.Progress("Achieved", 20, "Staff reviewed the agreed outcome", now, Today, now));
        Assert.NotNull(MeasurableCareGoalPolicy.Progress("Achieved", null, "No evidence", now, Today, now));
        Assert.NotNull(MeasurableCareGoalPolicy.Progress("InProgress", 5, " ", now, Today, now));
        Assert.NotNull(MeasurableCareGoalPolicy.Progress("InProgress", 5, "Observed", now.AddHours(1), Today, now));
        Assert.NotNull(MeasurableCareGoalPolicy.Progress("InProgress", 5, "Observed", now.AddDays(-1), Today, now));
    }
}
