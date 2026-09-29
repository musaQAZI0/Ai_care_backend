using System.Security.Cryptography;
using System.Text;
using AiCare.Application.Email;
using AiCare.Domain;

namespace AiCare.Application.Accounts;

public sealed record CreateAccountInvitationCommand(string UserName, string Email, UserRole Role, Guid? BranchId, Guid? CareWorkerId);
public sealed record AccountInvitationResult(Guid InvitationId, Guid UserId, string Status, DateTimeOffset ExpiresAt);
public sealed record AccountInvitationValidation(bool Valid, string Status, string Message, string? UserName, string? ProviderName, string? Role, DateTimeOffset? ExpiresAt);
public interface IAccountInvitationStore
{
    Task<AccountInvitationResult> CreateAsync(Guid organizationId, Guid? actorBranchId, Guid actorUserId, string actorName, CreateAccountInvitationCommand command, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct);
    Task MarkQueueFailedAsync(Guid invitationId, string reason, CancellationToken ct);
    Task<AccountInvitationValidation> ValidateAsync(string tokenHash, DateTimeOffset now, CancellationToken ct);
    Task AcceptAsync(string tokenHash, string password, DateTimeOffset now, CancellationToken ct);
}
public interface IAccountInvitationEmailSender { Task SendAsync(AccountInvitationEmailRequest request, CancellationToken ct); }
public interface IAccountInvitationService
{
    Task<AccountInvitationResult> CreateAsync(Guid organizationId, Guid? branchId, Guid actorUserId, string actorName, CreateAccountInvitationCommand command, string frontendBaseUrl, CancellationToken ct);
    Task<AccountInvitationValidation> ValidateAsync(string token, CancellationToken ct);
    Task AcceptAsync(string token, string password, bool acceptTerms, CancellationToken ct);
}

public sealed class AccountInvitationService(IAccountInvitationStore store, IAccountInvitationEmailSender emailSender) : IAccountInvitationService
{
    private static readonly HashSet<UserRole> StaffRoles = [UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator, UserRole.CareWorker, UserRole.BackOffice];
    public async Task<AccountInvitationResult> CreateAsync(Guid organizationId, Guid? branchId, Guid actorUserId, string actorName, CreateAccountInvitationCommand command, string frontendBaseUrl, CancellationToken ct)
    {
        if (!StaffRoles.Contains(command.Role)) throw new InvalidOperationException("Use the governed recipient invitation workflow for family and service-user accounts.");
        if (string.IsNullOrWhiteSpace(command.UserName) || string.IsNullOrWhiteSpace(command.Email)) throw new InvalidOperationException("Username and email are required.");
        if (!System.Net.Mail.MailAddress.TryCreate(command.Email.Trim(), out _)) throw new InvalidOperationException("A valid email is required.");
        if (command.Role == UserRole.CareWorker && command.CareWorkerId is null) throw new InvalidOperationException("Care worker accounts must be linked to a care worker profile.");
        var normalized = command with { UserName = command.UserName.Trim(), Email = command.Email.Trim().ToLowerInvariant() };
        var rawToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTimeOffset.UtcNow.AddHours(48);
        var invitation = await store.CreateAsync(organizationId, branchId, actorUserId, actorName, normalized, Hash(rawToken), expiresAt, ct);
        var activationUrl = $"{frontendBaseUrl.TrimEnd('/')}/staff/activate?token={Uri.EscapeDataString(rawToken)}";
        try { await emailSender.SendAsync(new AccountInvitationEmailRequest(invitation.InvitationId, organizationId, normalized.UserName, normalized.Email, activationUrl, expiresAt, normalized.Role.ToString()), ct); }
        catch (Exception ex) { await store.MarkQueueFailedAsync(invitation.InvitationId, ex.GetType().Name, ct); throw new InvalidOperationException("The account remains inactive because its invitation could not be queued."); }
        return invitation;
    }
    public Task<AccountInvitationValidation> ValidateAsync(string token, CancellationToken ct) => string.IsNullOrWhiteSpace(token) ? Task.FromResult(new AccountInvitationValidation(false,"Invalid","Invitation token is required.",null,null,null,null)) : store.ValidateAsync(Hash(token),DateTimeOffset.UtcNow,ct);
    public Task AcceptAsync(string token,string password,bool acceptTerms,CancellationToken ct) { if(string.IsNullOrWhiteSpace(token))throw new InvalidOperationException("Invitation token is required."); if(!acceptTerms)throw new InvalidOperationException("Terms must be accepted before activation."); ValidatePassword(password); return store.AcceptAsync(Hash(token),password,DateTimeOffset.UtcNow,ct); }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Base64Url(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static void ValidatePassword(string password) { if(password.Length<12||!password.Any(char.IsUpper)||!password.Any(char.IsLower)||!password.Any(char.IsDigit)||!password.Any(ch=>!char.IsLetterOrDigit(ch)))throw new InvalidOperationException("Password must contain at least 12 characters, upper and lower case letters, a number, and a symbol."); }
}
