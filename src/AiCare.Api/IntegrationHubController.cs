using System.Data;
using System.Data.Common;
using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Roles = "Administrator,BackOffice")]
[Route("api/phase1/integrations")]
public sealed class IntegrationHubController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken token) => Ok(new
    {
        connectors = await Count("integration_connectors", null, token),
        pendingJobs = await Count("integration_jobs", "status='Pending'", token),
        successfulJobs = await Count("integration_jobs", "status='Succeeded'", token),
        failedJobs = await Count("integration_jobs", "status='Failed'", token),
        webhookEvents = await Count("integration_webhook_events", null, token),
        unresolvedFailures = await Count("integration_sync_failures", "status='Pending'", token),
        connectorItems = await Connectors(token), jobs = await Jobs(token), webhookEventItems = await Events(token), failures = await Failures(token)
    });

    [HttpGet("connectors")]
    public async Task<IActionResult> ListConnectors(CancellationToken token) => Ok(await Connectors(token));

    [HttpPost("connectors")]
    public async Task<IActionResult> CreateConnector(CreateIntegrationConnectorRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ConnectorType)) return BadRequest(new { message = "Name and connector type are required." });
        var id = Guid.NewGuid();
        await Exec("insert into integration_connectors(id,organization_id,branch_id,name,connector_type,endpoint_url,status,configuration_json,created_by) values(@id,@organization,@branch,@name,@type,@endpoint,'Active',cast(@configuration as jsonb),@actor)", command =>
        { Add(command,"id",id); Add(command,"name",request.Name.Trim()); Add(command,"type",request.ConnectorType.Trim()); Add(command,"endpoint",request.EndpointUrl?.Trim() ?? ""); Add(command,"configuration",JsonSerializer.Serialize(request.Configuration ?? new())); }, token);
        await Audit("connector.created", "IntegrationConnector", id, new { request.Name, request.ConnectorType }, token);
        return Created($"/api/phase1/integrations/connectors/{id}", new { id, name=request.Name.Trim(), connectorType=request.ConnectorType.Trim(), status="Active" });
    }

    [HttpPost("jobs/import")]
    public Task<IActionResult> Import(CreateIntegrationJobRequest request, CancellationToken token) => CreateJob("Import", request, token);

    [HttpPost("jobs/export")]
    public Task<IActionResult> Export(CreateIntegrationJobRequest request, CancellationToken token) => CreateJob("Export", request, token);

    [HttpPost("webhooks/{connectorId:guid}")]
    public async Task<IActionResult> Webhook(Guid connectorId, ReceiveIntegrationWebhookRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.EventType)) return BadRequest(new { message = "Event type is required." });
        if (!await ConnectorExists(connectorId, token)) return NotFound(new { message = "Connector not found." });
        var id=Guid.NewGuid();
        try
        {
            await Exec("insert into integration_webhook_events(id,connector_id,organization_id,branch_id,event_type,external_event_id,status,payload_json) values(@id,@connector,@organization,@branch,@type,@external,'Received',cast(@payload as jsonb))", c => { Add(c,"id",id);Add(c,"connector",connectorId);Add(c,"type",request.EventType.Trim());Add(c,"external",request.ExternalEventId);Add(c,"payload",JsonSerializer.Serialize(request.Payload ?? new()));}, token);
        }
        catch (DbException) when (!string.IsNullOrWhiteSpace(request.ExternalEventId)) { return Conflict(new { message="Webhook event has already been received." }); }
        await Audit("webhook.received", "IntegrationWebhookEvent", id, new { connectorId, request.EventType }, token);
        return Accepted(new { id, status="Received" });
    }

    [HttpGet("failures")]
    public async Task<IActionResult> ListFailures(CancellationToken token) => Ok(await Failures(token));

    [HttpPost("failures/{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken token)
    {
        var changed=await Exec("update integration_sync_failures set status='Resolved',retry_count=retry_count+1,last_retried_at=now(),resolved_at=now() where id=@id and organization_id=@organization and status='Pending'", c=>Add(c,"id",id), token);
        if(changed==0)return NotFound(new { message="Pending failure not found." });
        await Audit("sync_failure.retried", "IntegrationSyncFailure", id, new { result="Resolved" }, token);
        return Ok(new { id, status="Resolved" });
    }

    private async Task<IActionResult> CreateJob(string direction, CreateIntegrationJobRequest request, CancellationToken token)
    {
        if (request.ConnectorId==Guid.Empty || string.IsNullOrWhiteSpace(request.ResourceType)) return BadRequest(new { message="Connector and resource type are required." });
        if (!await ConnectorExists(request.ConnectorId, token)) return NotFound(new { message="Connector not found." });
        var id=Guid.NewGuid(); var failed=request.SimulateFailure; var status=failed?"Failed":"Pending";
        await Exec("insert into integration_jobs(id,connector_id,organization_id,branch_id,direction,status,resource_type,requested_by,error_message) values(@id,@connector,@organization,@branch,@direction,@status,@resource,@actor,@error)",c=>{Add(c,"id",id);Add(c,"connector",request.ConnectorId);Add(c,"direction",direction);Add(c,"status",status);Add(c,"resource",request.ResourceType.Trim());Add(c,"error",failed?"Simulated sync failure":null);},token);
        if(failed)await Exec("insert into integration_sync_failures(id,connector_id,job_id,organization_id,branch_id,operation,error_message,payload_json,status) values(@failure,@connector,@id,@organization,@branch,@direction,'Simulated sync failure',cast(@payload as jsonb),'Pending')",c=>{Add(c,"failure",Guid.NewGuid());Add(c,"connector",request.ConnectorId);Add(c,"id",id);Add(c,"direction",direction);Add(c,"payload",JsonSerializer.Serialize(request.Options??new()));},token);
        await Audit($"{direction.ToLowerInvariant()}_job.created", "IntegrationJob", id, new { request.ConnectorId, request.ResourceType, status }, token);
        return Created($"/api/phase1/integrations/jobs/{id}",new{id,direction,status});
    }

    private Task<List<ConnectorResponse>> Connectors(CancellationToken t)=>Query("select id,name,connector_type,endpoint_url,status,created_by,created_at from integration_connectors where organization_id=@organization order by created_at desc limit 100",_=>{},r=>new ConnectorResponse(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetFieldValue<DateTimeOffset>(6)),t);
    private Task<List<JobResponse>> Jobs(CancellationToken t)=>Query("select id,connector_id,direction,status,resource_type,requested_by,record_count,error_message,created_at,completed_at from integration_jobs where organization_id=@organization order by created_at desc limit 100",_=>{},r=>new JobResponse(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt32(6),r.IsDBNull(7)?null:r.GetString(7),r.GetFieldValue<DateTimeOffset>(8),r.IsDBNull(9)?null:r.GetFieldValue<DateTimeOffset>(9)),t);
    private Task<List<WebhookResponse>> Events(CancellationToken t)=>Query("select id,connector_id,event_type,external_event_id,status,received_at from integration_webhook_events where organization_id=@organization order by received_at desc limit 100",_=>{},r=>new WebhookResponse(r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.GetString(4),r.GetFieldValue<DateTimeOffset>(5)),t);
    private Task<List<FailureResponse>> Failures(CancellationToken t)=>Query("select id,connector_id,job_id,operation,error_message,status,retry_count,created_at,last_retried_at from integration_sync_failures where organization_id=@organization order by created_at desc limit 100",_=>{},r=>new FailureResponse(r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt32(6),r.GetFieldValue<DateTimeOffset>(7),r.IsDBNull(8)?null:r.GetFieldValue<DateTimeOffset>(8)),t);
    private async Task<bool> ConnectorExists(Guid id,CancellationToken t)=>await Scalar("select count(*) from integration_connectors where id=@id and organization_id=@organization",c=>Add(c,"id",id),t)>0;
    private Task<int> Count(string table,string? predicate,CancellationToken t)=>Scalar($"select count(*) from {table} where organization_id=@organization{(predicate is null?"":$" and {predicate}")}",_=>{},t);
    private async Task<int> Scalar(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);return Convert.ToInt32(await c.ExecuteScalarAsync(t));}
    private async Task<int> Exec(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);return await c.ExecuteNonQueryAsync(t);}
    private async Task<List<T>> Query<T>(string sql,Action<DbCommand> bind,Func<DbDataReader,T> map,CancellationToken t){var items=new List<T>();await using var c=await Command(sql,t);bind(c);await using var r=await c.ExecuteReaderAsync(t);while(await r.ReadAsync(t))items.Add(map(r));return items;}
    private async Task<DbCommand> Command(string sql,CancellationToken t){var cn=db.Database.GetDbConnection();if(cn.State!=ConnectionState.Open)await cn.OpenAsync(t);var c=cn.CreateCommand();c.CommandText=sql;Add(c,"organization",tenant.OrganizationId);Add(c,"branch",tenant.BranchId??TenantDefaults.BranchId);Add(c,"actor",user.UserName);return c;}
    private async Task Audit(string action,string entity,Guid? id,object detail,CancellationToken t){await Exec("insert into integration_audit_history(id,organization_id,branch_id,action,actor,entity_type,entity_id,detail_json) values(@audit,@organization,@branch,@action,@actor,@entity,@entityId,cast(@detail as jsonb))",c=>{Add(c,"audit",Guid.NewGuid());Add(c,"action",action);Add(c,"entity",entity);Add(c,"entityId",id);Add(c,"detail",JsonSerializer.Serialize(detail));},t);db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,entity,id,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));await db.SaveChangesAsync(t);}
    private static void Add(DbCommand c,string n,object? v){if(c.Parameters.Contains(n))return;var p=c.CreateParameter();p.ParameterName=n;p.Value=v??DBNull.Value;c.Parameters.Add(p);}
}

public sealed record CreateIntegrationConnectorRequest(string Name,string ConnectorType,string? EndpointUrl,Dictionary<string,string>? Configuration);
public sealed record CreateIntegrationJobRequest(Guid ConnectorId,string ResourceType,Dictionary<string,string>? Options,bool SimulateFailure=false);
public sealed record ReceiveIntegrationWebhookRequest(string EventType,string? ExternalEventId,Dictionary<string,object>? Payload);
public sealed record ConnectorResponse(Guid Id,string Name,string ConnectorType,string EndpointUrl,string Status,string CreatedBy,DateTimeOffset CreatedAt);
public sealed record JobResponse(Guid Id,Guid ConnectorId,string Direction,string Status,string ResourceType,string RequestedBy,int RecordCount,string? ErrorMessage,DateTimeOffset CreatedAt,DateTimeOffset? CompletedAt);
public sealed record WebhookResponse(Guid Id,Guid? ConnectorId,string EventType,string? ExternalEventId,string Status,DateTimeOffset ReceivedAt);
public sealed record FailureResponse(Guid Id,Guid? ConnectorId,Guid? JobId,string Operation,string ErrorMessage,string Status,int RetryCount,DateTimeOffset CreatedAt,DateTimeOffset? LastRetriedAt);
