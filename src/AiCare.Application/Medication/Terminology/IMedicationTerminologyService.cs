namespace AiCare.Application;

public interface IMedicationTerminologyService
{
    Task<IReadOnlyList<MedicationTerminologyResult>> SearchAsync(string query, int count = 20, CancellationToken cancellationToken = default);
    Task<MedicationTerminologyConcept?> LookupAsync(string code, CancellationToken cancellationToken = default);
}

public sealed record MedicationTerminologyResult(string Code, string Display, string System, bool Inactive);

public sealed record MedicationTerminologyConcept(
    string Code,
    string Display,
    string System,
    bool Inactive,
    IReadOnlyDictionary<string, string> Properties);
