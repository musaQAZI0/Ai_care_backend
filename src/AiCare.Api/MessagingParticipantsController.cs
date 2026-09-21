using AiCare.Application;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize]
[Route("api/messaging/participants")]
public sealed class MessagingParticipantsController : ControllerBase
{
    private readonly CareDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUserContext _user;

    public MessagingParticipantsController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user)
    {
        _db = db;
        _tenant = tenant;
        _user = user;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken, [FromQuery] Guid? serviceUserId = null)
    {
        if (_user.UserId is null) return Unauthorized();
        var users=await new MessagingAccess(_db,_tenant,_user).Directory(serviceUserId,cancellationToken);
        return Ok(users.Select(x=>new MessagingParticipantDto(x.Id,x.UserName,x.Email,x.Role.ToString(),x.CareWorkerId,x.FamilyMemberId)));
    }

}

public sealed record MessagingParticipantDto(
    Guid Id,
    string UserName,
    string Email,
    string Role,
    Guid? CareWorkerId,
    Guid? FamilyMemberId);
