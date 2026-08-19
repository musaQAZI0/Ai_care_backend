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

        if (!await CanAccessAsync(connection, conversationId, userId.Value, cancellationToken)) return NotFound();

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

    private async Task<bool> CanAccessAsync(DbConnection connection, Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select exists(
                select 1 from conversations c
                join conversation_participants p on p.conversation_id = c.id
                where c.id = @conversationId and c.organization_id = @organizationId
                  and p.user_id = @userId and p.left_at is null)
            """;
        Add(command, "conversationId", conversationId);
        Add(command, "organizationId", _tenant.OrganizationId);
        Add(command, "userId", userId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

public sealed record MessageAttachmentDto(Guid MessageId, Guid DocumentId, string FileName, string Category);
