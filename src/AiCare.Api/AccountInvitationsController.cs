using AiCare.Application;
using AiCare.Application.Accounts;
using AiCare.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCare.Api;

[ApiController]
[Route("api/phase1/account-invitations")]
public sealed class AccountInvitationsController(IAccountInvitationService invitations,ITenantContext tenant,ICurrentUserContext currentUser,IConfiguration configuration):ControllerBase
{
    [Authorize(Policy="Phase1User")]
    [HttpPost]
    public async Task<IActionResult> Create(CreateAccountInvitationRequest request,CancellationToken ct)
    {
        if(!currentUser.IsAdministrator)return Forbid();
        try
        {
            var baseUrl=configuration["Frontend:BaseUrl"]??throw new InvalidOperationException("Frontend:BaseUrl must be configured before invitations can be sent.");
            var result=await invitations.CreateAsync(tenant.OrganizationId,tenant.BranchId,currentUser.UserId??throw new InvalidOperationException("Authenticated user identifier is required."),currentUser.UserName,new(request.UserName??"",request.Email??"",request.Role,request.BranchId,request.CareWorkerId),baseUrl,ct);
            return StatusCode(StatusCodes.Status201Created,result);
        }
        catch(InvalidOperationException ex){return BadRequest(new{message=ex.Message});}
    }
    [AllowAnonymous][HttpPost("validate")]
    public Task<AccountInvitationValidation> Validate(ValidateAccountInvitationRequest request,CancellationToken ct)=>invitations.ValidateAsync(request.Token??"",ct);
    [AllowAnonymous][HttpPost("accept")]
    public async Task<IActionResult> Accept(AcceptAccountInvitationRequest request,CancellationToken ct)
    {
        try{await invitations.AcceptAsync(request.Token??"",request.Password??"",request.AcceptTerms,ct);return Ok(new{status="Activated",message="Account activated. You can now sign in."});}
        catch(InvalidOperationException ex){return BadRequest(new{message=ex.Message});}
    }
    [Authorize(Policy="Phase1User")]
    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id,RevokeAccountInvitationRequest request,CancellationToken ct)
    {
        if(!currentUser.IsAdministrator)return Forbid();
        if(string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Trim().Length is <10 or >500)return BadRequest(new{message="A reason of 10 to 500 characters is required."});
        try
        {
            var revoked=await invitations.RevokeAsync(id,tenant.OrganizationId,tenant.BranchId,tenant.IsOrganizationWide,currentUser.UserId??throw new InvalidOperationException("Authenticated user identifier is required."),currentUser.UserName,request.Reason.Trim(),ct);
            return revoked?NoContent():NotFound();
        }
        catch(InvalidOperationException ex){return Conflict(new{message=ex.Message});}
    }
}
public sealed record CreateAccountInvitationRequest(string? UserName,string? Email,UserRole Role,Guid? BranchId,Guid? CareWorkerId);
public sealed record ValidateAccountInvitationRequest(string? Token);
public sealed record AcceptAccountInvitationRequest(string? Token,string? Password,bool AcceptTerms);
public sealed record RevokeAccountInvitationRequest(string? Reason);
