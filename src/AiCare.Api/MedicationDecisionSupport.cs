namespace AiCare.Api;

public enum MedicationDecisionSupportMode
{
    Disabled,
    LocalRules,
    ExternalProvider
}

public sealed record MedicationDecisionSupportFinding(
    string RuleOrProviderId,
    string Severity,
    string MedicationOrIngredientPair,
    string ClinicalMessage,
    string RecommendedAction,
    string Source,
    string Version,
    DateTimeOffset CheckedAt);

public sealed record MedicationDecisionSupportResult(
    MedicationDecisionSupportMode Mode,
    bool ScreeningPerformed,
    bool ComprehensiveScreeningAvailable,
    IReadOnlyList<MedicationDecisionSupportFinding> Findings,
    DateTimeOffset CheckedAt);

public sealed record MedicationDecisionSupportStatus(
    MedicationDecisionSupportMode Mode,
    bool ComprehensiveScreeningAvailable,
    string Notice);

public interface IMedicationDecisionSupport
{
    MedicationDecisionSupportStatus Status { get; }
    Task<MedicationDecisionSupportResult> ScreenAsync(
        Guid serviceUserId, Guid medicationId, CancellationToken cancellationToken);
}

public sealed class DisabledMedicationDecisionSupport : IMedicationDecisionSupport
{
    public MedicationDecisionSupportStatus Status { get; } = new(
        MedicationDecisionSupportMode.Disabled,
        ComprehensiveScreeningAvailable: false,
        "Comprehensive interaction screening is not available. Follow the provider's medication policy and clinical instructions.");

    public Task<MedicationDecisionSupportResult> ScreenAsync(
        Guid serviceUserId, Guid medicationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new MedicationDecisionSupportResult(
            MedicationDecisionSupportMode.Disabled,
            ScreeningPerformed: false,
            ComprehensiveScreeningAvailable: false,
            Findings: [],
            CheckedAt: DateTimeOffset.UtcNow));
    }
}
