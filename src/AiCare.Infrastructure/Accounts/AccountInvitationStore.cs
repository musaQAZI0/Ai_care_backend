using System.Security.Cryptography;
using AiCare.Application.Accounts;
using AiCare.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiCare.Infrastructure.Accounts;

public sealed class AccountInvitationStore(CareDbContext db) : IAccountInvitationStore
{
    public async Task<AccountInvitationResult> CreateAsync(Guid organizationId,Guid? actorBranchId,Guid actorUserId,string actorName,CreateAccountInvitationCommand command,string tokenHash,DateTimeOffset expiresAt,CancellationToken ct)
    {
        var strategy=db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<AccountInvitationResult>(async()=>
        {
            await using var transaction=await db.Database.BeginTransactionAsync(ct);
            if(await db.AppUsers.AnyAsync(x=>x.UserName==command.UserName||x.Email==command.Email,ct))throw new InvalidOperationException("A user with that username or email already exists.");
            if(command.BranchId is not null&&!await db.Branches.AnyAsync(x=>x.Id==command.BranchId&&x.OrganizationId==organizationId,ct))throw new InvalidOperationException("The selected branch is not part of this organization.");
            if(command.Role==UserRole.CareWorker&&(command.CareWorkerId is null||!await db.CareWorkers.AnyAsync(x=>x.Id==command.CareWorkerId&&x.OrganizationId==organizationId,ct)))throw new InvalidOperationException("The selected care worker is not part of this organization.");
            var userId=Guid.NewGuid();
            db.AppUsers.Add(new AppUser(userId,command.UserName,command.Email,PasswordHasher.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))),command.Role,false,organizationId,command.BranchId,command.CareWorkerId));
            await db.SaveChangesAsync(ct);
            var invitationId=Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into account_invitations(id,user_id,token_hash,email,role,status,expires_at,created_at,created_by_user_id,created_by,organization_id,branch_id) values ({invitationId},{userId},{tokenHash},{command.Email},{command.Role.ToString()},'Sent',{expiresAt},now(),{actorUserId},{actorName},{organizationId},{actorBranchId})""",ct);
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),"account.invited",actorName,nameof(AppUser),userId,DateTimeOffset.UtcNow,organizationId,actorBranchId??TenantDefaults.BranchId));
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            return new(invitationId,userId,"Sent",expiresAt);
        });
    }

    public Task MarkQueueFailedAsync(Guid invitationId,string reason,CancellationToken ct)=>db.Database.ExecuteSqlInterpolatedAsync($"update account_invitations set status='Failed',failed_at=now(),failure_reason={reason} where id={invitationId}",ct);

    public async Task<AccountInvitationValidation> ValidateAsync(string tokenHash,DateTimeOffset now,CancellationToken ct)
    {
        var connection=(NpgsqlConnection)db.Database.GetDbConnection();
        if(connection.State!=System.Data.ConnectionState.Open)await connection.OpenAsync(ct);
        await using var command=new NpgsqlCommand("""select i.status,i.expires_at,u."UserName",o."Name",i.role from account_invitations i join "AppUsers" u on u."Id"=i.user_id join "Organizations" o on o."Id"=i.organization_id where i.token_hash=@token limit 1""",connection);
        command.Parameters.AddWithValue("token",tokenHash);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))return new(false,"Invalid","This invitation is invalid or no longer available.",null,null,null,null);
        var status=reader.GetString(0);var expires=reader.GetFieldValue<DateTimeOffset>(1);var name=reader.GetString(2);var provider=reader.GetString(3);var role=reader.GetString(4);
        if(expires<=now)return new(false,"Expired","This invitation has expired. Ask an administrator to resend it.",name,provider,role,expires);
        if(status is not ("Pending" or "Sent"))return new(false,status,"This invitation has already been used or revoked.",name,provider,role,expires);
        return new(true,status,"Invitation is valid.",name,provider,role,expires);
    }

    public async Task AcceptAsync(string tokenHash,string password,DateTimeOffset now,CancellationToken ct)
    {
        var strategy=db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async()=>
        {
            await using var transaction=await db.Database.BeginTransactionAsync(ct);
            var row=await db.Database.SqlQuery<InvitationRow>($"""select i.id as "InvitationId",i.user_id as "UserId",i.status as "Status",i.expires_at as "ExpiresAt",i.organization_id as "OrganizationId",i.branch_id as "BranchId" from account_invitations i where i.token_hash={tokenHash} for update""").SingleOrDefaultAsync(ct)??throw new InvalidOperationException("Invitation is invalid.");
            if(row.ExpiresAt<=now)throw new InvalidOperationException("Invitation has expired.");
            if(row.Status is not ("Pending" or "Sent"))throw new InvalidOperationException("Invitation has already been used or revoked.");
            var user=await db.AppUsers.SingleAsync(x=>x.Id==row.UserId,ct);
            db.Entry(user).CurrentValues.SetValues(user with{PasswordHash=PasswordHasher.HashPassword(password),IsActive=true});
            await db.SaveChangesAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"update account_invitations set status='Accepted',accepted_at={now},accepted_terms_at={now} where id={row.InvitationId}",ct);
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),"account.invitation_accepted",user.UserName,nameof(AppUser),user.Id,now,row.OrganizationId,row.BranchId??TenantDefaults.BranchId));
            await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);
        });
    }
    private sealed class InvitationRow{public Guid InvitationId{get;set;}public Guid UserId{get;set;}public string Status{get;set;}="";public DateTimeOffset ExpiresAt{get;set;}public Guid OrganizationId{get;set;}public Guid? BranchId{get;set;}}
}
