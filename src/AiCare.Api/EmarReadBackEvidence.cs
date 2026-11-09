namespace AiCare.Api;

public sealed record EmarReadBackEvidence(Guid PersonId,Guid MedicationId,int ProfileVersion,string DoseInstructions,
    string Route,DateTimeOffset ScheduledAt,string Indication,bool AllergyChecked,bool RightToDeclineExplained);

public static class EmarReadBackValidation
{
    public static string? Validate(EmarReadBackEvidence evidence,Guid person,Guid medication,int profileVersion,
        string dosage,string route,DateTimeOffset scheduled,string indication)
    {
        if(evidence.PersonId!=person) return "The confirmed person does not match this MAR.";
        if(evidence.MedicationId!=medication) return "The confirmed medicine does not match this MAR.";
        if(evidence.ProfileVersion!=profileVersion) return "Medication instructions changed after read-back. Reload and check the current instruction.";
        if(evidence.DoseInstructions!=dosage || evidence.Route!=route || evidence.ScheduledAt!=scheduled || evidence.Indication!=indication)
            return "The confirmed dose, route, time or indication does not match the current instruction. Reload before recording.";
        if(!evidence.AllergyChecked || !evidence.RightToDeclineExplained) return "Confirm the allergy check and the person's right to decline.";
        return null;
    }
}
