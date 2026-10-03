namespace AiCare.Api;

public static class EmarPilotGate
{
    public static bool IsEnabledForBranch(IConfiguration configuration, Guid branchId)
    {
        if (!configuration.GetValue<bool>("MedicationSafety:EmarProductionEnabled")) return false;
        var configured = configuration["MedicationSafety:PilotBranchId"];
        return string.IsNullOrWhiteSpace(configured) ||
               Guid.TryParse(configured, out var pilotBranchId) && pilotBranchId == branchId;
    }
}
