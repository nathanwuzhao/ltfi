using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using Serilog;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.ViewModels;

/// <summary>A task choice for starting a focus session; <c>Id == null</c> means "no task".</summary>
public sealed record TaskOption(Guid? Id, string Title);

/// <summary>
/// The Focus page: pick what to work on, run a timer (start/pause/resume), and record an
/// end-of-session review. Registered as a singleton so the timer survives navigation.
/// </summary>
public partial class FocusViewModel : ViewModelBase, IRefreshable
{
    private readonly IFocusSessionService _focus;
    private readonly IProjectService _projectService;
    private readonly ITaskService _taskService;
    private readonly DispatcherTimer _timer;

    public ObservableCollection<ProjectOption> ProjectOptions { get; } = [];
    public ObservableCollection<TaskOption> TaskOptions { get; } = [];
    public Array Results { get; } = Enum.GetValues<FocusSessionResult>();

    public string Header => "Focus";

    // --- setup (no active session) ---
    [ObservableProperty] private ProjectOption? selectedProjectOption;
    [ObservableProperty] private TaskOption? selectedTaskOption;
    [ObservableProperty] private string intentText = string.Empty;

    // --- active session ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetup), nameof(ShowActive))]
    private bool hasActiveSession;

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string elapsedText = "00:00";
    [ObservableProperty] private string currentObjective = string.Empty;
    [ObservableProperty] private string pauseResumeText = "Pause";

    // --- review ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetup), nameof(ShowActive), nameof(ShowReview))]
    private bool isReviewing;

    [ObservableProperty] private FocusSessionResult selectedResult = FocusSessionResult.Completed;
    [ObservableProperty] private string reviewSummary = string.Empty;
    [ObservableProperty] private string reviewBlocker = string.Empty;
    [ObservableProperty] private string reviewNext = string.Empty;

    [ObservableProperty] private string feedbackMessage = string.Empty;

    public FocusViewModel(IFocusSessionService focus, IProjectService projectService, ITaskService taskService)
    {
        _focus = focus;
        _projectService = projectService;
        _taskService = taskService;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => SyncFromService();
        _timer.Start();

        SyncFromService();
    }

    public bool ShowSetup => !HasActiveSession && !IsReviewing;
    public bool ShowActive => HasActiveSession && !IsReviewing;
    public bool ShowReview => IsReviewing;

    public async Task RefreshAsync()
    {
        var projects = await _projectService.GetAllAsync();
        ProjectOptions.Clear();
        ProjectOptions.Add(new ProjectOption(null, "(No project)"));
        foreach (var p in projects)
        {
            ProjectOptions.Add(new ProjectOption(p.Id, p.Title));
        }
        SelectedProjectOption ??= ProjectOptions.FirstOrDefault();

        var tasks = await _taskService.GetAllAsync();
        TaskOptions.Clear();
        TaskOptions.Add(new TaskOption(null, "(No task)"));
        foreach (var t in tasks.Where(t => t.Status is not (TaskStatus.Completed or TaskStatus.Canceled)))
        {
            TaskOptions.Add(new TaskOption(t.Id, t.Title));
        }
        SelectedTaskOption ??= TaskOptions.FirstOrDefault();

        SyncFromService();
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (_focus.HasActiveSession)
        {
            return;
        }

        try
        {
            await _focus.StartAsync(SelectedProjectOption?.Id, SelectedTaskOption?.Id, IntentText);
            IntentText = string.Empty;
            FeedbackMessage = string.Empty;
            SyncFromService();
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
            Log.Error(ex, "Failed to start focus session");
        }
    }

    [RelayCommand]
    private async Task PauseResumeAsync()
    {
        try
        {
            if (IsRunning)
            {
                await _focus.PauseAsync();
            }
            else
            {
                await _focus.ResumeAsync();
            }

            SyncFromService();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to pause/resume focus session");
        }
    }

    [RelayCommand]
    private async Task BeginFinishAsync()
    {
        // Stop the clock while the user writes their review.
        if (IsRunning)
        {
            await _focus.PauseAsync();
        }

        SelectedResult = FocusSessionResult.Completed;
        ReviewSummary = string.Empty;
        ReviewBlocker = string.Empty;
        ReviewNext = string.Empty;
        IsReviewing = true;
        SyncFromService();
    }

    [RelayCommand]
    private async Task ConfirmFinishAsync()
    {
        try
        {
            await _focus.FinishAsync(SelectedResult, ReviewSummary, ReviewBlocker, ReviewNext);
            IsReviewing = false;
            FeedbackMessage = "Focus session saved.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
            Log.Error(ex, "Failed to finish focus session");
        }
    }

    [RelayCommand]
    private async Task CancelFinishAsync()
    {
        IsReviewing = false;
        await _focus.ResumeAsync();
        SyncFromService();
    }

    [RelayCommand]
    private async Task AbandonAsync()
    {
        try
        {
            await _focus.AbandonAsync();
            IsReviewing = false;
            FeedbackMessage = "Focus session abandoned.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to abandon focus session");
        }
    }

    private void SyncFromService()
    {
        var snapshot = _focus.GetActiveSnapshot();

        if (snapshot is null)
        {
            HasActiveSession = false;
            IsRunning = false;
            ElapsedText = "00:00";
            CurrentObjective = string.Empty;
            return;
        }

        HasActiveSession = true;
        IsRunning = snapshot.Status == FocusSessionStatus.Active;
        PauseResumeText = IsRunning ? "Pause" : "Resume";
        ElapsedText = Format(snapshot.Elapsed);
        CurrentObjective = BuildObjective(snapshot);
    }

    private static string BuildObjective(ActiveFocusSnapshot snapshot)
    {
        var task = string.IsNullOrWhiteSpace(snapshot.TaskTitle) ? null : snapshot.TaskTitle;
        var intent = string.IsNullOrWhiteSpace(snapshot.Intent) ? null : snapshot.Intent;
        return (task, intent) switch
        {
            (not null, not null) => $"{task} — {intent}",
            (not null, null) => task!,
            (null, not null) => intent!,
            _ => "Focused work"
        };
    }

    private static string Format(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}
