using AiCare.Domain;
using Xunit;

namespace AiCare.Tests;

public sealed class AssessmentTemplateValidationTests
{
    private static readonly AssessmentField[] Fields = [
        new("needs_help", "Needs help", "YesNo", true),
        new("details", "Help details", "Text", true, VisibleWhen: new("needs_help", "true")),
        new("weight", "Weight", "Number", Minimum: 1, Maximum: 500),
        new("review_date", "Review date", "Date"),
        new("support", "Support", "Choice", Options: ["Independent", "Assisted"]),
        new("preferences", "Preferences", "MultiChoice", Options: ["Morning", "Evening"])
    ];

    [Fact]
    public void RequiredConditionalAnswersAndFalseValuesAreHandledWithoutInventedAnswers()
    {
        Assert.Null(AssessmentTemplateValidation.Definition(Fields));
        Assert.Null(AssessmentTemplateValidation.Answers(Fields, "{\"needs_help\":false}", true));
        Assert.Contains("required", AssessmentTemplateValidation.Answers(Fields, "{\"needs_help\":true}", true)!);
        Assert.Contains("hidden", AssessmentTemplateValidation.Answers(Fields, "{\"needs_help\":false,\"details\":\"Hidden answer\"}", true)!);
        Assert.Null(AssessmentTemplateValidation.Answers(Fields, "{\"needs_help\":true,\"details\":\"Transfer support\",\"weight\":75.5,\"review_date\":\"2027-02-28\",\"support\":\"Assisted\",\"preferences\":[\"Morning\"]}", true));
    }

    [Theory]
    [InlineData("{\"needs_help\":\"false\"}")]
    [InlineData("{\"needs_help\":false,\"weight\":0}")]
    [InlineData("{\"needs_help\":false,\"review_date\":\"2027-02-30\"}")]
    [InlineData("{\"needs_help\":false,\"support\":\"Unknown\"}")]
    [InlineData("{\"needs_help\":false,\"preferences\":[\"Morning\",\"Morning\"]}")]
    [InlineData("{\"needs_help\":false,\"unknown\":\"value\"}")]
    [InlineData("{\"needs_help\":false,\"needs_help\":true}")]
    [InlineData("[]")]
    public void InvalidTypesRangesChoicesDatesAndUnknownOrDuplicateKeysAreRejected(string json)
        => Assert.NotNull(AssessmentTemplateValidation.Answers(Fields, json, true));

    [Fact]
    public void DefinitionRejectsDuplicateIdsForwardConditionsAndInvalidOptions()
    {
        Assert.NotNull(AssessmentTemplateValidation.Definition([Fields[0], Fields[0]]));
        Assert.NotNull(AssessmentTemplateValidation.Definition([Fields[1], Fields[0]]));
        Assert.NotNull(AssessmentTemplateValidation.Definition([new("choice", "Choice", "Choice", Options: ["Same", "Same"])]));
        Assert.NotNull(AssessmentTemplateValidation.Definition([new("number", "Number", "Number", Minimum: 10, Maximum: 1)]));
    }

    [Fact]
    public void HiddenAncestorsCannotTriggerNestedQuestions()
    {
        AssessmentField[] fields = [Fields[0], Fields[1], new("followup", "Follow-up", "Text", true, VisibleWhen: new("details", "Yes"))];
        Assert.Null(AssessmentTemplateValidation.Answers(fields, "{\"needs_help\":false}", true));
        Assert.NotNull(AssessmentTemplateValidation.Answers(fields, "{\"needs_help\":false,\"followup\":\"Unexpected\"}", true));
    }
}
