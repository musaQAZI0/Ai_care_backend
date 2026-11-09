namespace AiCare.Api;

public sealed record MedicationAdministrationSafety(string Kind,string StockUnit,decimal MinimumStockQuantity,decimal MaximumStockQuantity,
    string HandlingInstructions,decimal? ConcentrationAmount=null,string? ConcentrationUnit=null,decimal? ConcentrationVolume=null,
    string? SiteInstructions=null,bool PatchRemovalRequired=false,string? TechniqueInstructions=null,string? InjectionAuthorisation=null,
    bool VariableDose=false,string? VariableDoseProtocol=null,bool Covert=false,string? CovertAuthorisation=null,
    string? CapacityAssessmentReference=null,DateTimeOffset? CovertReviewDueAt=null,bool ControlledDrug=false,string? ControlledDrugPolicyReference=null);

public sealed record EmarAdministrationObservations(string ApplicationSite,bool PreviousPatchRemoved,bool TechniqueConfirmed,
    string QuantitySelectionEvidence,bool CovertInstructionsFollowed);

public static class MedicationAdministrationSafetyValidation
{
    public static string? ValidateObservations(MedicationAdministrationSafety p,EmarAdministrationObservations? evidence)
    {
        if(p.Kind is "Patch" or "Topical" or "EyeDrops" or "Injection" && string.IsNullOrWhiteSpace(evidence?.ApplicationSite))
            return "Record the actual application or administration site.";
        if(p.Kind=="Patch" && evidence?.PreviousPatchRemoved!=true)return "Confirm that the previous patch was checked and removed.";
        if(p.Kind is "Inhaler" or "EyeDrops" or "Injection" && evidence?.TechniqueConfirmed!=true)return "Confirm that the approved administration technique was followed.";
        if(p.VariableDose && string.IsNullOrWhiteSpace(evidence?.QuantitySelectionEvidence))return "Record the instruction or authorised direction used to select this variable quantity.";
        if(p.Covert && evidence?.CovertInstructionsFollowed!=true)return "Confirm that the approved covert-administration instructions were followed.";
        if(evidence?.ApplicationSite?.Length>1000||evidence?.QuantitySelectionEvidence?.Length>4000)return "Administration observations exceed the supported length.";
        return null;
    }
    public static string? Validate(MedicationAdministrationSafety p)
    {
        if(p.Kind is not ("Standard" or "Liquid" or "Patch" or "Topical" or "Inhaler" or "EyeDrops" or "Injection")) return "Select a supported medication form safety category.";
        if(string.IsNullOrWhiteSpace(p.StockUnit) || p.StockUnit.Length>80 || string.IsNullOrWhiteSpace(p.HandlingInstructions) || p.HandlingInstructions.Length>4000)
            return "Stock unit and handling instructions (maximum 4000 characters) are required.";
        if(p.MinimumStockQuantity<=0 || p.MaximumStockQuantity<p.MinimumStockQuantity || p.MaximumStockQuantity>9999999999.99m ||
           decimal.Round(p.MinimumStockQuantity,2)!=p.MinimumStockQuantity || decimal.Round(p.MaximumStockQuantity,2)!=p.MaximumStockQuantity)
            return "Enter the approved minimum/maximum stock quantities with at most two decimal places.";
        if(!p.VariableDose && p.MinimumStockQuantity!=p.MaximumStockQuantity) return "A quantity range requires an approved variable-dose protocol.";
        if(p.Kind=="Liquid" && (p.ConcentrationAmount is null or <=0 || p.ConcentrationVolume is null or <=0 || string.IsNullOrWhiteSpace(p.ConcentrationUnit)))
            return "Liquid instructions require a positive concentration amount, unit and volume. No conversion is inferred.";
        if(p.Kind is "Patch" or "Topical" or "EyeDrops" or "Injection" && string.IsNullOrWhiteSpace(p.SiteInstructions)) return "Site or application instructions are required for this medication form.";
        if(p.Kind=="Patch" && !p.PatchRemovalRequired) return "Patch instructions must explicitly require checking and removing the previous patch.";
        if(p.Kind is "Inhaler" or "EyeDrops" or "Injection" && string.IsNullOrWhiteSpace(p.TechniqueInstructions)) return "Explicit administration technique instructions are required.";
        if(p.Kind=="Injection" && string.IsNullOrWhiteSpace(p.InjectionAuthorisation)) return "Injection administration requires an authorisation reference.";
        if(p.VariableDose && string.IsNullOrWhiteSpace(p.VariableDoseProtocol)) return "Variable doses require an approved protocol reference.";
        if(p.Covert && (string.IsNullOrWhiteSpace(p.CovertAuthorisation) || string.IsNullOrWhiteSpace(p.CapacityAssessmentReference) || p.CovertReviewDueAt is null))
            return "Covert administration requires authorisation, capacity-assessment evidence and a review deadline.";
        if(p.ControlledDrug && string.IsNullOrWhiteSpace(p.ControlledDrugPolicyReference)) return "Controlled drugs require the provider's approved policy reference.";
        var texts=new[]{p.ConcentrationUnit,p.SiteInstructions,p.TechniqueInstructions,p.InjectionAuthorisation,p.VariableDoseProtocol,p.CovertAuthorisation,p.CapacityAssessmentReference,p.ControlledDrugPolicyReference};
        if(texts.Any(x=>x?.Length>4000))return "Medication safety text fields must not exceed 4000 characters.";
        return null;
    }
}
