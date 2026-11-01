using System.Data;
using System.Data.Common;
using AiCare.Application.CarePlans;
using AiCare.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace AiCare.Infrastructure;

public sealed class CarePlanLifecycleStore(CareDbContext context) : ICarePlanLifecycleStore
{
    public async Task<CarePlanLifecycleSnapshot?> GetAsync(Guid carePlanId, Guid organizationId, Guid? branchId, CancellationToken cancellationToken)
    {
        var plan = await context.CarePlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == carePlanId && x.OrganizationId == organizationId && (branchId == null || x.BranchId == branchId), cancellationToken);
        if (plan is null) return null;

        var version = await ReadVersionAsync(carePlanId, organizationId, branchId, cancellationToken);
        if (version is null) return null;

        var signatures = await ReadSignaturesAsync(carePlanId, organizationId, branchId, cancellationToken);
        var acknowledgements = await ReadAcknowledgementsAsync(carePlanId, organizationId, branchId, cancellationToken);
        var events = await ReadEventsAsync(carePlanId, organizationId, branchId, cancellationToken);
        return new CarePlanLifecycleSnapshot(plan, version, signatures, acknowledgements, events, RequiredSignaturesSatisfied(signatures));
    }

    public async Task<IReadOnlyList<CarePlanVersionRecord>> GetVersionsAsync(Guid serviceUserId, Guid organizationId, Guid? branchId, CancellationToken cancellationToken)
    {
        var result = new List<CarePlanVersionRecord>();
        var connection = context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                select id,care_plan_id,service_user_id,version_number,previous_care_plan_id,change_reason,status,created_at,updated_at,revision,organization_id,branch_id
                from care_plan_versions
                where organization_id=@organization and service_user_id=@person
            """ + (branchId is null ? string.Empty : " and branch_id=@branch") + " order by version_number desc";
            AddUuid(command, "organization", organizationId);
            AddUuid(command, "person", serviceUserId);
            if (branchId is not null) AddUuid(command, "branch", branchId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(ReadVersion(reader));
            return result;
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    public async Task<CarePlanLifecycleSnapshot> TransitionAsync(Guid carePlanId, long expectedRevision, CarePlanLifecycleStatus targetStatus, string reason, string comment, CarePlanActor actor, CancellationToken cancellationToken)
    {
        var snapshot = await RequireSnapshotAsync(carePlanId, actor, cancellationToken);
        EnsureRevision(snapshot.Version, expectedRevision);
        CarePlanLifecyclePolicy.EnsureTransition(snapshot.Version.Status, targetStatus);

        await ExecuteTransactionAsync(async () =>
        {
            if (targetStatus == CarePlanLifecycleStatus.InReview)
            {
                await using var goalOwners = context.Database.GetDbConnection().CreateCommand();
                goalOwners.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
                goalOwners.CommandText = """
                    select count(*) from care_plan_measurable_goals g where g.care_plan_id=@plan and g.organization_id=@organization
                      and not exists(select 1 from care_plan_measurable_goals n where n.supersedes_goal_id=g.id)
                      and not exists(select 1 from "AppUsers" u where u."Id"=g.owner_user_id and u."OrganizationId"=g.organization_id and u."IsActive"
                        and (u."BranchId"=g.branch_id or (u."BranchId" is null and u."Role"='Administrator'))
                        and u."Role" in ('Administrator','CareManager','CareCoordinator','CareWorker'))
                    """;
                AddUuid(goalOwners, "plan", carePlanId); AddUuid(goalOwners, "organization", actor.OrganizationId);
                if (Convert.ToInt64(await goalOwners.ExecuteScalarAsync(cancellationToken)) > 0)
                    throw new InvalidOperationException("Review measurable goal ownership before submitting the care plan. Replace goals whose owners are no longer eligible.");
                await using var needOwners = context.Database.GetDbConnection().CreateCommand();
                needOwners.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
                needOwners.CommandText = """
                    select count(*) from care_plan_needs n where n.care_plan_id=@plan and n.organization_id=@organization and n.status='Active'
                      and not exists(select 1 from care_plan_needs newer where newer.supersedes_need_id=n.id)
                      and not exists(select 1 from "AppUsers" u where u."Id"=n.owner_user_id and u."OrganizationId"=n.organization_id and u."IsActive"
                        and (u."BranchId"=n.branch_id or (u."BranchId" is null and u."Role"='Administrator'))
                        and u."Role" in ('Administrator','CareManager','CareCoordinator','CareWorker'))
                    """;
                AddUuid(needOwners, "plan", carePlanId); AddUuid(needOwners, "organization", actor.OrganizationId);
                if (Convert.ToInt64(await needOwners.ExecuteScalarAsync(cancellationToken)) > 0)
                    throw new InvalidOperationException("Review structured need ownership before submitting the care plan. Correct active needs whose owners are no longer eligible.");
            }
            await UpdateStatusAsync(snapshot.Version, targetStatus, expectedRevision, cancellationToken);
            await InsertEventAsync(snapshot.Version.CarePlanId, snapshot.Version.Status, targetStatus, reason, comment, actor, cancellationToken);
            context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), $"care_plan.{targetStatus.ToString().ToLowerInvariant()}", actor.UserName, nameof(CarePlan), carePlanId, DateTimeOffset.UtcNow, actor.OrganizationId, actor.BranchId));
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return await RequireSnapshotAsync(carePlanId, actor, cancellationToken);
    }

    public async Task<CarePlanLifecycleSnapshot> AddSignatureAsync(Guid carePlanId, SignCarePlanCommand command, CarePlanActor actor, CancellationToken cancellationToken)
    {
        var snapshot = await RequireSnapshotAsync(carePlanId, actor, cancellationToken);
        EnsureRevision(snapshot.Version, command.ExpectedRevision);
        if (snapshot.Version.Status is not CarePlanLifecycleStatus.Approved and not CarePlanLifecycleStatus.Signed)
            throw new InvalidOperationException("Only an approved or signed care plan can receive signatures.");

        if (command.SignerType == CarePlanSignerType.Representative && actor.Role == UserRole.FamilyMember)
        {
            if (actor.FamilyMemberId is null) throw new UnauthorizedAccessException("The family account is not linked to a representative record.");
            var linked = await context.FamilyMembers.AsNoTracking().AnyAsync(x => x.Id == actor.FamilyMemberId && x.ServiceUserId == snapshot.CarePlan.ServiceUserId && x.OrganizationId == actor.OrganizationId && (actor.BranchId == null || x.BranchId == actor.BranchId), cancellationToken);
            if (!linked) throw new UnauthorizedAccessException("This representative is not linked to the service user for this care plan.");
        }

        if (command.SignerType == CarePlanSignerType.ServiceUser && actor.Role == UserRole.ServiceUser)
            throw new InvalidOperationException("Direct service-user account signing requires a verified service-user account link. Use an authorized witnessed signature until that link is configured.");

        var duplicate = snapshot.Signatures.Any(x => x.SignerType == command.SignerType &&
            (command.SignerType != CarePlanSignerType.Representative || x.FamilyMemberId == actor.FamilyMemberId));
        if (duplicate) throw new InvalidOperationException("This required signer has already signed this care plan version.");

        await ExecuteTransactionAsync(async () =>
        {
            await using var sql = context.Database.GetDbConnection().CreateCommand();
            sql.CommandText = """
                insert into care_plan_signatures(id,care_plan_id,signer_type,signer_user_id,family_member_id,signer_name,relationship,declaration,signature_method,signed_at,organization_id,branch_id)
                values(@id,@plan,@type,@user,@family,@name,@relationship,@declaration,@method,now(),@organization,@branch)
            """;
            AddUuid(sql, "id", Guid.NewGuid());
            AddUuid(sql, "plan", carePlanId);
            AddText(sql, "type", command.SignerType.ToString());
            AddNullableUuid(sql, "user", actor.UserId);
            AddNullableUuid(sql, "family", command.SignerType == CarePlanSignerType.Representative ? actor.FamilyMemberId : null);
            AddText(sql, "name", command.SignerName);
            AddText(sql, "relationship", command.Relationship);
            AddText(sql, "declaration", command.Declaration);
            AddText(sql, "method", command.SignatureMethod.ToString());
            AddUuid(sql, "organization", actor.OrganizationId);
            AddUuid(sql, "branch", snapshot.Version.BranchId ?? actor.BranchId ?? TenantDefaults.BranchId);
            await sql.ExecuteNonQueryAsync(cancellationToken);

            await IncrementRevisionAsync(carePlanId, command.ExpectedRevision, cancellationToken);
            context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), $"care_plan.signed:{command.SignerType}", actor.UserName, nameof(CarePlan), carePlanId, DateTimeOffset.UtcNow, actor.OrganizationId, actor.BranchId));
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        var afterSignature = await RequireSnapshotAsync(carePlanId, actor, cancellationToken);
        if (afterSignature.Version.Status == CarePlanLifecycleStatus.Approved && afterSignature.RequiredSignaturesSatisfied)
        {
            return await TransitionAsync(carePlanId, afterSignature.Version.Revision, CarePlanLifecycleStatus.Signed, "Required signatures completed", string.Empty, actor, cancellationToken);
        }
        return afterSignature;
    }

    public async Task<CarePlanLifecycleSnapshot> ActivateAsync(Guid carePlanId, long expectedRevision, CarePlanActor actor, CancellationToken cancellationToken)
    {
        var snapshot = await RequireSnapshotAsync(carePlanId, actor, cancellationToken);
        EnsureRevision(snapshot.Version, expectedRevision);
        CarePlanLifecyclePolicy.EnsureTransition(snapshot.Version.Status, CarePlanLifecycleStatus.Active);
        if (!snapshot.RequiredSignaturesSatisfied) throw new InvalidOperationException("Required signatures are incomplete.");
        await EnsureActivationPrerequisitesAsync(snapshot.CarePlan.ServiceUserId, actor, cancellationToken);

        await ExecuteTransactionAsync(async () =>
        {
            var connection = context.Database.GetDbConnection();
            var activePlans = new List<(Guid CarePlanId, long Revision)>();
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = """
                    select care_plan_id,revision from care_plan_versions
                    where organization_id=@organization and service_user_id=@person and status='Active' and care_plan_id<>@plan
                    for update
                """;
                AddUuid(select, "organization", actor.OrganizationId);
                AddUuid(select, "person", snapshot.CarePlan.ServiceUserId);
                AddUuid(select, "plan", carePlanId);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) activePlans.Add((reader.GetGuid(0), reader.GetInt64(1)));
            }

            foreach (var old in activePlans)
            {
                await UpdateStatusByIdAsync(old.CarePlanId, old.Revision, CarePlanLifecycleStatus.Superseded, cancellationToken);
                await InsertEventAsync(old.CarePlanId, CarePlanLifecycleStatus.Active, CarePlanLifecycleStatus.Superseded, "Superseded by a newer active care plan version", string.Empty, actor, cancellationToken);
                context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "care_plan.superseded", actor.UserName, nameof(CarePlan), old.CarePlanId, DateTimeOffset.UtcNow, actor.OrganizationId, actor.BranchId));
            }

            await UpdateStatusAsync(snapshot.Version, CarePlanLifecycleStatus.Active, expectedRevision, cancellationToken);
            await InsertEventAsync(carePlanId, CarePlanLifecycleStatus.Signed, CarePlanLifecycleStatus.Active, "Activated for care delivery", string.Empty, actor, cancellationToken);
            context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "care_plan.active", actor.UserName, nameof(CarePlan), carePlanId, DateTimeOffset.UtcNow, actor.OrganizationId, actor.BranchId));
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return await RequireSnapshotAsync(carePlanId, actor, cancellationToken);
    }

    private async Task EnsureActivationPrerequisitesAsync(Guid serviceUserId,CarePlanActor actor,CancellationToken cancellationToken)
    {
        var connection=context.Database.GetDbConnection();var opened=connection.State!=ConnectionState.Open;if(opened)await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command=connection.CreateCommand();
            command.CommandText="""
                select
                 exists(select 1 from consent_records where service_user_id=@person and organization_id=@organization and branch_id=@branch and status='Active' and effective_from<=now() and (expires_at is null or expires_at>now())),
                 exists(select 1 from capacity_decisions where service_user_id=@person and organization_id=@organization and branch_id=@branch and status='Current' and (review_due_at is null or review_due_at>now())),
                 not exists(select 1 from capacity_decisions where service_user_id=@person and organization_id=@organization and branch_id=@branch and status='Current' and outcome='LacksCapacity')
                   or exists(select 1 from best_interest_decisions b join capacity_decisions c on c.id=b.capacity_decision_id where b.service_user_id=@person and b.organization_id=@organization and b.branch_id=@branch and b.status='Current' and c.status='Current' and (b.review_due_at is null or b.review_due_at>now())),
                 exists(select 1 from governed_assessments where service_user_id=@person and organization_id=@organization and branch_id=@branch and status='Current' and review_due_at>now()),
                 exists(select 1 from governed_risk_assessments where service_user_id=@person and organization_id=@organization and branch_id=@branch and status='Current' and review_due_at>now())
                """;
            AddUuid(command,"person",serviceUserId);AddUuid(command,"organization",actor.OrganizationId);AddUuid(command,"branch",actor.BranchId??TenantDefaults.BranchId);
            await using var reader=await command.ExecuteReaderAsync(cancellationToken);await reader.ReadAsync(cancellationToken);
            var missing=new List<string>();if(!reader.GetBoolean(0))missing.Add("active consent");if(!reader.GetBoolean(1))missing.Add("current capacity decision");if(!reader.GetBoolean(2))missing.Add("current best-interest decision");if(!reader.GetBoolean(3))missing.Add("current approved assessment");if(!reader.GetBoolean(4))missing.Add("current approved risk assessment");
            if(missing.Count>0)throw new InvalidOperationException($"Care plan activation is blocked: missing or overdue {string.Join(", ",missing)}.");
        }
        finally{if(opened)await connection.CloseAsync();}
    }

    public async Task<CarePlanLifecycleSnapshot> CreateRevisionAsync(Guid carePlanId, CreateCarePlanRevisionCommand command, CarePlanActor actor, CancellationToken cancellationToken)
    {
        var source = await RequireSnapshotAsync(carePlanId, actor, cancellationToken);
        EnsureRevision(source.Version, command.ExpectedRevision);
        if (source.Version.Status is CarePlanLifecycleStatus.Draft or CarePlanLifecycleStatus.InReview)
            throw new InvalidOperationException("Finish or update the current draft instead of creating another revision.");

        var versions = await GetVersionsAsync(source.CarePlan.ServiceUserId, actor.OrganizationId, actor.BranchId, cancellationToken);
        var nextVersionNumber = versions.Count == 0 ? 1 : versions.Max(x => x.VersionNumber) + 1;
        var newPlanId = Guid.NewGuid();
        var branchId = source.Version.BranchId ?? actor.BranchId ?? TenantDefaults.BranchId;

        await ExecuteTransactionAsync(async () =>
        {
            var newPlan = source.CarePlan with
            {
                Id = newPlanId,
                Version = $"v{nextVersionNumber}",
                Status = CarePlanLifecycleStatus.Draft.ToString()
            };
            context.CarePlans.Add(newPlan);

            var outcomes = await context.CarePlanOutcomes.AsNoTracking().Where(x => x.CarePlanId == carePlanId).ToListAsync(cancellationToken);
            foreach (var outcome in outcomes)
            {
                context.CarePlanOutcomes.Add(outcome with { Id = Guid.NewGuid(), CarePlanId = newPlanId });
            }
            await context.SaveChangesAsync(cancellationToken);

            await using (var metadata = context.Database.GetDbConnection().CreateCommand())
            {
                metadata.CommandText = """
                    insert into care_plan_versions(id,care_plan_id,service_user_id,version_number,previous_care_plan_id,change_reason,status,revision,created_at,updated_at,organization_id,branch_id)
                    values(@id,@plan,@person,@version,@previous,@reason,'Draft',1,now(),now(),@organization,@branch)
                """;
                AddUuid(metadata, "id", Guid.NewGuid());
                AddUuid(metadata, "plan", newPlanId);
                AddUuid(metadata, "person", source.CarePlan.ServiceUserId);
                AddInt(metadata, "version", nextVersionNumber);
                AddUuid(metadata, "previous", carePlanId);
                AddText(metadata, "reason", command.ChangeReason);
                AddUuid(metadata, "organization", actor.OrganizationId);
                AddUuid(metadata, "branch", branchId);
                await metadata.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var tasks = context.Database.GetDbConnection().CreateCommand())
            {
                tasks.CommandText = """
                    insert into care_plan_tasks(id,care_plan_id,service_user_id,organization_id,branch_id,title,category,instructions,is_required,frequency,status,created_at)
                    select gen_random_uuid(),@newplan,service_user_id,organization_id,branch_id,title,category,instructions,is_required,frequency,status,now()
                    from care_plan_tasks where care_plan_id=@oldplan
                """;
                AddUuid(tasks, "newplan", newPlanId);
                AddUuid(tasks, "oldplan", carePlanId);
                await tasks.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var goals = context.Database.GetDbConnection().CreateCommand())
            {
                goals.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
                goals.CommandText = """
                    insert into care_plan_measurable_goals(id,root_goal_id,care_plan_id,service_user_id,organization_id,branch_id,
                        source_goal_id,title,baseline_value,target_value,unit,baseline_date,review_due_date,target_date,
                        owner_user_id,owner_name,planned_actions,change_reason,created_by,request_snapshot)
                    select gen_random_uuid(),g.root_goal_id,@newplan,g.service_user_id,g.organization_id,g.branch_id,
                        g.id,g.title,g.baseline_value,g.target_value,g.unit,g.baseline_date,g.review_due_date,g.target_date,
                        g.owner_user_id,g.owner_name,g.planned_actions,@reason,@actor,
                        jsonb_build_object('sourceGoalId',g.id,'reason',@reason)
                    from care_plan_measurable_goals g where g.care_plan_id=@oldplan and g.organization_id=@organization
                      and not exists(select 1 from care_plan_measurable_goals n where n.supersedes_goal_id=g.id)
                    """;
                AddUuid(goals, "newplan", newPlanId); AddUuid(goals, "oldplan", carePlanId); AddUuid(goals, "organization", actor.OrganizationId);
                AddUuid(goals, "actor", actor.UserId ?? throw new InvalidOperationException("A named author is required to revise care goals."));
                AddText(goals, "reason", command.ChangeReason);
                await goals.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var emergency = context.Database.GetDbConnection().CreateCommand())
            {
                emergency.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
                emergency.CommandText = """
                    insert into care_plan_emergency_selections(id,care_plan_id,organization_id,branch_id,selection_version,
                      sections_json,source_selection_id,reason,created_by,request_snapshot)
                    select gen_random_uuid(),@newplan,organization_id,branch_id,1,sections_json,id,@reason,@actor,jsonb_build_object('copiedFrom',id)
                    from care_plan_emergency_selections where care_plan_id=@oldplan and organization_id=@organization
                    order by selection_version desc limit 1
                    """;
                AddUuid(emergency, "newplan", newPlanId); AddUuid(emergency, "oldplan", carePlanId); AddUuid(emergency, "organization", actor.OrganizationId);
                AddUuid(emergency, "actor", actor.UserId ?? throw new InvalidOperationException("A named author is required to copy emergency sections."));
                AddText(emergency, "reason", command.ChangeReason); await emergency.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var needs = context.Database.GetDbConnection().CreateCommand())
            {
                needs.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
                needs.CommandText = """
                    insert into care_plan_needs(id,root_need_id,care_plan_id,service_user_id,organization_id,branch_id,source_need_id,
                      section,identified_need,level_of_need,planned_outcomes,actions,owner_user_id,owner_name,status,change_reason,created_by,request_snapshot)
                    select gen_random_uuid(),root_need_id,@newplan,service_user_id,organization_id,branch_id,id,
                      section,identified_need,level_of_need,planned_outcomes,actions,owner_user_id,owner_name,status,@reason,@actor,jsonb_build_object('copiedFrom',id)
                    from care_plan_needs n where n.care_plan_id=@oldplan and n.organization_id=@organization
                      and not exists(select 1 from care_plan_needs newer where newer.supersedes_need_id=n.id)
                    """;
                AddUuid(needs, "newplan", newPlanId); AddUuid(needs, "oldplan", carePlanId); AddUuid(needs, "organization", actor.OrganizationId);
                AddUuid(needs, "actor", actor.UserId ?? throw new InvalidOperationException("A named author is required to copy care needs."));
                AddText(needs, "reason", command.ChangeReason); await needs.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var evidence = context.Database.GetDbConnection().CreateCommand())
            {
                evidence.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
                evidence.CommandText = """
                    insert into care_plan_evidence(id,care_plan_id,organization_id,branch_id,service_user_id,
                      source_kind,source_id,source_snapshot,section,rationale,copied_from_id,created_by,request_snapshot)
                    select gen_random_uuid(),@newplan,organization_id,branch_id,service_user_id,
                      source_kind,source_id,source_snapshot,section,rationale,id,@actor,jsonb_build_object('copiedFrom',id)
                    from care_plan_evidence e where care_plan_id=@oldplan and organization_id=@organization
                      and not exists(select 1 from care_plan_evidence_withdrawals w where w.evidence_id=e.id)
                    """;
                AddUuid(evidence, "newplan", newPlanId); AddUuid(evidence, "oldplan", carePlanId);
                AddUuid(evidence, "organization", actor.OrganizationId);
                AddUuid(evidence, "actor", actor.UserId ?? throw new InvalidOperationException("A named author is required to copy evidence."));
                await evidence.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var authorities = context.Database.GetDbConnection().CreateCommand())
            {
                authorities.CommandText = """
                    insert into care_plan_medication_authorities(
                        care_plan_id,medication_id,service_user_id,organization_id,branch_id,
                        support_type,instructions,review_due_at,family_medicine_applies,
                        family_recording_method,family_responsible_person,family_escalation_route,
                        prior_dose_check,updated_by_user_id,updated_at)
                    select @newplan,medication_id,service_user_id,organization_id,branch_id,
                        support_type,instructions,review_due_at,family_medicine_applies,
                        family_recording_method,family_responsible_person,family_escalation_route,
                        prior_dose_check,@actor,now()
                    from care_plan_medication_authorities where care_plan_id=@oldplan
                    """;
                AddUuid(authorities, "newplan", newPlanId);
                AddUuid(authorities, "oldplan", carePlanId);
                AddUuid(authorities, "actor", actor.UserId ?? throw new InvalidOperationException("A named user is required to revise medication authority."));
                await authorities.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertEventAsync(newPlanId, CarePlanLifecycleStatus.Draft, CarePlanLifecycleStatus.Draft, command.ChangeReason, "Revision created from previous care plan version", actor, cancellationToken);
            context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "care_plan.revision_created", actor.UserName, nameof(CarePlan), newPlanId, DateTimeOffset.UtcNow, actor.OrganizationId, branchId));
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return await RequireSnapshotAsync(newPlanId, actor, cancellationToken);
    }

    public async Task<CarePlanLifecycleSnapshot> AcknowledgeAsync(Guid carePlanId, long expectedRevision, CarePlanActor actor, CancellationToken cancellationToken)
    {
        var snapshot = await RequireSnapshotAsync(carePlanId, actor, cancellationToken);
        EnsureRevision(snapshot.Version, expectedRevision);
        if (snapshot.Version.Status != CarePlanLifecycleStatus.Active) throw new InvalidOperationException("Care workers can only acknowledge the active care plan version.");
        if (actor.CareWorkerId is null) throw new UnauthorizedAccessException("The account is not linked to a care worker.");
        if (snapshot.Acknowledgements.Any(x => x.CareWorkerId == actor.CareWorkerId)) return snapshot;

        await ExecuteTransactionAsync(async () =>
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                insert into care_plan_acknowledgements(id,care_plan_id,care_worker_id,acknowledged_by_user_id,acknowledged_by,acknowledged_at,organization_id,branch_id)
                values(@id,@plan,@worker,@user,@name,now(),@organization,@branch)
            """;
            AddUuid(command, "id", Guid.NewGuid());
            AddUuid(command, "plan", carePlanId);
            AddUuid(command, "worker", actor.CareWorkerId.Value);
            AddNullableUuid(command, "user", actor.UserId);
            AddText(command, "name", actor.UserName);
            AddUuid(command, "organization", actor.OrganizationId);
            AddUuid(command, "branch", snapshot.Version.BranchId ?? actor.BranchId ?? TenantDefaults.BranchId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await IncrementRevisionAsync(carePlanId, expectedRevision, cancellationToken);
            context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "care_plan.acknowledged", actor.UserName, nameof(CarePlan), carePlanId, DateTimeOffset.UtcNow, actor.OrganizationId, actor.BranchId));
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return await RequireSnapshotAsync(carePlanId, actor, cancellationToken);
    }

    private async Task ExecuteTransactionAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await operation();
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        });
    }

    private async Task<CarePlanLifecycleSnapshot> RequireSnapshotAsync(Guid carePlanId, CarePlanActor actor, CancellationToken cancellationToken) =>
        await GetAsync(carePlanId, actor.OrganizationId, actor.BranchId, cancellationToken)
        ?? throw new KeyNotFoundException("Care plan was not found.");

    private static void EnsureRevision(CarePlanVersionRecord version, long expectedRevision)
    {
        if (version.Revision != expectedRevision)
            throw new DBConcurrencyException("The care plan changed since it was loaded. Refresh it before trying again.");
    }

    private async Task UpdateStatusAsync(CarePlanVersionRecord version, CarePlanLifecycleStatus targetStatus, long expectedRevision, CancellationToken cancellationToken) =>
        await UpdateStatusByIdAsync(version.CarePlanId, expectedRevision, targetStatus, cancellationToken);

    private async Task UpdateStatusByIdAsync(Guid carePlanId, long expectedRevision, CarePlanLifecycleStatus targetStatus, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            update care_plan_versions set status=@status,revision=revision+1,updated_at=now()
            where care_plan_id=@plan and revision=@revision;
            update "CarePlans" set "Status"=@status where "Id"=@plan;
        """;
        AddText(command, "status", targetStatus.ToString());
        AddUuid(command, "plan", carePlanId);
        AddLong(command, "revision", expectedRevision);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected < 2) throw new DBConcurrencyException("The care plan changed while this action was being processed.");
    }

    private async Task IncrementRevisionAsync(Guid carePlanId, long expectedRevision, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "update care_plan_versions set revision=revision+1,updated_at=now() where care_plan_id=@plan and revision=@revision";
        AddUuid(command, "plan", carePlanId);
        AddLong(command, "revision", expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new DBConcurrencyException("The care plan changed while this action was being processed.");
    }

    private async Task InsertEventAsync(Guid carePlanId, CarePlanLifecycleStatus from, CarePlanLifecycleStatus to, string reason, string comment, CarePlanActor actor, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            insert into care_plan_lifecycle_events(id,care_plan_id,from_status,to_status,reason,comment,performed_by_user_id,performed_by,performed_at,organization_id,branch_id)
            values(@id,@plan,@from,@to,@reason,@comment,@user,@name,now(),@organization,@branch)
        """;
        AddUuid(command, "id", Guid.NewGuid());
        AddUuid(command, "plan", carePlanId);
        AddText(command, "from", from.ToString());
        AddText(command, "to", to.ToString());
        AddText(command, "reason", reason ?? string.Empty);
        AddText(command, "comment", comment ?? string.Empty);
        AddNullableUuid(command, "user", actor.UserId);
        AddText(command, "name", actor.UserName);
        AddUuid(command, "organization", actor.OrganizationId);
        AddUuid(command, "branch", actor.BranchId ?? TenantDefaults.BranchId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<CarePlanVersionRecord?> ReadVersionAsync(Guid carePlanId, Guid organizationId, Guid? branchId, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                select id,care_plan_id,service_user_id,version_number,previous_care_plan_id,change_reason,status,created_at,updated_at,revision,organization_id,branch_id
                from care_plan_versions where care_plan_id=@plan and organization_id=@organization
            """ + (branchId is null ? string.Empty : " and branch_id=@branch");
            AddUuid(command, "plan", carePlanId);
            AddUuid(command, "organization", organizationId);
            if (branchId is not null) AddUuid(command, "branch", branchId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadVersion(reader) : null;
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    private async Task<IReadOnlyList<CarePlanSignatureRecord>> ReadSignaturesAsync(Guid carePlanId, Guid organizationId, Guid? branchId, CancellationToken cancellationToken)
    {
        var result = new List<CarePlanSignatureRecord>();
        var connection = context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                select id,care_plan_id,signer_type,signer_user_id,family_member_id,signer_name,relationship,declaration,signature_method,signed_at,organization_id,branch_id
                from care_plan_signatures where care_plan_id=@plan and organization_id=@organization
            """ + (branchId is null ? string.Empty : " and branch_id=@branch") + " order by signed_at";
            AddUuid(command, "plan", carePlanId); AddUuid(command, "organization", organizationId); if (branchId is not null) AddUuid(command, "branch", branchId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new CarePlanSignatureRecord(reader.GetGuid(0), reader.GetGuid(1), Enum.Parse<CarePlanSignerType>(reader.GetString(2)), ReadNullableGuid(reader,3), ReadNullableGuid(reader,4), reader.GetString(5), reader.GetString(6), reader.GetString(7), Enum.Parse<CarePlanSignatureMethod>(reader.GetString(8)), AsUtc(reader.GetDateTime(9)), reader.GetGuid(10), reader.GetGuid(11)));
            return result;
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    private async Task<IReadOnlyList<CarePlanAcknowledgementRecord>> ReadAcknowledgementsAsync(Guid carePlanId, Guid organizationId, Guid? branchId, CancellationToken cancellationToken)
    {
        var result = new List<CarePlanAcknowledgementRecord>();
        var connection = context.Database.GetDbConnection(); var opened = connection.State != ConnectionState.Open; if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "select id,care_plan_id,care_worker_id,acknowledged_by_user_id,acknowledged_by,acknowledged_at,organization_id,branch_id from care_plan_acknowledgements where care_plan_id=@plan and organization_id=@organization" + (branchId is null ? string.Empty : " and branch_id=@branch") + " order by acknowledged_at";
            AddUuid(command,"plan",carePlanId); AddUuid(command,"organization",organizationId); if(branchId is not null) AddUuid(command,"branch",branchId.Value);
            await using var reader=await command.ExecuteReaderAsync(cancellationToken);
            while(await reader.ReadAsync(cancellationToken)) result.Add(new CarePlanAcknowledgementRecord(reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),ReadNullableGuid(reader,3),reader.GetString(4),AsUtc(reader.GetDateTime(5)),reader.GetGuid(6),reader.GetGuid(7)));
            return result;
        }
        finally { if(opened) await connection.CloseAsync(); }
    }

    private async Task<IReadOnlyList<CarePlanLifecycleEventRecord>> ReadEventsAsync(Guid carePlanId, Guid organizationId, Guid? branchId, CancellationToken cancellationToken)
    {
        var result = new List<CarePlanLifecycleEventRecord>();
        var connection=context.Database.GetDbConnection(); var opened=connection.State!=ConnectionState.Open; if(opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command=connection.CreateCommand();
            command.CommandText="select id,care_plan_id,from_status,to_status,reason,comment,performed_by_user_id,performed_by,performed_at,organization_id,branch_id from care_plan_lifecycle_events where care_plan_id=@plan and organization_id=@organization"+(branchId is null?string.Empty:" and branch_id=@branch")+" order by performed_at desc";
            AddUuid(command,"plan",carePlanId);AddUuid(command,"organization",organizationId);if(branchId is not null)AddUuid(command,"branch",branchId.Value);
            await using var reader=await command.ExecuteReaderAsync(cancellationToken);
            while(await reader.ReadAsync(cancellationToken)) result.Add(new CarePlanLifecycleEventRecord(reader.GetGuid(0),reader.GetGuid(1),Enum.Parse<CarePlanLifecycleStatus>(reader.GetString(2)),Enum.Parse<CarePlanLifecycleStatus>(reader.GetString(3)),reader.GetString(4),reader.GetString(5),ReadNullableGuid(reader,6),reader.GetString(7),AsUtc(reader.GetDateTime(8)),reader.GetGuid(9),reader.GetGuid(10)));
            return result;
        }
        finally { if(opened) await connection.CloseAsync(); }
    }

    private static CarePlanVersionRecord ReadVersion(DbDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt32(3), ReadNullableGuid(reader,4), reader.GetString(5), Enum.Parse<CarePlanLifecycleStatus>(reader.GetString(6)), AsUtc(reader.GetDateTime(7)), AsUtc(reader.GetDateTime(8)), reader.GetInt64(9), reader.GetGuid(10), reader.GetGuid(11));

    private static bool RequiredSignaturesSatisfied(IReadOnlyList<CarePlanSignatureRecord> signatures) =>
        signatures.Any(x => x.SignerType == CarePlanSignerType.CareManager) &&
        signatures.Any(x => x.SignerType is CarePlanSignerType.ServiceUser or CarePlanSignerType.Representative);

    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static Guid? ReadNullableGuid(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private static void AddUuid(DbCommand command, string name, Guid value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = value });
    private static void AddNullableUuid(DbCommand command, string name, Guid? value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = value is null ? DBNull.Value : value.Value });
    private static void AddText(DbCommand command, string name, string value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = value });
    private static void AddInt(DbCommand command, string name, int value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Integer) { Value = value });
    private static void AddLong(DbCommand command, string name, long value) => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Bigint) { Value = value });
}
