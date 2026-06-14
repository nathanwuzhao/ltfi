using System;
using CommunityToolkit.Mvvm.ComponentModel;
using LTFI.Models;

namespace LTFI.ViewModels;

public partial class TaskItemViewModel(TaskItem task) : ViewModelBase
{
    public TaskItem Task { get; } = task;

    public Guid Id => Task.Id;

    public string Title => Task.Title;

    public string Description => Task.Description ?? "No details yet.";

    public TaskPriority Priority => Task.Priority;

    public TaskStatus Status => Task.Status;

    public int RequiredWorkSeconds => Task.RequiredWorkSeconds;

    public int AccumulatedWorkSeconds => Task.AccumulatedWorkSeconds;

    public int PointsValue => Task.PointsValue;

    public DateTime? DueDate => Task.DueDate;

    public bool IsCompleted => Task.Status == TaskStatus.Completed;

    public bool CanComplete => !IsCompleted && Task.CanBeCompleted;

    public string ProgressText =>
        RequiredWorkSeconds <= 0
            ? "No required focus time"
            : $"{FormatDuration(AccumulatedWorkSeconds)} / {FormatDuration(RequiredWorkSeconds)}";

    public string DueDateText =>
        DueDate is null ? "No due date" : DueDate.Value.ToString("MMM d");

    public string StatusText => Status.ToString();

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Priority));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(RequiredWorkSeconds));
        OnPropertyChanged(nameof(AccumulatedWorkSeconds));
        OnPropertyChanged(nameof(PointsValue));
        OnPropertyChanged(nameof(DueDate));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(CanComplete));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(DueDateText));
        OnPropertyChanged(nameof(StatusText));
    }

    private static string FormatDuration(int totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(totalSeconds);

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{duration.Minutes}m";
    }
}
