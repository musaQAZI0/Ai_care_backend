using System.Data.Common;
using AiCare.Application;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiCare.Api;

[ApiController]
[Authorize]
[Route("api/messaging/conversations/{conversationId:guid}/attachments")]
public sealed class MessagingAttachmentsController : ControllerBase
{
    private readonly CareDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUserContext _user;

    public MessagingAttachmentsController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user)
    {
        _db = db;
        _tenant = tenant;
        _user = user;
    }

    [HttpGet]
    public async Task<IActionResult> Get(Guid conversationId, CancellationToken cancellationToken)
    {
        var userId = _user.UserId;
        if (userId is null) return Unauthorized();

        var connectionString = _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString)) return StatusCode(503);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var access = await GetAccessibleServiceUserIdAsync(connection, conversationId, userId.Value, cancellationToken);
        if (!access.Found) return NotFound();

        if (_user.IsFamilyMember)
        {
            if (_user.FamilyMemberId is null || access.ServiceUserId is null) return Forbid();
            if (!await FamilyHasPermissionAsync(connection, access.ServiceUserId.Value, "MessageCareTeam", cancellationToken)) return NotFound();
            if (!await FamilyHasPermissionAsync(connection, access.ServiceUserId.Value, "ViewDocuments", cancellationToken)) return Forbid();
        }

        var rows = new List<MessageAttachmentDto>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select a.message_id, d."Id", d."FileName", d."Category"
            from conversation_message_attachments a
            join conversation_messages m on m.id = a.message_id
            join "Documents" d on d."Id" = a.document_id
            where m.conversation_id = @conversationId
              and d."OrganizationId" = @organizationId
              and m.deleted_at is null
            order by m.sent_at, d."FileName"
            """;
        Add(command, "conversationId", conversationId);
        Add(command, "organizationId", _tenant.OrganizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new MessageAttachmentDto(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3)));

        return Ok(rows);
    }

    private async Task<ConversationAccessResult> GetAccessibleServiceUserIdAsync(DbConnection connection, Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select c.service_user_id
            from conversations c
            join conversation_participants p on p.conversation_id = c.id
            where c.id = @conversationId and c.organization_id = @organizationId
              and p.user_id = @userId and p.left_at is null
            limit 1
            """;
        Add(command, "conversationId", conversationId);
        Add(command, "organizationId", _tenant.OrganizationId);
        Add(command, "userId", userId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null
            ? new ConversationAccessResult(false, null)
            : new ConversationAccessResult(true, value is DBNull ? null : (Guid)value);
    }

    private async Task<bool> FamilyHasPermissionAsync(DbConnection connection, Guid serviceUserId, string permission, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select exists(
                select 1 from family_access_grants g
                join family_access_permissions p on p.access_grant_id=g.id
                where g.organization_id=@organizationId
                  and g.family_member_id=@familyMemberId
                  and g.service_user_id=@serviceUserId
                  and g.verification_status='Verified'
                  and g.access_status='Active'
                  and (g.valid_from is null or g.valid_from<=now())
                  and (g.valid_until is null or g.valid_until>now())
                  and p.permission=@permission)
            """;
        Add(command, "organizationId", _tenant.OrganizationId);
        Add(command, "familyMemberId", _user.FamilyMemberId!.Value);
        Add(command, "serviceUserId", serviceUserId);
        Add(command, "permission", permission);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private readonly record struct ConversationAccessResult(bool Found, Guid? ServiceUserId);
}

public sealed record MessageAttachmentDto(Guid MessageId, Guid DocumentId, string FileName, string Category);
