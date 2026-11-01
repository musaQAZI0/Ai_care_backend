using AiCare.Domain;
using Xunit;

namespace AiCare.Tests;
public sealed class EmergencyPlanCopyTests
{
    [Fact]
    public void SelectionRejectsUnknownDuplicateAndNullKeysAndAllowsExplicitClearing()
    {
        Assert.True(EmergencyPlanCopy.ValidSections([]));
        Assert.True(EmergencyPlanCopy.ValidSections(["PersonalCare", "Nutrition"]));
        Assert.False(EmergencyPlanCopy.ValidSections(null));
        Assert.False(EmergencyPlanCopy.ValidSections(["PersonalCare", "PersonalCare"]));
        Assert.False(EmergencyPlanCopy.ValidSections(["private-notes"]));
        Assert.False(EmergencyPlanCopy.ValidSections([null!]));
    }

    [Fact]
    public void PrintableCopyEscapesClinicalTextAndIncludesOnlyDesignatedContentWithProvenance()
    {
        var id = Guid.NewGuid();
        var html = EmergencyPlanCopy.Render("Name <script>alert(1)</script>", new DateOnly(1950, 1, 2), 3, 9, id,
            DateTimeOffset.Parse("2026-11-01T10:00:00Z"), DateTimeOffset.Parse("2026-12-01T10:00:00Z"),
            new Dictionary<string, string> { ["PersonalCare"] = "Offer <img src=x onerror=alert(1)> assistance & choices" });
        Assert.Contains("lang=\"en\"", html); Assert.Contains("<h1>", html); Assert.Contains("<h2>Personal care</h2>", html);
        Assert.Contains("Care plan version 3, revision 9", html); Assert.Contains(id.ToString(), html);
        Assert.Contains("&lt;script&gt;", html); Assert.Contains("&lt;img", html); Assert.Contains("&amp; choices", html);
        Assert.DoesNotContain("<script>", html); Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<h2>Medication support</h2>", html);
        Assert.Contains("default-src 'none'", html);
    }
}
