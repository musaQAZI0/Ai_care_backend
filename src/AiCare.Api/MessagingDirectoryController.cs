using System.Data.Common;
using AiCare.Application;
using AiCare.Application.FamilyPortal;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiCare.Api;

[ApiController]
[Authorize]
[Route("api/messaging")]
public sealed class MessagingDirectoryController : ControllerBase
{
    private readonly CareDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUserContext _user;
    private readonly IFamilyPortalService _familyPortal;

    public MessagingDirectoryController(
        CareDbContext db,
        ITenantContext tenant,
        ICurrentUserContext user,
        IFamilyPortalService familyPortal)
    {
        _db = db;
        _tenant = tenant;
        _user = user;
        _familyPortal = familyPortal;
    }

    [HttpGet("participants")]
    public async Task<IActionResult> GetParticipants(CancellationToken cancellationToken)
    {
        var query = _db.AppUsers.AsNoTracking()
            .Where(x => x.OrganizationId == _tenant.OrganizationId && x.IsActive);

        if (_user.IsFamilyMember)
        {
            query = query.Where(x => x.Role == UserRole.Administrator ||
                                     x.Role == UserRole.CareManager ||
                                     x.Role == UserRole.CareCoordinator ||
                                     x.Role == UserRole.CareWorker);
        }

        var participants = await query
            .OrderBy(x => x.UserName)
            .Select(x => new MessagingParticipantDto(
                x.Id,
                x.UserName,
                x.Email,
                x.Role.ToString(),
                x.CareWorkerId,
                x.FamilyMemberId))
            .ToListAsync(cancellationToken);

        return Ok(participants);
    }

    [HttpGet("conversations/{conversationId:guid}/attachments")]
    public async Task<IActionResult> GetAttachments(Guid conversationId, CancellationToken cancellationToken)
    {
        var userId = _user.UserId;
        if (userId is null) return Unauthorized();

        var conversation = await GetAccessibleConversationAsync(conversationId, userId.Value, cancellationToken);
        if (conversation is null) return NotFound();

        if (_user.IsFamilyMember)
        {
            if (_user.FamilyMemberId is null || conversation.ServiceUserId is null) return Forbid();
            try
            {
                await _familyPortal.EnsurePermissionAsync(
                    _tenant.OrganizationId,
                    _user.FamilyMemberId.Value,
                    conversation.ServiceUserId.Value,
                    FamilyPermissions.ViewDocuments,
                    cancellationToken);
            }
            catch (InvalidOperationException)
            {
                return Forbid();
            }
        }

        var rows = new List<MessageAttachmentDto>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select a.message_id, a.document_id, d."FileName", d."Category"
            from conversation_message_attachments a
            join conversation_messages m on m.id = a.message_id and m.deleted_at is null
            join "Documents" d on d."Id" = a.document_id
            where m.conversation_id = @conversationId
              and d."OrganizationId" = @organizationId
            order by m.sent_at, d."FileName"
            """;
        Add(command, "conversationId", conversationId);
        Add(command, "organizationId", _tenant.OrganizationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MessageAttachmentDto(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return Ok(rows);
    }

    private async Task<AccessibleConversation?> GetAccessibleConversationAsync(
        Guid conversationId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select c.service_user_id
            from conversations c
            join conversation_participants p on p.conversation_id = c.id
            where c.id = @conversationId
              and c.organization_id = @organizationId
              and p.user_id = @userId
              and p.left_at is null
            limit 1
            """;
        Add(command, "conversationId", conversationId);
        Add(command, "organizationId", _tenant.OrganizationId);
        Add(command, "userId", userId);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null) return null;
        return new AccessibleConversation(value is DBNull ? null : (Guid)value);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connectionString = _db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("Database connection string is unavailable.");
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record AccessibleConversation(Guid? ServiceUserId);
}

public sealed record MessagingParticipantDto(
    Guid Id,
    string UserName,
    string Email,
    string Role,
    Guid? CareWorkerId,
    Guid? FamilyMemberId);

public sealed record MessageAttachmentDto(
    Guid MessageId,
    Guid DocumentId,
    string FileName,
    string Category);
