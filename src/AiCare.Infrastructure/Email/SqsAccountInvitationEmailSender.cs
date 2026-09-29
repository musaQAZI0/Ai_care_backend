using System.Text.Json;
using AiCare.Application.Accounts;
using AiCare.Application.Email;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;

namespace AiCare.Infrastructure.Email;
public sealed class SqsAccountInvitationEmailSender(IAmazonSQS sqs,IConfiguration configuration):IAccountInvitationEmailSender
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web);
    public async Task SendAsync(AccountInvitationEmailRequest request,CancellationToken ct)
    {
        if(!configuration.GetValue<bool>("EmailQueue:Enabled"))throw new InvalidOperationException("Email queue is disabled.");
        var queueUrl=configuration["EmailQueue:QueueUrl"]??throw new InvalidOperationException("EmailQueue:QueueUrl must be configured.");
        var job=new EmailJobV1(1,request.InvitationId,$"account-invitation:{request.TenantId:N}:{request.InvitationId:N}",request.TenantId,"account-invitation",request.RecipientName,request.RecipientEmail,request.ActivationUrl,request.ExpiresAtUtc,DateTimeOffset.UtcNow,request.InvitationId.ToString("N"),request.Role);
        await sqs.SendMessageAsync(new SendMessageRequest{QueueUrl=queueUrl,MessageBody=JsonSerializer.Serialize(job,JsonOptions),MessageAttributes=new Dictionary<string,MessageAttributeValue>{{"schemaVersion",new(){DataType="Number",StringValue="1"}},{"emailType",new(){DataType="String",StringValue=job.EmailType}},{"tenantId",new(){DataType="String",StringValue=job.TenantId.ToString("N")}}}},ct);
    }
}
