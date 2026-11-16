using AiCare.Application.Accounts;
using AiCare.Application.Email;
using AiCare.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiCare.Tests;

public sealed class AccountInvitationEmailConfigurationTests
{
    [Theory]
    [InlineData("Email:Enabled", "false", "Email__Enabled")]
    [InlineData("Resend:ApiKey", "", "Resend__ApiKey")]
    [InlineData("Resend:FromAddress", "invalid", "Resend__FromAddress")]
    public void MissingDeliverySettingsHaveActionableErrors(string key, string value, string expected)
    {
        var settings = Settings(); settings[key] = value;
        var sender = new SqsAccountInvitationEmailSender(null!, new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new ResendStub());
        var error = Assert.Throws<AccountInvitationDeliveryException>(() => sender.ValidateConfiguration());
        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("test-secret-key", error.Message);
    }

    [Fact]
    public async Task DirectDeliveryUsesResendWithoutAnSqsQueue()
    {
        var resend = new ResendStub();
        var sender = new SqsAccountInvitationEmailSender(null!, new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build(), resend);
        await sender.SendAsync(new AccountInvitationEmailRequest(Guid.NewGuid(), Guid.NewGuid(), "User", "user@example.com", "https://app.example.com/staff/activate?token=test", DateTimeOffset.UtcNow.AddHours(48), "CareCoordinator"), default);
        Assert.Equal(1, resend.Calls);
    }

    private static Dictionary<string, string?> Settings() => new()
    {
        ["Email:Enabled"] = "true", ["EmailQueue:Enabled"] = "false",
        ["Resend:ApiKey"] = "test-secret-key", ["Resend:FromAddress"] = "notifications@example.com"
    };
    private sealed class ResendStub : IResendEmailClient
    {
        public int Calls;
        public Task<string> SendAsync(EmailJobV1 job, CancellationToken cancellationToken) { Calls++; return Task.FromResult("provider-id"); }
        public Task<string> SendMessageAsync(ResendMessage message, CancellationToken cancellationToken) => Task.FromResult("provider-id");
    }
}
