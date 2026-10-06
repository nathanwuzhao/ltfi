using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using LTFI.Infrastructure.Audio;
using LTFI.Infrastructure.Settings;
using LTFI.Services;
using LTFI.Services.Audio;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.ViewModels;

/// <summary>A task choice for starting a focus session; <c>Id == null</c> means "no task".</summary>
public sealed record TaskOption(Guid? Id, string Title);

/// <summary>
/// The Focus page: pick what to work on, run a timer — pomodoro countdown (default) or free
/// count-up — and record an end-of-session review. Also hosts the 10-minute NSDR. Registered as a
/// singleton, and its 1-second tick is the one place that advances pomodoro/NSDR transitions
/// (and raises the attention alert), so the timers keep running on any page.
/// </summary>
public partial class FocusViewModel : ViewModelBase, IRefreshable
{
    private readonly IFocusSessionService _focus;
    private readonly IPomodoroService _pomodoro;
    private readonly INsdrService _nsdr;
    private readonly IProjectService _projectService;
    private readonly ITaskService _taskService;
    private readonly FocusSettings _settings;
    private readonly AttentionAlert _alert;
    private readonly IAudioPlayer _audio;
    private readonly NotificationSounds _sounds;
    private readonly ShellSignals _signals;
    private readonly DispatcherTimer _timer;

    /// <summary>The NSDR run the audio state belongs to is live (edge-detects NSDR start/end).</summary>
    private bool _nsdrAudioSession;

    /// <summary>The local NSDR track for the current run; null = none (or an http(s) link).</summary>
    private string? _nsdrAudioPath;

    private Guid? _pendingProjectId;
    private Guid? _pendingTaskId;
    private bool _hasPendingPrefill;
    private bool _ticking;
    private bool _resumeAfterReview;

    public ObservableCollection<ProjectOption> ProjectOptions { get; } = [];
    public ObservableCollection<TaskOption> TaskOptions { get; } = [];
    public Array Results { get; } = Enum.GetValues<FocusSessionResult>();

    public string Header => "Focus";

    // --- setup (no active session) ---
    [ObservableProperty] private ProjectOption? selectedProjectOption;
    [ObservableProperty] private TaskOption? selectedTaskOption;
    [ObservableProperty] private string intentText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPomodoroMode), nameof(IsFreeMode))]
    private FocusTimerMode timerMode = FocusTimerMode.Pomodoro;

    public bool IsPomodoroMode => TimerMode == FocusTimerMode.Pomodoro;
    public bool IsFreeMode => TimerMode == FocusTimerMode.Free;

    // --- active session ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetup), nameof(ShowActive), nameof(ShowFreeTimer))]
    private bool hasActiveSession;

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string elapsedText = "00:00";
    [ObservableProperty] private string currentObjective = string.Empty;
    [ObservableProperty] private string pauseResumeText = "Pause";

    // --- pomodoro (active session in pomodoro mode) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFreeTimer), nameof(ShowActive))]
    private bool isPomodoroActive;

    [ObservableProperty] private string phaseLabel = "WORK";
    [ObservableProperty] private string countdownText = "25:00";
    [ObservableProperty] private string pomodoroDots = "○○○○";
    [ObservableProperty] private string pomodorosText = "0 POMODOROS COMPLETED";
    [ObservableProperty] private IBrush phaseBrush = CcBrush.Green;
    [ObservableProperty] private bool isWorkPhase = true;
    [ObservableProperty] private bool isBreakRunning;
    [ObservableProperty] private bool isBreakOver;
    [ObservableProperty] private bool canTakeNsdr;

    // --- NSDR (standalone, or in place of a long break) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetup), nameof(ShowActive), nameof(ShowNsdr))]
    private bool isNsdrRunning;

    [ObservableProperty] private bool isNsdrInPomodoro;
    [ObservableProperty] private string nsdrCountdownText = "10:00";
    [ObservableProperty] private string nsdrStepText = string.Empty;
    [ObservableProperty] private string nsdrCueTitle = string.Empty;
    [ObservableProperty] private string nsdrCueText = string.Empty;
    [ObservableProperty] private string nsdrNextText = string.Empty;
    [ObservableProperty] private double nsdrProgress;
    [ObservableProperty] private string nsdrStopText = "STOP";

    public bool HasNsdrAudio => !string.IsNullOrWhiteSpace(_settings.NsdrAudioUrl);

    /// <summary>nsdrAudioUrl is an existing local file: played in-app with a PLAY/PAUSE toggle.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOpenAudio))]
    private bool isNsdrAudioLocal;

    [ObservableProperty] private string nsdrAudioToggleText = "PLAY AUDIO";

    /// <summary>OPEN AUDIO for an http(s) link (or a bad path, which then explains itself).</summary>
    public bool ShowOpenAudio => HasNsdrAudio && !IsNsdrAudioLocal;

    // --- review ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetup), nameof(ShowActive), nameof(ShowReview), nameof(ShowNsdr))]
    private bool isReviewing;

    [ObservableProperty] private FocusSessionResult selectedResult = FocusSessionResult.Completed;
    [ObservableProperty] private string reviewSummary = string.Empty;
    [ObservableProperty] private string reviewBlocker = string.Empty;
    [ObservableProperty] private string reviewNext = string.Empty;

    [ObservableProperty] private string feedbackMessage = string.Empty;

    public FocusViewModel(
        IFocusSessionService focus,
        IPomodoroService pomodoro,
        INsdrService nsdr,
        IProjectService projectService,
        ITaskService taskService,
        FocusSettings settings,
        AttentionAlert alert,
        [FromKeyedServices(AudioPlayers.Ambient)] IAudioPlayer audio,
        NotificationSounds sounds,
        ShellSignals signals)
    {
        _signals = signals;
        _focus = focus;
        _pomodoro = pomodoro;
        _nsdr = nsdr;
        _projectService = projectService;
        _taskService = taskService;
        _settings = settings;
        _alert = alert;
        _audio = audio;
        _sounds = sounds;

        // The track ending before 10:00 just ends; the toggle offers to play it again.
        _audio.PlaybackFinished += (_, _) => UpdateNsdrAudioText();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();

        SyncFromService();
    }

    public bool ShowSetup => !HasActiveSession && !IsReviewing && !IsNsdrRunning;
    public bool ShowActive => HasActiveSession && !IsReviewing && !IsNsdrRunning;
    public bool ShowFreeTimer => HasActiveSession && !IsPomodoroActive;
    public bool ShowReview => IsReviewing;
    public bool ShowNsdr => IsNsdrRunning && !IsReviewing;

    /// <summary>
    /// Pre-selects a project/task for the next session (used by Today's quick-start). Applied on the
    /// next refresh, which the navigation into this page triggers. Ignored if a session is running.
    /// </summary>
    public void PrepareFor(Guid? projectId, Guid taskId)
    {
        if (_focus.HasActiveSession)
        {
            return;
        }

        _pendingProjectId = projectId;
        _pendingTaskId = taskId;
        _hasPendingPrefill = true;
    }

    public async Task RefreshAsync()
    {
        var projects = await _projectService.GetAllAsync();
        ProjectOptions.Clear();
        ProjectOptions.Add(new ProjectOption(null, "(No project)"));
        foreach (var p in projects)
        {
            ProjectOptions.Add(new ProjectOption(p.Id, p.Title));
        }

        var tasks = await _taskService.GetAllAsync();
        TaskOptions.Clear();
        TaskOptions.Add(new TaskOption(null, "(No task)"));
        foreach (var t in tasks.Where(t => t.Status is not (TaskStatus.Completed or TaskStatus.Canceled)))
        {
            TaskOptions.Add(new TaskOption(t.Id, t.Title));
        }

        if (_hasPendingPrefill)
        {
            SelectedProjectOption = ProjectOptions.FirstOrDefault(o => o.Id == _pendingProjectId) ?? ProjectOptions.FirstOrDefault();
            SelectedTaskOption = TaskOptions.FirstOrDefault(o => o.Id == _pendingTaskId) ?? TaskOptions.FirstOrDefault();
            _hasPendingPrefill = false;
        }
        else
        {
            SelectedProjectOption ??= ProjectOptions.FirstOrDefault();
            SelectedTaskOption ??= TaskOptions.FirstOrDefault();
        }

        SyncFromService();
    }

    [RelayCommand] private void UsePomodoro() => TimerMode = FocusTimerMode.Pomodoro;

    [RelayCommand] private void UseFree() => TimerMode = FocusTimerMode.Free;

    [RelayCommand]
    private async Task StartAsync()
    {
        if (_focus.HasActiveSession)
        {
            return;
        }

        try
        {
            if (TimerMode == FocusTimerMode.Pomodoro)
            {
                await _pomodoro.StartAsync(SelectedProjectOption?.Id, SelectedTaskOption?.Id, IntentText);
            }
            else
            {
                await _focus.StartAsync(SelectedProjectOption?.Id, SelectedTaskOption?.Id, IntentText);
            }

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
            // Breaks keep the session paused on purpose; only work time can be paused/resumed.
            if (_pomodoro.GetSnapshot() is { IsBreak: true })
            {
                return;
            }

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
    private async Task SkipBreakAsync()
    {
        try
        {
            await _pomodoro.SkipBreakAsync();
            SyncFromService();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to skip break");
        }
    }

    [RelayCommand]
    private async Task StartNextPomodoroAsync()
    {
        try
        {
            await _pomodoro.StartNextAsync();
            SyncFromService();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start next pomodoro");
        }
    }

    [RelayCommand]
    private void TakeNsdrInstead()
    {
        _pomodoro.TakeNsdrInstead();
        SyncFromService();
    }

    [RelayCommand]
    private void StartNsdr()
    {
        if (_focus.HasActiveSession || _nsdr.IsRunning)
        {
            return;
        }

        FeedbackMessage = string.Empty;
        _nsdr.Start();
        SyncFromService();
    }

    [RelayCommand]
    private async Task StopNsdrAsync()
    {
        try
        {
            if (IsNsdrInPomodoro)
            {
                // In place of a long break: stopping early skips straight to the next pomodoro.
                await _pomodoro.SkipBreakAsync();
            }
            else
            {
                _nsdr.Stop();
                FeedbackMessage = "NSDR stopped early — nothing recorded.";
            }

            SyncFromService();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to stop NSDR");
        }
    }

    /// <summary>PLAY/PAUSE for a local NSDR track (from the top again once it has ended).</summary>
    [RelayCommand]
    private async Task ToggleNsdrAudioAsync()
    {
        if (_nsdrAudioPath is null || !IsNsdrRunning)
        {
            return;
        }

        if (_audio.IsPlaying)
        {
            await _audio.PauseAsync();
        }
        else if (_audio.IsPaused)
        {
            await _audio.ResumeAsync();
        }
        else
        {
            await PlayNsdrTrackAsync(_nsdrAudioPath);
        }

        UpdateNsdrAudioText();
    }

    [RelayCommand]
    private void OpenNsdrAudio()
    {
        var url = _settings.NsdrAudioUrl?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        // An http(s) link opens in the browser; a local audio file (absolute path or file: URI)
        // opens in the default player. Anything else is refused.
        string target;
        if (Uri.TryCreate(Environment.ExpandEnvironmentVariables(url), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            target = uri.AbsoluteUri;
        }
        else if (uri is { IsFile: true } && System.IO.File.Exists(uri.LocalPath))
        {
            target = uri.LocalPath;
        }
        else
        {
            FeedbackMessage = "focus.nsdrAudioUrl in settings.json must be an http(s) link or an existing audio file path.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FeedbackMessage = "Couldn't open the audio link.";
            Log.Warning(ex, "Failed to open NSDR audio URL");
        }
    }

    [RelayCommand]
    private async Task BeginFinishAsync()
    {
        // Stop the clock while the user writes their review; remember whether to restart it.
        _resumeAfterReview = IsRunning;
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
            _pomodoro.Reset();
            IsReviewing = false;
            FeedbackMessage = "Focus session saved.";
            _signals.NotifyStatsChanged();
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
        // Only restart a clock that was running (never resume into a pomodoro break).
        if (_resumeAfterReview)
        {
            await _focus.ResumeAsync();
        }

        _resumeAfterReview = false;
        SyncFromService();
    }

    [RelayCommand]
    private async Task AbandonAsync()
    {
        try
        {
            await _focus.AbandonAsync();
            _pomodoro.Reset();
            IsReviewing = false;
            FeedbackMessage = "Focus session abandoned.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to abandon focus session");
        }
    }

    /// <summary>Once a second: advance any due pomodoro/NSDR transition, alert, and refresh the view.</summary>
    private async Task TickAsync()
    {
        if (_ticking)
        {
            return;
        }

        _ticking = true;
        try
        {
            if (!IsReviewing && _pomodoro.IsActive)
            {
                var wasNsdr = _pomodoro.GetSnapshot() is { Phase: PomodoroPhase.Nsdr };
                var transition = await _pomodoro.AdvanceAsync();
                if (transition is PomodoroTransition.WorkEnded or PomodoroTransition.BreakEnded)
                {
                    // A work block / NSDR break may have recorded evidence (points, streak).
                    _signals.NotifyStatsChanged();
                }

                switch (transition)
                {
                    case PomodoroTransition.WorkEnded:
                        _alert.Raise(Chime.WorkDone);
                        break;
                    case PomodoroTransition.BreakEnded when wasNsdr:
                        // 10:00 beat the track: stop it before the chime.
                        EndNsdrAudio();
                        _alert.Raise(Chime.NsdrDone);
                        break;
                    case PomodoroTransition.BreakEnded:
                        _alert.Raise(Chime.BreakOver);
                        break;
                }
            }
            else if (_nsdr.IsRunning && !_pomodoro.IsActive && await _nsdr.CompleteIfDueAsync())
            {
                FeedbackMessage = "NSDR complete — 10 minutes of deep rest logged (+3).";
                _signals.NotifyStatsChanged();
                EndNsdrAudio();
                _alert.Raise(Chime.NsdrDone);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Focus timer tick failed");
        }
        finally
        {
            _ticking = false;
            SyncFromService();
        }
    }

    private void SyncFromService()
    {
        var snapshot = _focus.GetActiveSnapshot();
        var pomo = _pomodoro.GetSnapshot();
        SyncNsdr(pomo);

        if (snapshot is null)
        {
            HasActiveSession = false;
            IsPomodoroActive = false;
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

        IsPomodoroActive = pomo is not null;
        if (pomo is null)
        {
            IsWorkPhase = true; // free mode: pause/resume always allowed
            return;
        }

        PhaseLabel = pomo.IsBreakOver
            ? "BREAK OVER"
            : pomo.IsWorkPaused ? "WORK · PAUSED" : pomo.PhaseLabel;
        CountdownText = FormatCountdown(pomo.Remaining);
        PomodoroDots = pomo.Dots;
        PomodorosText = pomo.CompletedWork == 1
            ? "1 POMODORO COMPLETED"
            : $"{pomo.CompletedWork} POMODOROS COMPLETED";
        IsWorkPhase = !pomo.IsBreak;
        IsBreakRunning = pomo.IsBreak && !pomo.IsBreakOver;
        IsBreakOver = pomo.IsBreakOver;
        CanTakeNsdr = pomo.CanTakeNsdr;
        PhaseBrush = pomo.IsBreak || pomo.IsWorkPaused ? CcBrush.Amber : CcBrush.Green;
    }

    private void SyncNsdr(PomodoroSnapshot? pomo)
    {
        var nsdr = _nsdr.GetSnapshot();
        IsNsdrInPomodoro = pomo is { Phase: PomodoroPhase.Nsdr, IsBreakOver: false };
        IsNsdrRunning = nsdr is not null;
        NsdrStopText = IsNsdrInPomodoro ? "END NSDR — START NEXT POMODORO" : "STOP (NOTHING RECORDED)";

        // Every way an NSDR starts or ends (button, long break, stop, completion, abandon) passes
        // through here, so the track follows the NSDR without per-command hooks.
        if (nsdr is not null && !_nsdrAudioSession)
        {
            BeginNsdrAudio();
        }
        else if (nsdr is null && _nsdrAudioSession)
        {
            EndNsdrAudio();
        }

        if (nsdr is null)
        {
            return;
        }

        NsdrCountdownText = FormatCountdown(nsdr.Remaining);
        NsdrStepText = $"STEP {nsdr.CueIndex + 1} / {Nsdr.Cues.Count} · {nsdr.Cue.Title.ToUpperInvariant()}";
        NsdrCueTitle = nsdr.Cue.Title;
        NsdrCueText = nsdr.Cue.Text;
        NsdrNextText = nsdr.NextCue is { } next
            ? $"NEXT AT {Format(next.At)} · {next.Title.ToUpperInvariant()}"
            : "LAST STEP";
        NsdrProgress = nsdr.Elapsed.TotalSeconds / Nsdr.Duration.TotalSeconds * 100;
    }

    /// <summary>An NSDR just started: auto-play the local track, if one is configured and exists.</summary>
    private void BeginNsdrAudio()
    {
        _nsdrAudioSession = true;
        _nsdrAudioPath = ResolveLocalAudioPath(_settings.NsdrAudioUrl);
        IsNsdrAudioLocal = _nsdrAudioPath is not null;
        if (_nsdrAudioPath is not null)
        {
            _ = PlayNsdrTrackAsync(_nsdrAudioPath);
        }

        UpdateNsdrAudioText();
    }

    /// <summary>The NSDR ended (stopped, completed or replaced): stop the track. Idempotent.</summary>
    private void EndNsdrAudio()
    {
        if (!_nsdrAudioSession)
        {
            return;
        }

        _nsdrAudioSession = false;
        if (_nsdrAudioPath is not null)
        {
            _ = _audio.StopAsync();
        }

        UpdateNsdrAudioText();
    }

    private async Task PlayNsdrTrackAsync(string path)
    {
        await _audio.SetVolumeAsync(_sounds.Volume);
        // The NSDR may have been stopped while the volume call was in flight.
        if (_nsdrAudioSession && _nsdrAudioPath == path)
        {
            await _audio.PlayAsync(path);
        }

        UpdateNsdrAudioText();
    }

    private void UpdateNsdrAudioText() =>
        NsdrAudioToggleText = _audio.IsPlaying ? "PAUSE AUDIO" : _audio.IsPaused ? "RESUME AUDIO" : "PLAY AUDIO";

    /// <summary>
    /// The local file <paramref name="setting"/> names (absolute path or file: URI, env vars
    /// expanded), if it exists; null for http(s) links, relative paths and missing files.
    /// </summary>
    private static string? ResolveLocalAudioPath(string? setting)
    {
        var raw = setting?.Trim().Trim('"');
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(raw);
            if (Uri.TryCreate(expanded, UriKind.Absolute, out var uri))
            {
                return uri.IsFile && System.IO.File.Exists(uri.LocalPath) ? uri.LocalPath : null;
            }

            return System.IO.Path.IsPathFullyQualified(expanded) && System.IO.File.Exists(expanded) ? expanded : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't resolve focus.nsdrAudioUrl");
            return null;
        }
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

    /// <summary>Countdowns round up, so a fresh 25:00 reads 25:00 and 0:00 means actually done.</summary>
    private static string FormatCountdown(TimeSpan remaining) =>
        Format(TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, remaining.TotalSeconds))));

    private static string Format(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}
