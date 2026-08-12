namespace AiCare.Domain;

public enum UserRole
{
    ServiceUser,
    FamilyMember,
    CareWorker,
    CareCoordinator,
    CareManager,
    Administrator,
    BackOffice
}

public enum RiskLevel
{
    Low,
    Medium,
    High
}

public enum VisitStatus
{
    Scheduled,
    InProgress,
    Completed,
    Cancelled,
    Missed
}

public enum MessagePriority
{
    Routine,
    Medium,
    High
}
