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
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var currentUserId = _user.UserId;
        if (currentUserId is null) return Unauthorized();

        var users = await _db.AppUsers.AsNoTracking()
            .Where(x => x.OrganizationId == _tenant.OrganizationId && x.IsActive && x.Id != currentUserId.Value)
            .OrderBy(x => x.UserName)
            .Select(x => new MessagingParticipantDto(
                x.Id,
                x.UserName,
                x.Email,
                x.Role.ToString(),
                x.CareWorkerId,
                x.FamilyMemberId))
            .ToArrayAsync(cancellationToken);

        // Family users may message the care team but should not receive a directory of other family accounts.
        if (_user.IsFamilyMember)
        {
            users = users.Where(x => !string.Equals(x.Role, "FamilyMember", StringComparison.Ordinal)).ToArray();
        }

        return Ok(users);
    }
}

public sealed record MessagingParticipantDto(
    Guid Id,
    string UserName,
    string Email,
    string Role,
    Guid? CareWorkerId,
    Guid? FamilyMemberId);
