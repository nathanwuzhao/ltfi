namespace LTFI.Core.Domain;

public enum TaskPriority
{
    Low,
    Medium,
    High,
    Urgent
}

public enum TaskStatus
{
    NotStarted,
    InProgress,
    Paused,
    Completed,
    Skipped
}
