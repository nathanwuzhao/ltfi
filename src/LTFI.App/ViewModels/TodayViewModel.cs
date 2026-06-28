using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.ViewModels;

/// <summary>
/// The daily cockpit: the active focus session, tasks due today, and a small factual summary
/// (points earned today, focus streak). Enriched in Phase 2.
/// </summary>
public partial class TodayViewModel : ViewModelBase, IRefreshable
{
    private readonly ITaskService _taskService;
    private readonly IProjectService _projectService;
    private readonly IFocusSessionService _focus;
    private readonly IInsightsService _insights;

    /// <summary>Raised by the quick-start button so the shell can navigate to the Focus page.</summary>
    public event EventHandler? StartFocusRequested;

    public ObservableCollection<TaskItem> TodayTasks { get; } = [];

    public string Header => "Today";

    [ObservableProperty] private int activeProjectCount;
    [ObservableProperty] private bool hasTasks;
    [ObservableProperty] private int pointsToday;
    [ObservableProperty] private int focusStreakDays;

    [ObservableProperty] private bool hasActiveSession;
    [ObservableProperty] private string activeSessionText = string.Empty;

    public TodayViewModel(
        ITaskService taskService,
        IProjectService projectService,
        IFocusSessionService focus,
        IInsightsService insights)
    {
        _taskService = taskService;
        _projectService = projectService;
        _focus = focus;
        _insights = insights;
    }

    public string SummaryText =>
        $"{TodayTasks.Count} task(s) due today · {ActiveProjectCount} active project(s)";

    public async Task RefreshAsync()
    {
        var tasks = await _taskService.GetTodayAsync();
        TodayTasks.Clear();
        foreach (var task in tasks)
        {
            TodayTasks.Add(task);
        }
        HasTasks = TodayTasks.Count > 0;

        var projects = await _projectService.GetAllAsync();
        ActiveProjectCount = projects.Count(p => p.Status == ProjectStatus.Active);

        var snapshot = await _insights.GetTodaySnapshotAsync();
        PointsToday = snapshot.PointsToday;
        FocusStreakDays = snapshot.FocusStreakDays;

        UpdateActiveSession();
        OnPropertyChanged(nameof(SummaryText));
    }

    private void UpdateActiveSession()
    {
        var active = _focus.GetActiveSnapshot();
        if (active is null)
        {
            HasActiveSession = false;
            ActiveSessionText = string.Empty;
            return;
        }

        HasActiveSession = true;
        var what = active.TaskTitle ?? active.Intent ?? "Focused work";
        var state = active.Status == FocusSessionStatus.Active ? "running" : "paused";
        ActiveSessionText = $"{what} — {(int)active.Elapsed.TotalMinutes}m ({state})";
    }

    [RelayCommand]
    private void StartFocus() => StartFocusRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task CompleteTaskAsync(TaskItem? task)
    {
        if (task is null)
        {
            return;
        }

        await _taskService.SetStatusAsync(task.Id, TaskStatus.Completed);
        await RefreshAsync();
    }
}
