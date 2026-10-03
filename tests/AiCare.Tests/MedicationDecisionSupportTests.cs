using AiCare.Api;
using Xunit;

namespace AiCare.Tests;

public sealed class MedicationDecisionSupportTests
{
    [Fact]
    public async Task DisabledModeReportsNoScreeningAndNoClinicalClearance()
    {
        IMedicationDecisionSupport support = new DisabledMedicationDecisionSupport();

        Assert.Equal(MedicationDecisionSupportMode.Disabled, support.Status.Mode);
        Assert.False(support.Status.ComprehensiveScreeningAvailable);
        Assert.Contains("Comprehensive interaction screening is not available", support.Status.Notice);

        var result = await support.ScreenAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(MedicationDecisionSupportMode.Disabled, result.Mode);
        Assert.False(result.ScreeningPerformed);
        Assert.False(result.ComprehensiveScreeningAvailable);
        Assert.Empty(result.Findings);
    }
}
