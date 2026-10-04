using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using Serilog;

namespace LTFI.ViewModels;

/// <summary>
/// The app shell: owns the navigation rail, the live top-bar status readout (clock, points,
/// streak, active-project meter), and the currently displayed page. Modelled on the
/// "LTFI Command Center" design â€” a top status bar over a 46px icon rail and a content region.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IInsightsService _insights;
    private readonly IProjectService _projectService;
    private readonly IReflectionService _reflections;
    private readonly RemindersPanelViewModel _reminders;
    private readonly DispatcherTimer _clock;
    private int _tick;

    public ObservableCollection<NavItem> NavItems { get; }
    public NavItem SettingsNav { get; }

    [ObservableProperty] private NavItem? selectedNav;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsActive))]
    private ViewModelBase? currentViewModel;

    [ObservableProperty] private string dateText = string.Empty;
    [ObservableProperty] private string clockText = string.Empty;
    [ObservableProperty] private int pointsToday;
    [ObservableProperty] private int streakDays;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveMeterFilled), nameof(ActiveMeterEmpty), nameof(ActiveText))]
    private int activeCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveMeterFilled), nameof(ActiveMeterEmpty), nameof(ActiveText))]
    private int activeLimit = ProjectPolicy.MaxActiveProjects;

    /// <summary>Green filled cells and faint empty cells of the top-bar active-project meter.</summary>
    public string ActiveMeterFilled => new('â–®', Math.Clamp(ActiveCount, 0, ActiveLimit));
    public string ActiveMeterEmpty => new('â–¯', Math.Max(0, ActiveLimit - ActiveCount));
    public string ActiveText => $"{ActiveCount}/{ActiveLimit}";

    /// <summary>True when the (unlisted, bottom-pinned) Settings page is showing.</summary>
    public bool IsSettingsActive => CurrentViewModel == SettingsNav.ViewModel;

    /// <summary>
    /// True while the weekly check-in is due and not snoozed: the shell pins the Check-In page and
    /// disables the rail until the user submits or snoozes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNavEnabled))]
    private bool isGated;

    public bool IsNavEnabled => !IsGated;

    private readonly NavItem _focusNav;
    private readonly FocusViewModel _focus_vm;
    private readonly NavItem _checkInNav;
    private readonly CheckInViewModel _checkIn;

    public MainWindowViewModel(
        CommandCenterViewModel command,
        TodayViewModel today,
        ProjectsViewModel projects,
        TasksViewModel tasks,
        FocusViewModel focus,
        ReviewViewModel review,
        CheckInViewModel checkIn,
        IInsightsService insights,
        IProjectService projectService,
        IReflectionService reflections,
        RemindersPanelViewModel reminders)
    {
        _insights = insights;
        _projectService = projectService;
        _reflections = reflections;
        _reminders = reminders;
        _checkIn = checkIn;
        _checkInNav = new NavItem("CHK", "Check-In", checkIn);
        _focus_vm = focus;
        _focusNav = new NavItem("FOC", "Focus", focus);

        NavItems =
        [
            new NavItem("CMD", "Command Center", command),
            new NavItem("TDY", "Today", today),
            new NavItem("PRJ", "Projects", projects),
            new NavItem("TSK", "Tasks", tasks),
            _focusNav,
            new NavItem("REV", "Review", review),
            _checkInNav,
        ];

        SettingsNav = new NavItem("SET", "Settings", new PlaceholderViewModel(
            "Settings", "Settings and optional integrations arrive in later phases."));

        // Quick-start from the Today page: prefill the Focus page for the chosen task, then open it.
        today.StartFocusRequested += (_, task) =>
        {
            _focus_vm.PrepareFor(task.ProjectId, task.Id);
            SelectedNav = _focusNav;
        };

        // Command Center's current-operation controls hand off to the Focus page.
        command.OpenFocusRequested += (_, _) => SelectedNav = _focusNav;

        // Submitting or snoozing the check-in lifts the gate (re-checked against the service).
        checkIn.GateCleared += (_, _) =>
        {
            _ = CheckGateAsync();
            _ = RefreshHeaderAsync();
        };

        // A reminders sync that changed data (new tasks, iPhone completions â†’ evidence/points)
        // re-reads the header and the read-only overview pages. Tasks is left alone so an
        // in-progress edit there isn't clobbered.
        reminders.Synced += (_, _) =>
        {
            if (CurrentViewModel is (TodayViewModel or CommandCenterViewModel) and IRefreshable page)
            {
                _ = SafeRefreshAsync(page);
            }
            _ = RefreshHeaderAsync();
        };

        UpdateClock();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => OnClockTick();
        _clock.Start();

        SelectedNav = NavItems[0];
        _ = RefreshHeaderAsync();
        _ = CheckGateAsync();

        // Pull the latest iPhone reminders export on start-up (then on the 15s poll below).
        _ = _reminders.SyncIfChangedAsync();
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null)
        {
            return;
        }

        CurrentViewModel = value.ViewModel;

        if (value.ViewModel is IRefreshable refreshable)
        {
            _ = SafeRefreshAsync(refreshable);
        }

        // Header numbers can change as the user acts on a page; re-read on every navigation.
        _ = RefreshHeaderAsync();
    }

    private void OnClockTick()
    {
        UpdateClock();

        // Re-read the DB-backed header numbers periodically, not every second.
        if (++_tick % 15 == 0)
        {
            _ = RefreshHeaderAsync();
            _ = CheckGateAsync();
            // Cheap when nothing changed: a stat of the export file, no read.
            _ = _reminders.SyncIfChangedAsync();
        }
    }

    private void UpdateClock()
    {
        var now = DateTimeOffset.Now;
        DateText = now.ToString("ddd MMM dd", CultureInfo.InvariantCulture).ToUpperInvariant();
        ClockText = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private async Task RefreshHeaderAsync()
    {
        try
        {
            var snapshot = await _insights.GetTodaySnapshotAsync();
            PointsToday = snapshot.PointsToday;
            StreakDays = snapshot.FocusStreakDays;

            ActiveCount = await _projectService.CountActiveAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh shell header");
        }
    }

    /// <summary>
    /// Raises or lowers the weekly check-in gate. Runs at launch and on the 15s refresh, so a
    /// snooze expiring mid-session brings the check-in back.
    /// </summary>
    private async Task CheckGateAsync()
    {
        try
        {
            var status = await _reflections.GetWeeklyCheckInStatusAsync();
            if (status.MustShow && !IsGated)
            {
                IsGated = true;
                if (SelectedNav == _checkInNav)
                {
                    await SafeRefreshAsync(_checkIn);
                }
                else
                {
                    SelectedNav = _checkInNav;
                }
            }
            else if (!status.MustShow && IsGated)
            {
                IsGated = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to check the weekly check-in gate");
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (!IsGated)
        {
            SelectedNav = SettingsNav;
        }
    }

    private static async Task SafeRefreshAsync(IRefreshable refreshable)
    {
        try
        {
            await refreshable.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh {Page}", refreshable.GetType().Name);
        }
    }
}
