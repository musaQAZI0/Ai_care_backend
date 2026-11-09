using AiCare.Api;
using Xunit;

namespace AiCare.Tests;

public sealed class EmarAdministrationSafetyTests
{
    private static readonly MedicationAdministrationSafety Standard = new("Standard","tablet",1m,1m,"Follow approved instruction");
    [Fact] public void FixedQuantityRangeNeedsAnApprovedVariableProtocol()
    {
        Assert.Null(MedicationAdministrationSafetyValidation.Validate(Standard));
        Assert.NotNull(MedicationAdministrationSafetyValidation.Validate(Standard with {MaximumStockQuantity=2}));
        Assert.NotNull(MedicationAdministrationSafetyValidation.Validate(Standard with {MaximumStockQuantity=2,VariableDose=true}));
        Assert.Null(MedicationAdministrationSafetyValidation.Validate(Standard with {MaximumStockQuantity=2,VariableDose=true,VariableDoseProtocol="Approved protocol V1"}));
    }
    [Fact] public void LiquidRequiresConcentrationAndDoesNotInferConversions()
    {
        var p=Standard with {Kind="Liquid",StockUnit="mL"};
        Assert.NotNull(MedicationAdministrationSafetyValidation.Validate(p));
        Assert.Null(MedicationAdministrationSafetyValidation.Validate(p with {ConcentrationAmount=10,ConcentrationUnit="mg",ConcentrationVolume=5}));
    }
    [Theory][InlineData("Patch")][InlineData("Topical")][InlineData("EyeDrops")][InlineData("Injection")]
    public void SiteSpecificFormsRequireApplicationInstructions(string kind)
        =>Assert.NotNull(MedicationAdministrationSafetyValidation.Validate(Standard with {Kind=kind}));
    [Fact] public void PatchRequiresRemovalAndInhalerRequiresTechnique()
    {
        Assert.NotNull(MedicationAdministrationSafetyValidation.Validate(Standard with {Kind="Patch",SiteInstructions="Approved application sites"}));
        Assert.Null(MedicationAdministrationSafetyValidation.Validate(Standard with {Kind="Patch",SiteInstructions="Approved application sites",PatchRemovalRequired=true}));
        Assert.NotNull(MedicationAdministrationSafetyValidation.Validate(Standard with {Kind="Inhaler"}));
        Assert.Null(MedicationAdministrationSafetyValidation.Validate(Standard with {Kind="Inhaler",TechniqueInstructions="Approved spacer technique"}));
    }
    [Fact] public void CovertAndControlledDrugInstructionsRequireExplicitEvidence()
    {
        Assert.NotNull(MedicationAdministrationSafetyValidation.Validate(Standard with {Covert=true}));
        Assert.Null(MedicationAdministrationSafetyValidation.Validate(Standard with {Covert=true,CovertAuthorisation="CA-1",CapacityAssessmentReference="CAP-1",CovertReviewDueAt=DateTimeOffset.UtcNow.AddDays(1)}));
        Assert.NotNull(MedicationAdministrationSafetyValidation.Validate(Standard with {ControlledDrug=true}));
        Assert.Null(MedicationAdministrationSafetyValidation.Validate(Standard with {ControlledDrug=true,ControlledDrugPolicyReference="CD-1"}));
    }
    [Fact] public void AdministrationRequiresActualSitePatchAndVariableDoseEvidence()
    {
        var evidence=new EmarAdministrationObservations("Left upper arm",true,true,"Approved direction for this dose",true);
        Assert.NotNull(MedicationAdministrationSafetyValidation.ValidateObservations(Standard with {Kind="Patch"},null));
        Assert.Null(MedicationAdministrationSafetyValidation.ValidateObservations(Standard with {Kind="Patch"},evidence));
        Assert.NotNull(MedicationAdministrationSafetyValidation.ValidateObservations(Standard with {Kind="Patch"},evidence with {PreviousPatchRemoved=false}));
        Assert.NotNull(MedicationAdministrationSafetyValidation.ValidateObservations(Standard with {VariableDose=true},evidence with {QuantitySelectionEvidence=""}));
        Assert.NotNull(MedicationAdministrationSafetyValidation.ValidateObservations(Standard with {Covert=true},evidence with {CovertInstructionsFollowed=false}));
    }
    [Fact] public void ReadBackRejectsChangedPersonOrderAndMissingDeclineEvidence()
    {
        var person=Guid.NewGuid();var medication=Guid.NewGuid();var scheduled=DateTimeOffset.UtcNow;
        var evidence=new EmarReadBackEvidence(person,medication,4,"Prescribed dose","Oral",scheduled,"Approved indication",true,true);
        string? Check(EmarReadBackEvidence e)=>EmarReadBackValidation.Validate(e,person,medication,4,"Prescribed dose","Oral",scheduled,"Approved indication");
        Assert.Null(Check(evidence));
        Assert.NotNull(Check(evidence with {PersonId=Guid.NewGuid()}));
        Assert.NotNull(Check(evidence with {MedicationId=Guid.NewGuid()}));
        Assert.NotNull(Check(evidence with {ProfileVersion=3}));
        Assert.NotNull(Check(evidence with {Route="Eye"}));
        Assert.NotNull(Check(evidence with {RightToDeclineExplained=false}));
    }
}
