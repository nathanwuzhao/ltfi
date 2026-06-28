namespace LTFI.Core.Domain;

/// <summary>Project lifecycle states (plan §4.2). <c>Killed</c> is an intentional, non-failure end.</summary>
public enum ProjectStatus
{
    InboxIdea,
    Backlog,
    Active,
    Paused,
    Blocked,
    Completed,
    Archived,
    Killed
}

/// <summary>
/// Task lifecycle states. Trimmed from the plan's §4.3 list to the five that carry their
/// weight day to day: Ready (queued), InProgress, Completed, Canceled, Deferred (pushed out).
/// </summary>
public enum TaskStatus
{
    Ready,
    InProgress,
    Completed,
    Canceled,
    Deferred
}

public enum TaskPriority
{
    Low,
    Medium,
    High,
    Urgent
}

public enum MilestoneStatus
{
    Planned,
    InProgress,
    Completed,
    Abandoned
}

/// <summary>Focus session lifecycle states (plan §2.2).</summary>
public enum FocusSessionStatus
{
    Active,
    Paused,
    Completed,
    Abandoned
}

/// <summary>The outcome a user records when finishing a focus session (plan §2.3 review).</summary>
public enum FocusSessionResult
{
    Completed,
    Partial,
    Blocked
}

/// <summary>Evidence item types (plan §4.4). Only manual/task/subtask/focus are produced early.</summary>
public enum EvidenceType
{
    ManualNote,
    FocusSessionCompleted,
    TaskCompleted,
    SubtaskCompleted,
    GitCommit,
    GitHubPullRequest,
    GitHubIssueClosed,
    LogseqJournalEntry,
    FileChanged,
    ReflectionSubmitted,
    DistractionBlocked,
    DistractionOverride,
    CalendarEventCompleted
}

/// <summary>What a <see cref="ReflectionEntry"/> is scoped to (plan §5 ScopeType).</summary>
public enum ReflectionScope
{
    Day,
    Week,
    Project,
    Task
}
