using AiCare.Api;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiCare.Tests;

public sealed class EmarPilotGateTests
{
    [Fact]
    public void GateOnlyEnablesApprovedPilotBranch()
    {
        var approved = Guid.NewGuid();
        var other = Guid.NewGuid();
        var settings = new Dictionary<string, string?>
        {
            ["MedicationSafety:EmarProductionEnabled"] = "true",
            ["MedicationSafety:PilotBranchId"] = approved.ToString()
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        Assert.True(EmarPilotGate.IsEnabledForBranch(configuration, approved));
        Assert.False(EmarPilotGate.IsEnabledForBranch(configuration, other));

        settings["MedicationSafety:EmarProductionEnabled"] = "false";
        var disabled = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Assert.False(EmarPilotGate.IsEnabledForBranch(disabled, approved));
    }
}
