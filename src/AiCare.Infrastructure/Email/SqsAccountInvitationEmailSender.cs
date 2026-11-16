using System.Text.Json;
using AiCare.Application.Accounts;
using AiCare.Application.Email;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;

namespace AiCare.Infrastructure.Email;
public sealed class SqsAccountInvitationEmailSender(IAmazonSQS sqs,IConfiguration configuration,IResendEmailClient resend):IAccountInvitationEmailSender
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web);
    public void ValidateConfiguration()
    {
        if (!configuration.GetValue<bool>("Email:Enabled"))
            throw new AccountInvitationDeliveryException("Invitation email is disabled. Ask the operator to set Email__Enabled=true after configuring delivery.");
        if (string.IsNullOrWhiteSpace(configuration["Resend:ApiKey"]))
            throw new AccountInvitationDeliveryException("Invitation email is unavailable. Ask the operator to configure Resend__ApiKey.");
        if (!System.Net.Mail.MailAddress.TryCreate(configuration["Resend:FromAddress"], out _))
            throw new AccountInvitationDeliveryException("Invitation email is unavailable. Ask the operator to configure Resend__FromAddress using a verified sender.");
        if (configuration.GetValue<bool>("EmailQueue:Enabled") &&
            (!Uri.TryCreate(configuration["EmailQueue:QueueUrl"], UriKind.Absolute, out var queue) || queue.Scheme != "https"))
            throw new AccountInvitationDeliveryException("Invitation email queue is unavailable. Ask the operator to configure EmailQueue__QueueUrl or explicitly use direct delivery.");
    }

    public async Task SendAsync(AccountInvitationEmailRequest request,CancellationToken ct)
    {
        ValidateConfiguration();
        var job=new EmailJobV1(1,request.InvitationId,$"account-invitation:{request.TenantId:N}:{request.InvitationId:N}",request.TenantId,"account-invitation",request.RecipientName,request.RecipientEmail,request.ActivationUrl,request.ExpiresAtUtc,DateTimeOffset.UtcNow,request.InvitationId.ToString("N"),request.Role);
        if(!configuration.GetValue<bool>("EmailQueue:Enabled")){await resend.SendAsync(job,ct);return;}
        var queueUrl=configuration["EmailQueue:QueueUrl"]??throw new InvalidOperationException("EmailQueue:QueueUrl must be configured.");
        await sqs.SendMessageAsync(new SendMessageRequest{QueueUrl=queueUrl,MessageBody=JsonSerializer.Serialize(job,JsonOptions),MessageAttributes=new Dictionary<string,MessageAttributeValue>{{"schemaVersion",new(){DataType="Number",StringValue="1"}},{"emailType",new(){DataType="String",StringValue=job.EmailType}},{"tenantId",new(){DataType="String",StringValue=job.TenantId.ToString("N")}}}},ct);
    }
}
