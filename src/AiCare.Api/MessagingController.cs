using System.Data.Common;
using AiCare.Application;
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
              and (
                    not @isFamilyMember
                    or (
                        c.service_user_id is not null
                        and exists(
                            select 1
                            from family_access_grants g
                            join family_access_permissions fp on fp.access_grant_id=g.id
                            where g.organization_id=@organizationId
                              and g.family_member_id=@familyMemberId
                              and g.service_user_id=c.service_user_id
                              and g.verification_status='Verified'
                              and g.access_status='Active'
                              and (g.valid_from is null or g.valid_from<=now())
                              and (g.valid_until is null or g.valid_until>now())
                              and fp.permission='MessageCareTeam'
                        )
                    )
                  )
            order by c.updated_at desc
            """;
        Add(command, "userId", userId);
        Add(command, "organizationId", _tenant.OrganizationId);
        Add(command, "isFamilyMember", _user.IsFamilyMember);
        Add(command, "familyMemberId", _user.FamilyMemberId ?? Guid.Empty);
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
        await reader.DisposeAsync();
        var visible = new List<ConversationSummaryDto>();
        foreach(var row in rows) if(await CanAccessConversationAsync(row.Id,cancellationToken)) visible.Add(row);
        return Ok(visible);
    }

    [HttpPost("conversations")]
    public async Task<IActionResult> CreateConversation(CreateConversationRequest request, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        if (string.IsNullOrWhiteSpace(request.Subject)) return BadRequest(new { message = "Subject is required." });
        if (request.Subject.Trim().Length > 200) return BadRequest(new { message = "Subject is too long." });

        var access=new MessagingAccess(_db,_tenant,_user);
        var actor=await access.Actor(cancellationToken);
        if(actor is null)return Unauthorized();
        if(request.ServiceUserId is Guid person && !await access.Person(actor,person,cancellationToken))return NotFound();
        if(request.ServiceUserId is null && actor.Role is UserRole.FamilyMember or UserRole.ServiceUser)
            return BadRequest(new {message="A service user is required."});
        var participants=(request.ParticipantUserIds??[]).Append(userId).Distinct().ToArray();
        if(participants.Length>50)return BadRequest(new {message="Too many participants."});
        var allowed=await access.Directory(request.ServiceUserId,cancellationToken);
        if(participants.Any(id=>id!=userId&&!allowed.Any(x=>x.Id==id)))
            return BadRequest(new {message="Participants must belong to the authorized branch or care team."});
        var conversationBranch=request.ServiceUserId is Guid servicePerson
            ? await _db.ServiceUsers.Where(x=>x.Id==servicePerson).Select(x=>x.BranchId).SingleAsync(cancellationToken)
            : actor.BranchId;

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
            Add(command, "branchId", conversationBranch);
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
                   coalesce((select count(*) from conversation_message_reads r where r.message_id=m.id and r.user_id<>m.sender_user_id),0),
                   m.classification,case when m.delivery_status='Read' then 'Delivered' else m.delivery_status end,m.failure_reason,m.retry_count,m.retention_until,m.legal_hold,exists(select 1 from conversation_message_reads r where r.message_id=m.id and r.user_id=@readerId)
            from conversation_messages m
            where m.conversation_id=@conversationId and m.deleted_at is null
            order by m.sent_at
            """;
        Add(command, "conversationId", conversationId);
        Add(command, "readerId", RequireUserId());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new MessageDto(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5), reader.GetInt64(6),
                reader.GetString(7),reader.GetString(8),reader.GetString(9),reader.GetInt32(10),reader.IsDBNull(11)?null:reader.GetFieldValue<DateTimeOffset>(11),reader.GetBoolean(12),reader.GetBoolean(13)));
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
        var classification=string.IsNullOrWhiteSpace(request.Classification)?"Routine":request.Classification.Trim();
        if(classification is not("Routine" or "Sensitive" or "Urgent"))return BadRequest(new{message="Classification must be Routine, Sensitive, or Urgent."});
        if (request.ReplyToMessageId is not null && !await MessageBelongsToConversationAsync(request.ReplyToMessageId.Value, conversationId, cancellationToken))
            return BadRequest(new { message = "Reply target is invalid." });

        var attachmentIds = request.DocumentIds.Distinct().ToArray();
        if (attachmentIds.Length > 10) return BadRequest(new { message = "A message can contain at most 10 attachments." });
        if (attachmentIds.Length > 0)
        {
            var access=new MessagingAccess(_db,_tenant,_user);
            var actor=await access.Actor(cancellationToken);
            var person=await GetConversationServiceUserIdAsync(conversationId,cancellationToken);
            if(actor is null)return Unauthorized();
            foreach(var documentId in attachmentIds)
                if(!await access.Document(actor,documentId,person,cancellationToken))return BadRequest(new {message="An attachment is not authorized for this conversation."});
            // Sending an attachment must not disclose an internal document to a family recipient.
            await using var recipientsConnection=await OpenAsync(cancellationToken);
            await using var recipientsCommand=recipientsConnection.CreateCommand();
            recipientsCommand.CommandText="select user_id from conversation_participants where conversation_id=@conversation and left_at is null";
            Add(recipientsCommand,"conversation",conversationId);
            var recipientIds=new List<Guid>();
            await using(var reader=await recipientsCommand.ExecuteReaderAsync(cancellationToken))
                while(await reader.ReadAsync(cancellationToken))recipientIds.Add(reader.GetGuid(0));
            var recipients=await _db.AppUsers.AsNoTracking().Where(x=>recipientIds.Contains(x.Id)&&x.IsActive).ToListAsync(cancellationToken);
            foreach(var recipient in recipients)
                foreach(var documentId in attachmentIds)
                    if(!await access.Document(recipient,documentId,person,cancellationToken))return BadRequest(new {message="An attachment is not shared with every recipient."});
        }

        var messageId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "insert into conversation_messages(id,conversation_id,sender_user_id,body,reply_to_message_id,sent_at,classification,delivery_status,retention_until) values(@id,@conversationId,@sender,@body,@replyTo,@now,@classification,'Delivered',@retention);insert into conversation_message_events(id,message_id,conversation_id,organization_id,event_type,detail,actor,occurred_at) values(@eventId,@id,@conversationId,@organizationId,'Delivered','Internal in-app delivery completed',@actor,@now)";
            Add(command, "id", messageId); Add(command, "conversationId", conversationId); Add(command, "sender", userId);
            Add(command, "body", body); Add(command, "replyTo", request.ReplyToMessageId); Add(command, "now", now);
            Add(command,"classification",classification);Add(command,"retention",request.RetentionUntil);Add(command,"eventId",Guid.NewGuid());Add(command,"organizationId",_tenant.OrganizationId);Add(command,"actor",_user.UserName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if(classification=="Urgent")
        {
            await using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="insert into conversation_escalations(id,conversation_id,message_id,organization_id,branch_id,severity,status,owner,response_due_at,reason) values(@id,@conversation,@message,@organization,@branch,'High','Open','Care team',@due,'Urgent family/care-team message');";Add(command,"id",Guid.NewGuid());Add(command,"conversation",conversationId);Add(command,"message",messageId);Add(command,"organization",_tenant.OrganizationId);Add(command,"branch",_tenant.BranchId??TenantDefaults.BranchId);Add(command,"due",now.AddHours(4));await command.ExecuteNonQueryAsync(cancellationToken);
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
        await using(var command=connection.CreateCommand())
        {
            command.Transaction=transaction;
            command.CommandText="""
                insert into "Notifications"("Id","Title","Detail","CreatedAt","IsRead","OrganizationId","BranchId")
                select @notification,'New secure message','Open your authorized messaging inbox to view messages.',@now,false,organization_id,branch_id from conversations where id=@conversation;
                insert into "AuditEvents"("Id","Action","Actor","EntityType","EntityId","CreatedAt","OrganizationId","BranchId")
                select @audit,'messaging.message_delivered',@actor,'ConversationMessage',@message,@now,organization_id,branch_id from conversations where id=@conversation;
                """;
            Add(command,"notification",Guid.NewGuid());Add(command,"audit",Guid.NewGuid());Add(command,"actor",_user.UserName);
            Add(command,"message",messageId);Add(command,"now",now);Add(command,"conversation",conversationId);
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
        await using var transaction=await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction=transaction;
        command.CommandText = """
            insert into conversation_message_reads(message_id,user_id,read_at)
            select m.id,@userId,@now from conversation_messages m where m.conversation_id=@conversationId and m.deleted_at is null
            on conflict(message_id,user_id) do nothing;
            update conversation_participants set last_read_at=@now where conversation_id=@conversationId and user_id=@userId;
            """;
        Add(command, "userId", userId); Add(command, "now", now); Add(command, "conversationId", conversationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private Guid RequireUserId() => _user.UserId ?? throw new UnauthorizedAccessException("Authenticated user id is required.");

    private Task<bool> CanAccessConversationAsync(Guid conversationId,CancellationToken token)
        => new MessagingAccess(_db,_tenant,_user).Conversation(conversationId,token);

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

    private Task<bool> FamilyCanMessageAsync(Guid serviceUserId, CancellationToken cancellationToken) =>
        FamilyHasPermissionAsync(serviceUserId, "MessageCareTeam", cancellationToken);

    private Task<bool> FamilyCanViewDocumentsAsync(Guid serviceUserId, CancellationToken cancellationToken) =>
        FamilyHasPermissionAsync(serviceUserId, "ViewDocuments", cancellationToken);

    private async Task<bool> FamilyHasPermissionAsync(Guid serviceUserId, string permission, CancellationToken cancellationToken)
    {
        if (!_user.IsFamilyMember) return true;
        if (_user.FamilyMemberId is null) return false;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select exists(
                select 1 from family_access_grants g
                join family_access_permissions p on p.access_grant_id=g.id
                where g.organization_id=@organizationId and g.family_member_id=@familyMemberId and g.service_user_id=@serviceUserId
                  and g.verification_status='Verified' and g.access_status='Active'
                  and (g.valid_from is null or g.valid_from<=now()) and (g.valid_until is null or g.valid_until>now())
                  and p.permission=@permission)
            """;
        Add(command, "organizationId", _tenant.OrganizationId);
        Add(command, "familyMemberId", _user.FamilyMemberId.Value);
        Add(command, "serviceUserId", serviceUserId);
        Add(command, "permission", permission);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
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
}

public sealed record CreateConversationRequest(Guid? ServiceUserId, string Subject, IReadOnlyCollection<Guid> ParticipantUserIds);
public sealed record SendMessageRequest(string Body, Guid? ReplyToMessageId, IReadOnlyCollection<Guid> DocumentIds,string? Classification=null,DateTimeOffset? RetentionUntil=null);
public static class SendMessageRequestExtensions{public static string SubjectOrFallback(this SendMessageRequest request,string body)=>body.Length<=120?body:body[..120];}
public sealed record ConversationSummaryDto(Guid Id, Guid? ServiceUserId, string Subject, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string LastMessage, long UnreadCount);
public sealed record MessageDto(Guid Id, Guid SenderUserId, string Body, Guid? ReplyToMessageId, DateTimeOffset SentAt, DateTimeOffset? EditedAt, long ReadCount,string Classification,string DeliveryStatus,string FailureReason,int RetryCount,DateTimeOffset? RetentionUntil,bool LegalHold,bool IsReadByCurrentUser);
