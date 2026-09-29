using AiCare.Application.Accounts;
using AiCare.Application.Email;
using AiCare.Domain;
using Xunit;

namespace AiCare.Tests;

public sealed class AccountInvitationServiceTests
{
    [Fact]
    public async Task StaffInvitationCreatesInactiveTokenRecordAndQueuesRoleBoundActivation()
    {
        var store=new Store();var sender=new Sender();var service=new AccountInvitationService(store,sender);
        var result=await service.CreateAsync(Guid.NewGuid(),null,Guid.NewGuid(),"admin",new("worker@example.com","worker@example.com",UserRole.CareWorker,null,Guid.NewGuid()),"https://app.example.com",default);
        Assert.Equal("Sent",result.Status);Assert.NotNull(sender.Request);Assert.Equal("CareWorker",sender.Request!.Role);Assert.StartsWith("https://app.example.com/staff/activate?token=",sender.Request.ActivationUrl);Assert.DoesNotContain(sender.Request.ActivationUrl,result.ToString());Assert.Equal(64,store.TokenHash!.Length);
    }

    [Fact]
    public async Task QueueFailureKeepsAccountInactiveAndMarksInvitationFailed()
    {
        var store=new Store();var sender=new Sender{Fail=true};var service=new AccountInvitationService(store,sender);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.CreateAsync(Guid.NewGuid(),null,Guid.NewGuid(),"admin",new("manager@example.com","manager@example.com",UserRole.CareManager,null,null),"https://app.example.com",default));
        Assert.True(store.Failed);
    }

    [Fact]
    public async Task RecipientRolesCannotUseStaffInvitationFlow()
    {
        var service=new AccountInvitationService(new Store(),new Sender());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.CreateAsync(Guid.NewGuid(),null,Guid.NewGuid(),"admin",new("family@example.com","family@example.com",UserRole.FamilyMember,null,null),"https://app.example.com",default));
    }

    private sealed class Store:IAccountInvitationStore
    {
        public string? TokenHash;public bool Failed;
        public Task<AccountInvitationResult>CreateAsync(Guid o,Guid? b,Guid a,string n,CreateAccountInvitationCommand c,string hash,DateTimeOffset expiry,CancellationToken ct){TokenHash=hash;return Task.FromResult(new AccountInvitationResult(Guid.NewGuid(),Guid.NewGuid(),"Sent",expiry));}
        public Task MarkQueueFailedAsync(Guid id,string reason,CancellationToken ct){Failed=true;return Task.CompletedTask;}
        public Task<AccountInvitationValidation>ValidateAsync(string hash,DateTimeOffset now,CancellationToken ct)=>Task.FromResult(new AccountInvitationValidation(true,"Sent","Invitation is valid.","User","Provider","CareWorker",now.AddHours(1)));
        public Task AcceptAsync(string hash,string password,DateTimeOffset now,CancellationToken ct)=>Task.CompletedTask;
    }
    private sealed class Sender:IAccountInvitationEmailSender
    {
        public AccountInvitationEmailRequest? Request;public bool Fail;
        public Task SendAsync(AccountInvitationEmailRequest request,CancellationToken ct){Request=request;if(Fail)throw new HttpRequestException("queue unavailable");return Task.CompletedTask;}
    }
}
