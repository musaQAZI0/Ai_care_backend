using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiCare.Domain;

public sealed record AssessmentCondition(string FieldId, string EqualsValue);
public sealed record AssessmentField(string Id, string Label, string Type, bool Required = false,
    string Help = "", string[]? Options = null, AssessmentCondition? VisibleWhen = null,
    decimal? Minimum = null, decimal? Maximum = null);

public static class AssessmentTemplateValidation
{
    public static string? Definition(IReadOnlyList<AssessmentField>? fields)
    {
        if (fields is null || fields.Count is < 1 or > 100) return "Provide between 1 and 100 questions.";
        var preceding = new Dictionary<string, AssessmentField>();
        foreach (var f in fields)
        {
            if (f is null || string.IsNullOrWhiteSpace(f.Id) || !Regex.IsMatch(f.Id, "^[a-z][a-z0-9_]{0,63}$") ||
                string.IsNullOrWhiteSpace(f.Label) || f.Label.Length > 300 || f.Help is null || f.Help.Length > 2000 || preceding.ContainsKey(f.Id))
                return "Questions need unique stable IDs, labels and bounded help text.";
            if (f.Type is not ("Text" or "Number" or "Date" or "YesNo" or "Choice" or "MultiChoice")) return "Unsupported question type.";
            if (f.Type is "Choice" or "MultiChoice")
            {
                if (f.Options is null || f.Options.Length is < 2 or > 50 || f.Options.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200) ||
                    f.Options.Distinct(StringComparer.Ordinal).Count() != f.Options.Length) return "Choice questions need distinct non-empty options.";
            }
            else if (f.Options is { Length: > 0 }) return "Options apply only to choice questions.";
            if ((f.Minimum.HasValue || f.Maximum.HasValue) && f.Type != "Number" || f.Minimum > f.Maximum) return "Numeric limits must be ordered and apply only to number questions.";
            if (f.VisibleWhen is { } condition)
            {
                if (string.IsNullOrWhiteSpace(condition.FieldId) || !preceding.TryGetValue(condition.FieldId, out var source) || source.Type is not ("Text" or "Choice" or "YesNo") ||
                    string.IsNullOrWhiteSpace(condition.EqualsValue) ||
                    source.Type == "Choice" && !source.Options!.Contains(condition.EqualsValue) ||
                    source.Type == "YesNo" && condition.EqualsValue is not ("true" or "false"))
                    return "Conditions must match an earlier text, choice or yes/no question and a valid answer.";
            }
            preceding.Add(f.Id, f);
        }
        return null;
    }

    public static string? Answers(IReadOnlyList<AssessmentField> fields, string json, bool requireComplete)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); } catch (Exception e) when (e is JsonException or ArgumentNullException) { return "Answers must be a JSON object."; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return "Answers must be a JSON object.";
            var answers = new Dictionary<string, JsonElement>();
            foreach (var property in document.RootElement.EnumerateObject())
                if (!answers.TryAdd(property.Name, property.Value)) return "Duplicate answer IDs are not allowed.";
            if (answers.Keys.Any(key => !fields.Any(f => f.Id == key))) return "An answer refers to an unknown question.";
            var visible = new HashSet<string>();
            foreach (var field in fields)
            {
                var show = field.VisibleWhen is not { } condition || visible.Contains(condition.FieldId) &&
                    answers.TryGetValue(condition.FieldId, out var trigger) && Scalar(trigger) == condition.EqualsValue;
                var supplied = answers.TryGetValue(field.Id, out var value) && value.ValueKind != JsonValueKind.Null &&
                    !(value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())) &&
                    !(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0);
                if (!show) { if (supplied) return $"{field.Label}: hidden questions cannot contain answers."; continue; }
                visible.Add(field.Id);
                if (!supplied) { if (requireComplete && field.Required) return $"{field.Label}: an answer is required."; continue; }
                var valid = field.Type switch
                {
                    "Text" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 10000,
                    "Number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) &&
                        (!field.Minimum.HasValue || number >= field.Minimum) && (!field.Maximum.HasValue || number <= field.Maximum),
                    "Date" => value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    "YesNo" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "Choice" => value.ValueKind == JsonValueKind.String && field.Options!.Contains(value.GetString()),
                    "MultiChoice" => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && field.Options!.Contains(x.GetString())) &&
                        value.EnumerateArray().Select(x => x.GetString()).Distinct().Count() == value.GetArrayLength(),
                    _ => false
                };
                if (!valid) return $"{field.Label}: answer type, choice or range is invalid.";
            }
            return null;
        }
    }

    private static string? Scalar(JsonElement x) => x.ValueKind switch { JsonValueKind.String => x.GetString(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null };
}
