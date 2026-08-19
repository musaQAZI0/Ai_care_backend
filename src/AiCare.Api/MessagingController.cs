using System.Data.Common;
using AiCare.Application;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize]
[Route("api/messaging")]
public sealed class MessagingController : ControllerBase
{
    private readonly CareDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUserContext _user;

    public MessagingController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user)
    {
        _db = db;
        _tenant = tenant;
        _user = user;
    }

    [HttpGet("conversations")]
    public async Task<IActionResult> GetConversations(CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var rows = new List<ConversationSummaryDto>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select c.id, c.service_user_id, c.subject, c.status, c.created_at, c.updated_at,
                   coalesce((select m.body from conversation_messages m where m.conversation_id=c.id and m.deleted_at is null order by m.sent_at desc limit 1), ''),
                   (select count(*) from conversation_messages m
                    where m.conversation_id=c.id and m.sender_user_id<>@userId and m.deleted_at is null
                      and not exists(select 1 from conversation_message_reads r where r.message_id=m.id and r.user_id=@userId))
            from conversations c
            join conversation_participants p on p.conversation_id=c.id and p.user_id=@userId and p.left_at is null
            where c.organization_id=@organizationId
            order by c.updated_at desc
            """;
        Add(command, "userId", userId);
        Add(command, "organizationId", _tenant.OrganizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ConversationSummaryDto(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetString(6),
                reader.GetInt64(7)));
        }
        return Ok(rows);
    }

    [HttpPost("conversations")]
    public async Task<IActionResult> CreateConversation(CreateConversationRequest request, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        if (string.IsNullOrWhiteSpace(request.Subject)) return BadRequest(new { message = "Subject is required." });
        if (request.Subject.Trim().Length > 200) return BadRequest(new { message = "Subject is too long." });

        if (request.ServiceUserId is not null)
        {
            var serviceUserExists = await _db.ServiceUsers.AsNoTracking().AnyAsync(x =>
                x.Id == request.ServiceUserId && x.OrganizationId == _tenant.OrganizationId, cancellationToken);
            if (!serviceUserExists) return NotFound();
            if (_user.IsFamilyMember && !await FamilyCanMessageAsync(request.ServiceUserId.Value, cancellationToken)) return Forbid();
        }
        else if (_user.IsFamilyMember)
        {
            return BadRequest(new { message = "Family conversations must be linked to a service user." });
        }

        var participants = request.ParticipantUserIds.Append(userId).Distinct().ToArray();
        if (participants.Length > 50) return BadRequest(new { message = "Too many participants." });
        var validParticipantCount = await _db.AppUsers.AsNoTracking().CountAsync(x =>
            x.OrganizationId == _tenant.OrganizationId && x.IsActive && participants.Contains(x.Id), cancellationToken);
        if (validParticipantCount != participants.Length) return BadRequest(new { message = "All participants must be active users in this organization." });

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                insert into conversations(id,service_user_id,subject,status,created_by_user_id,created_at,updated_at,organization_id,branch_id)
                values(@id,@serviceUserId,@subject,'Active',@userId,@now,@now,@organizationId,@branchId)
                """;
            Add(command, "id", id);
            Add(command, "serviceUserId", request.ServiceUserId);
            Add(command, "subject", request.Subject.Trim());
            Add(command, "userId", userId);
            Add(command, "now", now);
            Add(command, "organizationId", _tenant.OrganizationId);
            Add(command, "branchId", _tenant.BranchId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var participantId in participants)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "insert into conversation_participants(conversation_id,user_id,joined_at,last_read_at) values(@conversationId,@userId,@now,@now)";
            Add(command, "conversationId", id);
            Add(command, "userId", participantId);
            Add(command, "now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return CreatedAtAction(nameof(GetConversation), new { conversationId = id }, new { id });
    }

    [HttpGet("conversations/{conversationId:guid}")]
    public async Task<IActionResult> GetConversation(Guid conversationId, CancellationToken cancellationToken)
    {
        if (!await CanAccessConversationAsync(conversationId, cancellationToken)) return NotFound();
        var messages = new List<MessageDto>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select m.id,m.sender_user_id,m.body,m.reply_to_message_id,m.sent_at,m.edited_at,
                   coalesce((select count(*) from conversation_message_reads r where r.message_id=m.id),0)
            from conversation_messages m
            where m.conversation_id=@conversationId and m.deleted_at is null
            order by m.sent_at
            """;
        Add(command, "conversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new MessageDto(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.GetInt64(6)));
        }
        return Ok(new { id = conversationId, messages });
    }

    [HttpPost("conversations/{conversationId:guid}/messages")]
    public async Task<IActionResult> SendMessage(Guid conversationId, SendMessageRequest request, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        if (!await CanAccessConversationAsync(conversationId, cancellationToken)) return NotFound();
        var body = request.Body?.Trim() ?? string.Empty;
        if (body.Length == 0 || body.Length > 5000) return BadRequest(new { message = "Message body must contain 1 to 5000 characters." });
        if (request.ReplyToMessageId is not null && !await MessageBelongsToConversationAsync(request.ReplyToMessageId.Value, conversationId, cancellationToken))
            return BadRequest(new { message = "Reply target is invalid." });

        var attachmentIds = request.DocumentIds.Distinct().ToArray();
        if (attachmentIds.Length > 10) return BadRequest(new { message = "A message can contain at most 10 attachments." });
        if (attachmentIds.Length > 0)
        {
            var serviceUserId = await GetConversationServiceUserIdAsync(conversationId, cancellationToken);
            var validDocuments = await _db.Documents.AsNoTracking().CountAsync(x =>
                x.OrganizationId == _tenant.OrganizationId && attachmentIds.Contains(x.Id) &&
                (serviceUserId == null || x.ServiceUserId == serviceUserId), cancellationToken);
            if (validDocuments != attachmentIds.Length) return BadRequest(new { message = "One or more attachments are not available to this conversation." });
        }

        var messageId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "insert into conversation_messages(id,conversation_id,sender_user_id,body,reply_to_message_id,sent_at) values(@id,@conversationId,@sender,@body,@replyTo,@now)";
            Add(command, "id", messageId); Add(command, "conversationId", conversationId); Add(command, "sender", userId);
            Add(command, "body", body); Add(command, "replyTo", request.ReplyToMessageId); Add(command, "now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var documentId in attachmentIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "insert into conversation_message_attachments(message_id,document_id) values(@messageId,@documentId)";
            Add(command, "messageId", messageId); Add(command, "documentId", documentId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "insert into conversation_message_reads(message_id,user_id,read_at) values(@messageId,@userId,@now) on conflict do nothing; update conversations set updated_at=@now where id=@conversationId";
            Add(command, "messageId", messageId); Add(command, "userId", userId); Add(command, "now", now); Add(command, "conversationId", conversationId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return Ok(new { id = messageId, sentAt = now });
    }

    [HttpPost("conversations/{conversationId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid conversationId, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        if (!await CanAccessConversationAsync(conversationId, cancellationToken)) return NotFound();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into conversation_message_reads(message_id,user_id,read_at)
            select m.id,@userId,@now from conversation_messages m where m.conversation_id=@conversationId and m.deleted_at is null
            on conflict(message_id,user_id) do update set read_at=excluded.read_at;
            update conversation_participants set last_read_at=@now where conversation_id=@conversationId and user_id=@userId;
            """;
        Add(command, "userId", userId); Add(command, "now", now); Add(command, "conversationId", conversationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return NoContent();
    }

    private Guid RequireUserId() => _user.UserId ?? throw new UnauthorizedAccessException("Authenticated user id is required.");

    private async Task<bool> CanAccessConversationAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select exists(
                select 1 from conversations c
                join conversation_participants p on p.conversation_id=c.id
                where c.id=@conversationId and c.organization_id=@organizationId and p.user_id=@userId and p.left_at is null)
            """;
        Add(command, "conversationId", conversationId); Add(command, "organizationId", _tenant.OrganizationId); Add(command, "userId", userId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<bool> MessageBelongsToConversationAsync(Guid messageId, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select exists(select 1 from conversation_messages where id=@messageId and conversation_id=@conversationId and deleted_at is null)";
        Add(command, "messageId", messageId); Add(command, "conversationId", conversationId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<Guid?> GetConversationServiceUserIdAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select service_user_id from conversations where id=@conversationId and organization_id=@organizationId";
        Add(command, "conversationId", conversationId); Add(command, "organizationId", _tenant.OrganizationId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : (Guid)result;
    }

    private async Task<bool> FamilyCanMessageAsync(Guid serviceUserId, CancellationToken cancellationToken)
    {
        if (!_user.IsFamilyMember || _user.FamilyMemberId is null) return !_user.IsFamilyMember;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select exists(
                select 1 from family_access_grants g
                join family_access_permissions p on p.access_grant_id=g.id
                where g.organization_id=@organizationId and g.family_member_id=@familyMemberId and g.service_user_id=@serviceUserId
                  and g.verification_status='Verified' and g.access_status='Active'
                  and (g.valid_from is null or g.valid_from<=now()) and (g.valid_until is null or g.valid_until>now())
                  and p.permission='MessageCareTeam')
            """;
        Add(command, "organizationId", _tenant.OrganizationId); Add(command, "familyMemberId", _user.FamilyMemberId.Value); Add(command, "serviceUserId", serviceUserId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

public sealed record CreateConversationRequest(Guid? ServiceUserId, string Subject, IReadOnlyCollection<Guid> ParticipantUserIds);
public sealed record SendMessageRequest(string Body, Guid? ReplyToMessageId, IReadOnlyCollection<Guid> DocumentIds);
public sealed record ConversationSummaryDto(Guid Id, Guid? ServiceUserId, string Subject, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string LastMessage, long UnreadCount);
public sealed record MessageDto(Guid Id, Guid SenderUserId, string Body, Guid? ReplyToMessageId, DateTimeOffset SentAt, DateTimeOffset? EditedAt, long ReadCount);
