using System.Net;
using System.Text;

namespace AiCare.Domain;
public static class EmergencyPlanCopy
{
    public static readonly IReadOnlyDictionary<string, string> Sections = new Dictionary<string, string>
    {
        ["PersonalCare"] = "Personal care", ["MedicationSupport"] = "Medication support",
        ["MobilityAndTransfers"] = "Mobility and transfers", ["Nutrition"] = "Nutrition"
    };
    public static bool ValidSections(string[]? sections) => sections is not null && sections.Length <= Sections.Count
        && sections.Distinct(StringComparer.Ordinal).Count() == sections.Length && sections.All(s => s is not null && Sections.ContainsKey(s));

    public static string Render(string personName, DateOnly dateOfBirth, int planVersion, long revision,
        Guid copyId, DateTimeOffset generatedAt, DateTimeOffset reviewDueAt, IReadOnlyDictionary<string, string> selectedContent,
        string title = "Selected care-plan sections for hospital handover")
    {
        static string Encode(string text) => WebUtility.HtmlEncode(text);
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>Selected care-plan sections for hospital handover</title><style>body{font:18px/1.6 sans-serif;max-width:48rem;margin:2rem auto;padding:0 1rem;color:#111;background:#fff}section{break-inside:avoid}p{white-space:pre-wrap;overflow-wrap:anywhere}h1,h2{line-height:1.25}@media print{body{margin:0;max-width:none}}</style></head><body><main><h1>Selected care-plan sections for hospital handover</h1>");
        html.Append("<p><strong>").Append(Encode(personName)).Append("</strong><br>Date of birth: ").Append(dateOfBirth.ToString("yyyy-MM-dd"))
            .Append("<br>Care plan version ").Append(planVersion).Append(", revision ").Append(revision)
            .Append("<br>Generated (UTC): ").Append(generatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"))
            .Append("<br>Plan review due: ").Append(reviewDueAt.ToUniversalTime().ToString("yyyy-MM-dd"))
            .Append("<br>Copy reference: ").Append(copyId).Append("</p><p>This copy contains the designated sections of the plan active when generated. Check the live record for later changes and the full clinical record.</p>");
        foreach (var (key, title) in Sections)
            if (selectedContent.TryGetValue(key, out var text)) html.Append("<section><h2>").Append(Encode(title)).Append("</h2><p>").Append(Encode(text)).Append("</p></section>");
        return html.Append("</main></body></html>").ToString().Replace("Selected care-plan sections for hospital handover", Encode(title));
    }
}
