namespace AiCare.Domain;

public sealed record Conversation(
    Guid Id,
    Guid ServiceUserId,
    string Subject,
    string Status,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid OrganizationId,
    Guid? BranchId = null);

public sealed record ConversationParticipant(
    Guid ConversationId,
    Guid UserId,
    DateTimeOffset JoinedAt,
    DateTimeOffset? LeftAt,
    DateTimeOffset? LastReadAt,
    Guid OrganizationId);

public sealed record ConversationMessage(
    Guid Id,
    Guid ConversationId,
    Guid SenderUserId,
    string Body,
    DateTimeOffset SentAt,
    DateTimeOffset? EditedAt,
    Guid? ReplyToMessageId,
    Guid OrganizationId);

public sealed record MessageAttachment(
    Guid MessageId,
    Guid DocumentId,
    Guid OrganizationId);
