using System.Data.Common;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiCare.Api;

// One policy for discovery, participation, and subsequent resource access.
internal sealed class MessagingAccess(CareDbContext db, ITenantContext tenant, ICurrentUserContext current)
{
    public Task<AppUser?> Actor(CancellationToken t) => db.AppUsers.AsNoTracking().SingleOrDefaultAsync(
        x=>x.Id==current.UserId && x.OrganizationId==tenant.OrganizationId && x.IsActive,t);

    public async Task<bool> Person(AppUser actor, Guid personId, CancellationToken t)
    {
        var person=await db.ServiceUsers.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==personId&&x.OrganizationId==tenant.OrganizationId,t);
        if(person is null || actor.OrganizationId!=person.OrganizationId || !actor.IsActive)return false;
        if(actor.Role==UserRole.FamilyMember)return await FamilyPermission(actor,personId,"MessageCareTeam",t);
        if(actor.Role==UserRole.Administrator)return true;
        if(actor.BranchId is null || actor.BranchId!=person.BranchId)return false;
        if(actor.Role is UserRole.CareManager or UserRole.CareCoordinator)return true;
        return actor.Role==UserRole.CareWorker && actor.CareWorkerId is Guid worker &&
            await db.Visits.AnyAsync(x=>x.OrganizationId==tenant.OrganizationId&&x.BranchId==person.BranchId&&x.ServiceUserId==personId&&x.CareWorkerId==worker&&x.Status!=VisitStatus.Cancelled,t);
    }

    public async Task<List<AppUser>> Directory(Guid? person, CancellationToken t)
    {
        var actor=await Actor(t);if(actor is null)return [];
        if(person is Guid p && !await Person(actor,p,t))return [];
        var candidates=await db.AppUsers.AsNoTracking().Where(x=>x.OrganizationId==tenant.OrganizationId&&x.IsActive&&x.Id!=actor.Id).OrderBy(x=>x.UserName).ToListAsync(t);
        var result=new List<AppUser>();
        if(person is Guid personId)
        {
            foreach(var candidate in candidates)
                if((actor.Role!=UserRole.FamilyMember||candidate.Role!=UserRole.FamilyMember)&&await Person(candidate,personId,t))result.Add(candidate);
            return result;
        }
        // Without a selected person, family/worker discovery is the union of their care teams.
        if(actor.Role is UserRole.FamilyMember or UserRole.CareWorker)
        {
            var people=await db.ServiceUsers.Where(x=>x.OrganizationId==tenant.OrganizationId).Select(x=>x.Id).ToListAsync(t);
            foreach(var id in people)
                if(await Person(actor,id,t))
                    foreach(var candidate in candidates.Where(x=>x.Role!=UserRole.FamilyMember))
                        if(!result.Any(x=>x.Id==candidate.Id)&&await Person(candidate,id,t))result.Add(candidate);
            return result;
        }
        if(actor.Role is not (UserRole.Administrator or UserRole.CareManager or UserRole.CareCoordinator or UserRole.BackOffice))return [];
        return candidates.Where(x=>x.Role is not (UserRole.FamilyMember or UserRole.ServiceUser) &&
            (actor.Role==UserRole.Administrator || actor.BranchId is not null&&x.BranchId==actor.BranchId)).ToList();
    }

    public async Task<bool> Conversation(Guid id,CancellationToken t)
    {
        var actor=await Actor(t);if(actor is null)return false;
        await using var connection=new NpgsqlConnection(db.Database.GetConnectionString());await connection.OpenAsync(t);
        Guid? person;Guid? branch;
        await using(var c=connection.CreateCommand())
        {
            c.CommandText="select c.service_user_id,c.branch_id from conversations c join conversation_participants p on p.conversation_id=c.id where c.id=@id and c.organization_id=@org and p.user_id=@actor and p.left_at is null";
            c.Parameters.AddWithValue("id",id);c.Parameters.AddWithValue("org",tenant.OrganizationId);c.Parameters.AddWithValue("actor",actor.Id);
            await using var r=await c.ExecuteReaderAsync(t);if(!await r.ReadAsync(t))return false;
            person=r.IsDBNull(0)?null:r.GetGuid(0);branch=r.IsDBNull(1)?null:r.GetGuid(1);
        }
        if(person is Guid p)return await Person(actor,p,t);
        return actor.Role is not (UserRole.FamilyMember or UserRole.ServiceUser) &&
            (actor.Role==UserRole.Administrator || actor.BranchId is not null&&actor.BranchId==branch);
    }

    public async Task<bool> Document(AppUser actor,Guid documentId,Guid? person,CancellationToken t)
    {
        if(person is not Guid p || !await Person(actor,p,t))return false;
        var document=await db.Documents.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==documentId&&x.OrganizationId==tenant.OrganizationId&&x.ServiceUserId==p,t);
        if(document is null)return false;
        if(actor.Role!=UserRole.FamilyMember)return actor.Role==UserRole.Administrator || document.BranchId==actor.BranchId;
        if(!await FamilyPermission(actor,p,"ViewDocuments",t))return false;
        return await Exists("""
            select exists(select 1 from family_document_visibility v where v.document_id=@document and v.organization_id=@org
            and (v.visibility='ServiceUserAndRepresentative' or (v.visibility='ExplicitFamilyAccess' and exists(
                select 1 from family_document_grants g where g.document_id=v.document_id and g.organization_id=@org and g.family_member_id=@family))))
            """,c=>{c.Parameters.AddWithValue("document",documentId);c.Parameters.AddWithValue("family",actor.FamilyMemberId!.Value);},t);
    }

    private Task<bool> FamilyPermission(AppUser actor,Guid person,string permission,CancellationToken t)
        => actor.FamilyMemberId is not Guid family ? Task.FromResult(false) : Exists("""
            select exists(select 1 from family_access_grants g join family_access_permissions p on p.access_grant_id=g.id
            where g.organization_id=@org and g.family_member_id=@family and g.service_user_id=@person
            and g.verification_status='Verified' and g.access_status='Active'
            and (g.valid_from is null or g.valid_from<=now()) and (g.valid_until is null or g.valid_until>now()) and p.permission=@permission)
            """,c=>{c.Parameters.AddWithValue("family",family);c.Parameters.AddWithValue("person",person);c.Parameters.AddWithValue("permission",permission);},t);
    private async Task<bool> Exists(string sql,Action<NpgsqlCommand> bind,CancellationToken t)
    {
        await using var connection=new NpgsqlConnection(db.Database.GetConnectionString());await connection.OpenAsync(t);
        await using var c=connection.CreateCommand();c.CommandText=sql;c.Parameters.AddWithValue("org",tenant.OrganizationId);bind(c);
        return Convert.ToBoolean(await c.ExecuteScalarAsync(t));
    }
}
