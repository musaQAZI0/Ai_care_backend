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

        settings.Remove("MedicationSafety:PilotBranchId");
        var missingBranch = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Assert.False(EmarPilotGate.IsEnabledForBranch(missingBranch, approved));

        settings["MedicationSafety:PilotBranchId"] = "not-a-guid";
        var invalidBranch = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Assert.False(EmarPilotGate.IsEnabledForBranch(invalidBranch, approved));

        settings["MedicationSafety:PilotBranchId"] = Guid.Empty.ToString();
        var emptyBranch = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Assert.False(EmarPilotGate.IsEnabledForBranch(emptyBranch, Guid.Empty));

        settings["MedicationSafety:PilotBranchId"] = approved.ToString();
        settings["MedicationSafety:EmarProductionEnabled"] = "false";
        var disabled = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Assert.False(EmarPilotGate.IsEnabledForBranch(disabled, approved));
    }
}
