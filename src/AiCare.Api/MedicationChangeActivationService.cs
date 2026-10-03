using System.Data;
using System.Data.Common;
using System.Text.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;

public sealed class MedicationChangeActivationService(CareDbContext db)
{
    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    internal async Task<string?> Apply(MedicationChangeRow change, Guid organization, Guid branch, Guid? actorId, string actor, CancellationToken token)
    {
        if (change.Status != "Approved" || change.EffectiveAt > DateTimeOffset.UtcNow)
            return "Only an approved change at or after its effective time can be activated.";
        var profile = await ProfileState(change.MedicationId, token);
        if (profile is null || profile.Value.Status != "Verified" || profile.Value.Version != change.BaselineProfileVersion)
            return "Active profile version changed. The request must be reconciled again.";
        if (change.ChangeType == "Stop")
        {
            await Execute("""
                update "Medications" set "Schedule"='Discontinued' where "Id"=@medication and "OrganizationId"=@organization and "BranchId"=@branch;
                update medication_safety_profiles set reconciliation_status='Discontinued',end_date=now(),profile_version=profile_version+1,updated_at=now()
                where medication_id=@medication and organization_id=@organization and branch_id=@branch
                """, c=>Add(c,"medication",change.MedicationId), token);
        }
        else
        {
            var proposal=JsonSerializer.Deserialize<MedicationChangeProposal>(change.ProposalJson);
            if(proposal is null)return "Proposed regimen could not be read.";
            await Execute("""
                update "Medications" set "Dosage"=@dosage,"Route"=@route,"Schedule"=@schedule,"IsPrn"=@isPrn
                  where "Id"=@medication and "OrganizationId"=@organization and "BranchId"=@branch;
                update medication_safety_profiles set indication=@indication,prescriber=@prescriber,form=@form,strength=@strength,
                  dose_unit=@unit,frequency=@frequency,administration_instructions=@instructions,start_date=@start,end_date=@end,
                  dose_window_minutes=@window,max_prn_doses_24h=@maxPrn,min_prn_interval_minutes=@minInterval,
                  prn_indication=@prnIndication,prn_effect_review_minutes=@effectReview,requires_witness=@witness,
                  review_due_at=@reviewDue,last_reconciled_at=now(),reconciled_by=@reviewerName,
                  reconciliation_status='Verified',source_type=@sourceType,source_reference=@sourceReference,
                  reviewed_by_user_id=@reviewerId,reviewed_at=now(),change_reason=@reason,
                  profile_version=profile_version+1,updated_at=now()
                  where medication_id=@medication and organization_id=@organization and branch_id=@branch
                """, c => {
                    Add(c,"medication",change.MedicationId);Add(c,"dosage",proposal.Dosage.Trim());Add(c,"route",proposal.Route.Trim());
                    Add(c,"schedule",proposal.Schedule.Trim());Add(c,"isPrn",proposal.IsPrn);Add(c,"indication",proposal.Indication.Trim());
                    Add(c,"prescriber",proposal.Prescriber.Trim());Add(c,"form",proposal.Form.Trim());Add(c,"strength",proposal.Strength.Trim());
                    Add(c,"unit",proposal.DoseUnit.Trim());Add(c,"frequency",proposal.Frequency.Trim());Add(c,"instructions",proposal.AdministrationInstructions.Trim());
                    Add(c,"start",proposal.StartDate);Add(c,"end",proposal.EndDate);Add(c,"window",proposal.DoseWindowMinutes);
                    Add(c,"maxPrn",proposal.MaxPrnDoses24h);Add(c,"minInterval",proposal.MinPrnIntervalMinutes);
                    Add(c,"prnIndication",proposal.PrnIndication.Trim());Add(c,"effectReview",proposal.PrnEffectReviewMinutes);
                    Add(c,"witness",proposal.RequiresWitness);Add(c,"reviewDue",proposal.ReviewDueAt);
                    Add(c,"sourceType",change.SourceType);Add(c,"sourceReference",change.SourceReference);Add(c,"reason",change.Reason);
                }, token);
        }
        await Execute("""
            update medication_change_requests set status='Applied',applied_by_user_id=@actorId,applied_by=@actor,applied_at=now(),updated_at=now()
            where id=@change and medication_id=@medication and organization_id=@organization and branch_id=@branch
            """, c=>{Add(c,"change",change.Id);Add(c,"medication",change.MedicationId);},token);
        await Event(change.Id,change.MedicationId,"Applied","Approved medication instruction activated.",token);
        Audit("medication.change_applied",change.MedicationId);
        return null;

        async Task<(int Version, string Status)?> ProfileState(Guid medication, CancellationToken t)
        {
            await using var command = await Command("select profile_version,reconciliation_status from medication_safety_profiles where medication_id=@medication and organization_id=@organization and branch_id=@branch for update", t);
            Add(command, "medication", medication);
            await using var reader = await command.ExecuteReaderAsync(t);
            return await reader.ReadAsync(t) ? (reader.GetInt32(0), reader.GetString(1)) : null;
        }
        async Task<DbCommand> Command(string sql, CancellationToken t)
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync(t);
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            Add(command, "organization", organization);
            Add(command, "branch", branch);
            Add(command, "actorId", actorId);
            Add(command, "actor", actor);
            Add(command, "reviewerId", change.ReviewedByUserId ?? actorId);
            Add(command, "reviewerName", string.IsNullOrWhiteSpace(change.ReviewedBy) ? actor : change.ReviewedBy);
            return command;
        }
        async Task<int> Execute(string sql, Action<DbCommand> bind, CancellationToken t)
        {
            await using var command = await Command(sql, t);
            bind(command);
            return await command.ExecuteNonQueryAsync(t);
        }
        Task<int> Event(Guid id, Guid medication, string action, string detail, CancellationToken t) =>
            Execute("insert into medication_change_events(id,change_id,medication_id,organization_id,branch_id,action,detail,actor_user_id,actor) values(@id,@change,@medication,@organization,@branch,@action,@detail,@eventActorId,@actor)",
                command => { Add(command,"id",Guid.NewGuid());Add(command,"change",id);Add(command,"medication",medication);Add(command,"action",action);Add(command,"detail",detail);Add(command,"eventActorId",actorId ?? Guid.Empty); },t);
        void Audit(string action, Guid medication) =>
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,actor,nameof(Medication),medication,DateTimeOffset.UtcNow,organization,branch));
    }

}
