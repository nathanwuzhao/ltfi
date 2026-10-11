using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using Serilog;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.ViewModels;

/// <summary>
/// The Command Center (design doc "1a" reflow grid): a dense, single-screen operational overview
/// — current operation, focus debt, active projects, upcoming deadlines, evidence feed, weekly
/// commitments, and a per-project progress trend. Every panel is wired to real local data.
/// Clicking a contribution-graph day filters the evidence feed to that day.
/// </summary>
public partial class CommandCenterViewModel : ViewModelBase, IRefreshable
{
    private readonly IFocusSessionService _focus;
    private readonly IPomodoroService _pomodoro;
    private readonly INsdrService _nsdr;
    private readonly IProjectService _projectService;
    private readonly ITaskService _taskService;
    private readonly IMilestoneService _milestoneService;
    private readonly IReviewService _review;
    private readonly IEvidenceService _evidence;
    private readonly IInsightsService _insights;
    private readonly ICommitmentService _commitments;
    private readonly ShellSignals _signals;
    private readonly DispatcherTimer _clock;

    private readonly Dictionary<Guid, string> _projectTitles = new();
    private readonly HashSet<Guid> _standingProjects = new(); // project-code colour: standing = neutral
    private Guid? _selectedProjectId;
    private DateOnly? _selectedDay;

    /// <summary>Raised when the user asks to jump to the Focus page (start/finish a session).</summary>
    public event EventHandler? OpenFocusRequested;

    /// <summary>Raised by the empty WEEKLY COMMITMENTS panel's button: open the Check-In page.</summary>
    public event EventHandler? OpenCheckInRequested;

    public string Header => "Command Center";

    public ObservableCollection<ProjectRow> Projects { get; } = [];
    public ObservableCollection<ProjectRow> ProgressTabs { get; } = [];
    public ObservableCollection<DeadlineRow> Deadlines { get; } = [];
    public ObservableCollection<EvidenceRow> Evidence { get; } = [];
    public ObservableCollection<DebtRow> DebtLines { get; } = [];
    public ObservableCollection<CommitRow> Commitments { get; } = [];
    public ObservableCollection<HeatCell> ActivityCells { get; } = [];
    public ObservableCollection<ContribWeekRow> ContribWeeks { get; } = [];

    /// <summary>The Less → More legend swatches (Heat0–4).</summary>
    public IReadOnlyList<IBrush> ContribLegend { get; } =
        [CcBrush.Heat0, CcBrush.Heat1, CcBrush.Heat2, CcBrush.Heat3, CcBrush.Heat4];

    // --- CONTRIBUTIONS ---
    [ObservableProperty] private string contribSummary = "0 PTS IN THE LAST YEAR";
    [ObservableProperty] private string contribBest = string.Empty;
    [ObservableProperty] private string contribTotal = "0";
    [ObservableProperty] private string contribStreak = "0d";
    [ObservableProperty] private string contribLongest = "0d";
    [ObservableProperty] private string contribActive = "0";

    // --- CURRENT OPERATION ---
    [ObservableProperty] private bool hasActiveSession;
    [ObservableProperty] private string clockText = "00:00";
    [ObservableProperty] private string opContext = "NO ACTIVE SESSION";

    /// <summary>The running session's project code (empty when none), drawn in its colour before <see cref="OpContext"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpCode))]
    private string opCode = string.Empty;

    [ObservableProperty] private IBrush opCodeBrush = CcBrush.Faint;

    public bool HasOpCode => OpCode.Length > 0;
    [ObservableProperty] private string opTitle = "Start a focus session to begin an operation";
    [ObservableProperty] private string opIntent = string.Empty;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string runLabel = "START";

    // --- FOCUS DEBT ---
    [ObservableProperty] private string weekFocusText = "0.0h";
    [ObservableProperty] private string weekTargetText = "/ 15h";
    [ObservableProperty] private double weekFocusValue;
    [ObservableProperty] private IBrush weekFocusBrush = CcBrush.Amber;
    [ObservableProperty] private string focusStreakText = "0d";
    [ObservableProperty] private int tasksThisWeek;

    // --- UPCOMING / EVIDENCE / COMMITMENTS labels ---
    [ObservableProperty] private string evidenceCountLabel = "0 EVENTS";
    [ObservableProperty] private bool hasDayFilter;
    [ObservableProperty] private string evidenceEmptyText = string.Empty;
    [ObservableProperty] private string commitProgress = "0 / 0";
    [ObservableProperty] private bool hasCommitments;

    /// <summary>"THIS WEEK", or "NEXT WEEK · FROM MON OCT 12" once this week's check-in is in (Sat/Sun).</summary>
    [ObservableProperty] private string commitWeekLabel = "THIS WEEK";
    [ObservableProperty] private bool commitIsNextWeek;
    [ObservableProperty] private string commitEmptyText = "No commitments yet — do your weekly check-in";
    [ObservableProperty] private string activeLimitText = "0 / 4 LIMIT";

    // --- PROJECT PROGRESS ---
    [ObservableProperty] private bool hasProgressProject;

    /// <summary>The PROJECT PROGRESS panel shows only for a selected project that tracks progress
    /// (standing projects never complete, so they have none).</summary>
    [ObservableProperty] private bool showProgressPanel;
    [ObservableProperty] private int progPct;
    [ObservableProperty] private string progName = string.Empty;
    [ObservableProperty] private IBrush progProjectBrush = CcBrush.Secondary;
    [ObservableProperty] private string progDelta = string.Empty;
    [ObservableProperty] private string progMilestone = "—";
    [ObservableProperty] private int progTargetPct;
    [ObservableProperty] private IBrush progBrush = CcBrush.Green;
    [ObservableProperty] private Geometry? trendArea;
    [ObservableProperty] private Geometry? trendLine;
    [ObservableProperty] private Geometry? trendMilestone;
    [ObservableProperty] private string laneStat = string.Empty;
    [ObservableProperty] private string laneLast = string.Empty;

    // --- expand/collapse (reflow) ---
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(OpsCaret))] private bool opsExpanded;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ProjectsCaret))] private bool projectsExpanded;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DebtCaret))] private bool debtExpanded;

    public string OpsCaret => OpsExpanded ? "▾" : "▸";
    public string ProjectsCaret => ProjectsExpanded ? "▾" : "▸";
    public string DebtCaret => DebtExpanded ? "▾" : "▸";

    public CommandCenterViewModel(
        IFocusSessionService focus,
        IPomodoroService pomodoro,
        INsdrService nsdr,
        IProjectService projectService,
        ITaskService taskService,
        IMilestoneService milestoneService,
        IReviewService review,
        IEvidenceService evidence,
        IInsightsService insights,
        ICommitmentService commitments,
        ShellSignals signals)
    {
        _signals = signals;
        _focus = focus;
        _pomodoro = pomodoro;
        _nsdr = nsdr;
        _projectService = projectService;
        _taskService = taskService;
        _milestoneService = milestoneService;
        _review = review;
        _evidence = evidence;
        _insights = insights;
        _commitments = commitments;

        SyncCurrentOp();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => SyncCurrentOp();
        _clock.Start();
    }

    [RelayCommand] private void ToggleOps() => OpsExpanded = !OpsExpanded;
    [RelayCommand] private void ToggleProjects() => ProjectsExpanded = !ProjectsExpanded;
    [RelayCommand] private void ToggleDebt() => DebtExpanded = !DebtExpanded;

    [RelayCommand]
    private async Task ToggleRunAsync()
    {
        try
        {
            if (!_focus.HasActiveSession)
            {
                OpenFocusRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Pomodoro break: the session stays paused; the button skips / ends the break instead.
            if (_pomodoro.GetSnapshot() is { IsBreak: true } pomo)
            {
                if (pomo.IsBreakOver)
                {
                    await _pomodoro.StartNextAsync();
                }
                else
                {
                    await _pomodoro.SkipBreakAsync();
                }

                SyncCurrentOp();
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

            SyncCurrentOp();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Command Center run toggle failed");
        }
    }

    [RelayCommand]
    private void Finish() => OpenFocusRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task SelectProjectAsync(Guid id) => await LoadProgressAsync(id);

    [RelayCommand]
    private void OpenCheckIn() => OpenCheckInRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Marks an unlinked commitment kept (writes its evidence once), then reloads.</summary>
    [RelayCommand]
    private async Task KeepCommitmentAsync(Guid id)
    {
        try
        {
            await _commitments.KeepAsync(id);
            _signals.NotifyStatsChanged(); // +5 points, maybe today's first activity
            await LoadCommitmentsAsync();
            await LoadContributionsAsync();
            await LoadEvidenceAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Keeping commitment {Id} failed", id);
        }
    }

    /// <summary>Clicking a graph day selects it (feed filters to that day); clicking it again clears.</summary>
    public async Task ToggleDayAsync(ContribCellRow cell)
    {
        if (!cell.IsClickable)
        {
            return;
        }

        _selectedDay = _selectedDay == cell.Day ? null : cell.Day;
        MarkSelectedDay();
        await LoadEvidenceAsync();
    }

    [RelayCommand]
    private async Task ClearDayAsync()
    {
        _selectedDay = null;
        MarkSelectedDay();
        await LoadEvidenceAsync();
    }

    private void MarkSelectedDay()
    {
        foreach (var week in ContribWeeks)
        {
            foreach (var c in week.Cells)
            {
                c.IsSelected = c.IsClickable && c.Day == _selectedDay;
            }
        }
    }

    public async Task RefreshAsync()
    {
        var projects = await _projectService.GetAllAsync();
        _projectTitles.Clear();
        _standingProjects.Clear();
        foreach (var p in projects)
        {
            _projectTitles[p.Id] = p.Title;
            if (p.IsStanding)
            {
                _standingProjects.Add(p.Id);
            }
        }

        // --- CONTRIBUTIONS (year graph) ---
        await LoadContributionsAsync();

        var review = await _review.GetWeeklyReviewAsync();
        var snapshot = await _insights.GetTodaySnapshotAsync();
        var activity = review.ProjectActivity.ToDictionary(a => a.Title, a => a);
        var stalled = review.StalledProjects.Select(s => s.Title).ToHashSet();

        var active = projects.Where(p => p.Status == ProjectStatus.Active).ToList();

        // --- ACTIVE PROJECTS + PROGRESS TABS ---
        Projects.Clear();
        ProgressTabs.Clear();
        foreach (var p in active)
        {
            activity.TryGetValue(p.Title, out var act);
            var focusTime = act?.FocusTime ?? TimeSpan.Zero;
            var tasksDone = act?.TasksCompleted ?? 0;

            string risk;
            IBrush riskBrush;
            if (stalled.Contains(p.Title)) { risk = "STALLED"; riskBrush = CcBrush.Red; }
            else if (focusTime > TimeSpan.Zero && tasksDone == 0) { risk = "NO EVIDENCE"; riskBrush = CcBrush.Amber; }
            else { risk = "ON TRACK"; riskBrush = CcBrush.Green; }

            var pct = p.ProgressPercent ?? 0;
            var row = new ProjectRow(p.Id)
            {
                Code = Code(p.Title),
                Name = p.Title,
                ProjectBrush = ProjectBrushes.For(p),
                Dot = riskBrush,
                HasProgress = p.HasProgress,
                Pct = pct,
                BarValue = pct,
                BarBrush = riskBrush,
                Focus = FormatHoursShort(focusTime),
                Risk = risk,
                RiskBrush = riskBrush,
            };
            Projects.Add(row);
            if (p.HasProgress)
            {
                ProgressTabs.Add(row);
            }
        }

        // Standing projects (e.g. Life) are listed but don't use up the limit.
        ActiveLimitText = $"{active.Count(p => !p.IsStanding)} / {ProjectPolicy.MaxActiveProjects} LIMIT";

        // --- FOCUS DEBT ---
        var hours = review.FocusTimeThisWeek.TotalHours;
        WeekFocusText = $"{hours:0.0}h";
        WeekTargetText = $"/ {ProjectPolicy.WeeklyFocusTargetHours:0}h";
        var frac = Math.Clamp(hours / ProjectPolicy.WeeklyFocusTargetHours, 0, 1);
        WeekFocusValue = frac * 100;
        WeekFocusBrush = frac >= 1 ? CcBrush.Green : CcBrush.Amber;
        FocusStreakText = $"{snapshot.FocusStreakDays}d";
        TasksThisWeek = review.TasksCompletedThisWeek;

        DebtLines.Clear();
        foreach (var a in review.ProjectActivity.OrderByDescending(a => a.FocusTime))
        {
            var noEvidence = a.FocusTime > TimeSpan.Zero && a.TasksCompleted == 0;
            DebtLines.Add(new DebtRow
            {
                Dot = noEvidence ? CcBrush.Amber : CcBrush.Green,
                Code = Code(a.Title),
                ProjectBrush = ProjectBrushes.ForAny(a),
                Logged = FormatHoursShort(a.FocusTime),
                Note = noEvidence ? "logged, no completions" : $"{a.TasksCompleted} done",
                NoteBrush = noEvidence ? CcBrush.Amber : CcBrush.Dim,
            });
        }

        // --- UPCOMING DEADLINES ---
        await LoadDeadlinesAsync(projects);

        // --- EVIDENCE FEED (all recent, or the clicked graph day) ---
        await LoadEvidenceAsync();

        // --- WEEKLY COMMITMENTS ---
        await LoadCommitmentsAsync();

        // --- PROJECT PROGRESS (only projects that track progress; never standing ones) ---
        var target = _selectedProjectId is { } sel && active.Any(p => p.Id == sel)
            ? sel
            : active.FirstOrDefault(p => p.HasProgress)?.Id;
        if (target is { } id)
        {
            await LoadProgressAsync(id);
        }
        else
        {
            HasProgressProject = false;
            ShowProgressPanel = false;
        }

        SyncCurrentOp();
    }

    private async Task LoadContributionsAsync()
    {
        var scores = await _evidence.GetDailyScoresAsync(ContributionGraph.DefaultDays);
        var graph = ContributionGraph.Build(scores, DateOnly.FromDateTime(DateTime.Today));

        ContribWeeks.Clear();
        foreach (var week in graph.Weeks)
        {
            ContribWeeks.Add(new ContribWeekRow
            {
                Month = week.MonthLabel is { } m
                    ? CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(m)
                    : string.Empty,
                Cells = week.Days.Select(c => c.Kind == ContributionCellKind.Day
                    ? new ContribCellRow
                    {
                        Day = c.Day,
                        IsClickable = true,
                        IsSelected = c.Day == _selectedDay,
                        Color = HeatLevelBrush(c.Level),
                        Tip = ContribTip(c),
                    }
                    : new ContribCellRow { Day = c.Day, Color = Brushes.Transparent, Tip = null }).ToList(),
            });
        }

        // A selected day that scrolled out of the window is dropped.
        if (_selectedDay is { } d && (d < graph.Start || d > graph.Today))
        {
            _selectedDay = null;
        }

        var s = graph.Stats;
        ContribSummary = $"{s.TotalPoints.ToString("N0", CultureInfo.InvariantCulture)} PTS · {s.TotalEvents} EVENTS IN THE LAST YEAR";
        ContribBest = s.BestDay is { } best
            ? $"BEST DAY · {best.Day.ToString("MMM d", CultureInfo.InvariantCulture).ToUpperInvariant()} · {best.Points} PTS"
            : string.Empty;
        ContribTotal = s.TotalPoints.ToString("N0", CultureInfo.InvariantCulture);
        ContribStreak = $"{s.CurrentStreak}d";
        ContribLongest = $"{s.LongestStreak}d";
        ContribActive = $"{s.ActiveDays}";
    }

    private static string ContribTip(ContributionCell c)
    {
        var date = c.Day.ToString("ddd MMM d, yyyy", CultureInfo.InvariantCulture);
        if (c.Points <= 0)
        {
            return $"No contributions — {date}";
        }

        return $"{c.Points} pts · {c.Events} event{(c.Events == 1 ? "" : "s")} — {date}";
    }

    private static IBrush HeatLevelBrush(int level) => level switch
    {
        <= 0 => CcBrush.Heat0,
        1 => CcBrush.Heat1,
        2 => CcBrush.Heat2,
        3 => CcBrush.Heat3,
        _ => CcBrush.Heat4,
    };

    private async Task LoadDeadlinesAsync(IReadOnlyList<Project> projects)
    {
        var tasks = await _taskService.GetAllAsync();
        var items = new List<(DateTimeOffset when, string item, IBrush brush)>();

        foreach (var t in tasks.Where(t =>
                     t.DueAt is not null && t.Status is not (TaskStatus.Completed or TaskStatus.Canceled)))
        {
            items.Add((t.DueAt!.Value, t.Title, CcBrush.Secondary));
        }
        foreach (var p in projects.Where(p => p.TargetDate is not null && !p.IsArchived))
        {
            items.Add((p.TargetDate!.Value, $"{Code(p.Title)} target", ProjectBrushes.For(p)));
        }

        var today = DateTimeOffset.Now.Date;
        Deadlines.Clear();
        foreach (var (when, item, brush) in items.OrderBy(i => i.when).Take(6))
        {
            var days = (when.Date - today).Days;
            Deadlines.Add(new DeadlineRow
            {
                Date = when.ToString("MMM dd", CultureInfo.InvariantCulture).ToUpperInvariant(),
                Item = item,
                Days = days < 0 ? $"{-days}d over" : days == 0 ? "today" : $"{days}d",
                Dot = days <= 0 ? CcBrush.Red : days <= 3 ? CcBrush.Amber : CcBrush.Dim,
                ItemBrush = brush,
            });
        }
    }

    private async Task LoadEvidenceAsync()
    {
        IReadOnlyList<EvidenceLine> lines = _selectedDay is { } day
            ? await _evidence.GetForDayAsync(day)
            : await _evidence.GetRecentAsync(40);

        Evidence.Clear();
        foreach (var e in lines)
        {
            Evidence.Add(new EvidenceRow
            {
                Time = _selectedDay is null
                    ? FormatEvidenceTime(e.OccurredAt)
                    : e.OccurredAt.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                Tag = TypeTag(e.Type),
                TagBrush = TypeBrush(e.Type),
                Project = e.ProjectTitle is { } t ? Code(t) : ProjectCodes.None,
                ProjectBrush = ProjectBrushFor(e.ProjectId),
                HasProject = e.ProjectId is not null,
                Text = e.Title,
            });
        }

        HasDayFilter = _selectedDay is not null;
        if (_selectedDay is { } selected)
        {
            var pts = lines.Sum(l => EvidencePoints.ForContribution(l.Type));
            EvidenceCountLabel = $"{selected.ToString("ddd MMM dd", CultureInfo.InvariantCulture).ToUpperInvariant()} · "
                                 + $"{lines.Count} EVENT{(lines.Count == 1 ? "" : "S")} · {pts} PTS";
            EvidenceEmptyText = "No evidence on this day.";
        }
        else
        {
            EvidenceCountLabel = $"{lines.Count} EVENTS";
            EvidenceEmptyText = "No evidence yet. Complete a task or finish a focus session to log the first signal.";
        }
    }

    private async Task LoadCommitmentsAsync()
    {
        // This week's commitments (made in last week's check-in). Once this week's own check-in is
        // in, it has settled them, so the panel switches to the ones it made for next week.
        var panel = await _commitments.GetPanelAsync();
        var lines = panel.Lines;
        CommitIsNextWeek = panel.IsNextWeek;
        CommitWeekLabel = panel.IsNextWeek
            ? $"NEXT WEEK · FROM {CheckInFormat.Day(panel.WeekStart)}"
            : "THIS WEEK";
        CommitEmptyText = panel.IsNextWeek
            ? "No commitments for next week."
            : "No commitments for this week — they come from last week's check-in.";

        Commitments.Clear();
        foreach (var c in lines)
        {
            var meta = new List<string>();
            if (c.LinkedArea is { Length: > 0 } area)
            {
                meta.Add($"#{area}");
            }
            if (c.LinkedDue is { } due)
            {
                meta.Add(due.ToLocalTime().ToString("MMM dd", CultureInfo.InvariantCulture).ToUpperInvariant());
            }
            if (c.IsLinked && meta.Count == 0)
            {
                meta.Add("reminder");
            }

            Commitments.Add(new CommitRow(c.Id)
            {
                Text = c.Text,
                IsKept = c.IsKept,
                IsLinked = c.IsLinked,
                Meta = string.Join(" · ", meta),
                LinkTip = c.IsLinked
                    ? $"Linked to reminder “{c.LinkedTaskTitle ?? "?"}” — kept automatically when it's completed"
                    : null,
            });
        }

        HasCommitments = Commitments.Count > 0;
        CommitProgress = $"{lines.Count(c => c.IsKept)} / {lines.Count} KEPT";
    }

    private async Task LoadProgressAsync(Guid projectId)
    {
        _selectedProjectId = projectId;

        var project = await _projectService.GetByIdAsync(projectId);
        if (project is null)
        {
            HasProgressProject = false;
            ShowProgressPanel = false;
            return;
        }

        // Highlight the selected row / tab.
        foreach (var r in Projects)
        {
            r.IsSelected = r.Id == projectId;
        }

        // A standing project never completes: no progress, so no trend panel.
        if (!project.HasProgress)
        {
            HasProgressProject = false;
            ShowProgressPanel = false;
            return;
        }

        HasProgressProject = true;
        ShowProgressPanel = true;
        var pct = project.ProgressPercent ?? 0;
        var row = ProgressTabs.FirstOrDefault(r => r.Id == projectId);
        var brush = row?.RiskBrush ?? CcBrush.Green;

        ProgName = project.Title;
        ProgProjectBrush = ProjectBrushes.For(project);
        ProgPct = pct;
        ProgBrush = brush;
        ProgTargetPct = Math.Min(100, (pct / 25 + 1) * 25);

        var milestones = await _milestoneService.GetByProjectAsync(projectId);
        var next = milestones
            .Where(m => m.Status != MilestoneStatus.Completed)
            .OrderBy(m => m.SortOrder)
            .FirstOrDefault();
        ProgMilestone = next?.Title ?? "—";

        // Trend: cumulative evidence over 30 days, normalised to the current derived %.
        var daily30 = await _evidence.GetProjectDailyActivityAsync(projectId, 30);
        BuildTrend(daily30, pct);

        // Daily activity lane: 90 days of evidence density.
        var daily90 = await _evidence.GetProjectDailyActivityAsync(projectId, 90);
        ActivityCells.Clear();
        var total = 0;
        int? lastActive = null;
        for (var i = 0; i < daily90.Count; i++)
        {
            var c = daily90[i].Count;
            total += c;
            if (c > 0) lastActive = daily90.Count - 1 - i;
            ActivityCells.Add(new HeatCell { Color = HeatBrush(c) });
        }
        LaneStat = $"{total} events";
        LaneLast = lastActive switch
        {
            null => "no activity",
            0 => "today",
            1 => "1d ago",
            _ => $"{lastActive}d ago",
        };
    }

    private void BuildTrend(IReadOnlyList<DayActivity> daily, int currentPct)
    {
        var n = daily.Count;
        if (n < 2)
        {
            TrendArea = TrendLine = TrendMilestone = null;
            ProgDelta = "+0% · 7D";
            return;
        }

        // Cumulative fraction of the window's evidence, scaled to the current %.
        var cum = new double[n];
        var run = 0;
        for (var i = 0; i < n; i++)
        {
            run += daily[i].Count;
            cum[i] = run;
        }
        var totalEvidence = cum[n - 1];

        var pcts = new double[n];
        for (var i = 0; i < n; i++)
        {
            var frac = totalEvidence > 0 ? cum[i] / totalEvidence : 1.0;
            pcts[i] = frac * currentPct;
        }

        // Coordinate space matches the design SVG (viewBox 0 0 560 150).
        const double x0 = 12, x1 = 548, yBase = 96, yTop = 10;
        double X(int i) => x0 + i * (x1 - x0) / (n - 1);
        double Y(double p) => yBase - Math.Clamp(p, 0, 100) / 100.0 * (yBase - yTop);

        var line = new StringBuilder();
        var area = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            var cmd = i == 0 ? "M" : "L";
            var xy = $"{Fmt(X(i))},{Fmt(Y(pcts[i]))}";
            line.Append(cmd).Append(xy).Append(' ');
            area.Append(cmd).Append(xy).Append(' ');
        }
        area.Append($"L{Fmt(X(n - 1))},142 L{Fmt(X(0))},142 Z");

        TrendLine = Geometry.Parse(line.ToString().Trim());
        TrendArea = Geometry.Parse(area.ToString());

        var msY = Y(ProgTargetPct);
        TrendMilestone = Geometry.Parse($"M{Fmt(x0)},{Fmt(msY)} L{Fmt(x1)},{Fmt(msY)}");

        var sevenAgo = n >= 8 ? pcts[n - 8] : pcts[0];
        var delta = pcts[n - 1] - sevenAgo;
        ProgDelta = $"{(delta >= 0 ? "+" : "")}{delta:0}% · 7D";
    }

    private void SyncCurrentOp()
    {
        var s = _focus.GetActiveSnapshot();
        if (s is null && _nsdr.GetSnapshot() is { } rest)
        {
            // Standalone NSDR: show the rest countdown; the button opens the Focus page.
            HasActiveSession = false;
            IsRunning = false;
            ClockText = FormatCountdown(rest.Remaining);
            OpCode = string.Empty;
            OpContext = "NSDR · RESTING";
            OpTitle = "Non-sleep deep rest";
            OpIntent = rest.Cue.Title.ToLowerInvariant();
            RunLabel = "VIEW";
            return;
        }

        if (s is null)
        {
            HasActiveSession = false;
            IsRunning = false;
            ClockText = "00:00";
            OpCode = string.Empty;
            OpContext = "NO ACTIVE SESSION";
            OpTitle = "Start a focus session to begin an operation";
            OpIntent = string.Empty;
            RunLabel = "START";
            return;
        }

        HasActiveSession = true;
        IsRunning = s.Status == FocusSessionStatus.Active;
        RunLabel = IsRunning ? "PAUSE" : "RESUME";
        ClockText = FormatClock(s.Elapsed);

        // The project code is its own text run (in the project's colour); OpContext is the rest.
        var known = s.ProjectId is { } pid && _projectTitles.ContainsKey(pid);
        OpCode = known ? Code(_projectTitles[s.ProjectId!.Value]) : ProjectCodes.None;
        OpCodeBrush = known ? ProjectBrushFor(s.ProjectId) : CcBrush.Faint;
        OpContext = $"· {(IsRunning ? "RUNNING" : "PAUSED")}";
        OpTitle = s.TaskTitle ?? s.Intent ?? "Focused work";
        OpIntent = string.IsNullOrWhiteSpace(s.Intent) ? string.Empty : $"intent — {s.Intent}";

        // Pomodoro run: the clock shows the phase countdown instead of elapsed.
        if (_pomodoro.GetSnapshot() is { } pomo)
        {
            ClockText = FormatCountdown(pomo.Remaining);
            var phase = pomo.IsBreakOver ? "BREAK OVER"
                : pomo.IsWorkPaused ? "WORK · PAUSED"
                : pomo.PhaseLabel;
            OpContext = $"· {phase} · {pomo.Dots}";
            RunLabel = pomo.IsBreakOver ? "START NEXT"
                : pomo.IsBreak ? (pomo.Phase == PomodoroPhase.Nsdr ? "END NSDR" : "SKIP BREAK")
                : RunLabel;
            OpIntent = $"{pomo.CompletedWork} pomodoro{(pomo.CompletedWork == 1 ? "" : "s")} · {FormatClock(s.Elapsed)} worked"
                       + (string.IsNullOrWhiteSpace(s.Intent) ? string.Empty : $" · intent — {s.Intent}");
        }
    }

    private static string FormatCountdown(TimeSpan remaining) =>
        FormatClock(TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, remaining.TotalSeconds))));

    // ---------- helpers ----------

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string FormatClock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    private static string FormatHoursShort(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:0.0}h" : $"{(int)t.TotalMinutes}m";

    private static string FormatEvidenceTime(DateTimeOffset when)
    {
        var local = when.LocalDateTime;
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MMM dd", CultureInfo.InvariantCulture).ToUpperInvariant();
    }

    /// <summary>The project code everywhere on this page: first 4 letters/digits ("boids 01" → "BOID").</summary>
    private static string Code(string title) => ProjectCodes.Code(title);

    /// <summary>The project's stable colour (by id; standing projects neutral); dim for no project.</summary>
    private IBrush ProjectBrushFor(Guid? projectId) =>
        ProjectBrushes.For(projectId, projectId is { } id && _standingProjects.Contains(id));

    private static string TypeTag(EvidenceType type) => type switch
    {
        EvidenceType.TaskCompleted => "TASK",
        EvidenceType.SubtaskCompleted => "SUBTASK",
        EvidenceType.FocusSessionCompleted => "FOCUS",
        EvidenceType.ReflectionSubmitted => "REVIEW",
        EvidenceType.GitCommit => "COMMIT",
        EvidenceType.GitHubPullRequest => "PR",
        EvidenceType.GitHubIssueClosed => "ISSUE",
        EvidenceType.LogseqJournalEntry => "NOTE",
        EvidenceType.FileChanged => "FILE",
        EvidenceType.DistractionOverride => "OVERRIDE",
        EvidenceType.DistractionBlocked => "BLOCK",
        EvidenceType.NsdrCompleted => "NSDR",
        EvidenceType.CommitmentKept => "KEPT",
        _ => "NOTE",
    };

    private static IBrush TypeBrush(EvidenceType type) => type switch
    {
        EvidenceType.TaskCompleted or EvidenceType.SubtaskCompleted or EvidenceType.CommitmentKept => CcBrush.Green,
        EvidenceType.FocusSessionCompleted => CcBrush.Dim,
        EvidenceType.ReflectionSubmitted => CcBrush.Amber,
        EvidenceType.DistractionOverride or EvidenceType.DistractionBlocked => CcBrush.Red,
        _ => CcBrush.Dim,
    };

    private static IBrush HeatBrush(int count) => count switch
    {
        <= 0 => CcBrush.Heat0,
        1 => CcBrush.Heat1,
        2 => CcBrush.Heat2,
        3 => CcBrush.Heat3,
        _ => CcBrush.Heat4,
    };
}

// ---------- presentational row/cell types ----------

/// <summary>A row in the ACTIVE PROJECTS list / PROJECT PROGRESS tab strip.</summary>
public partial class ProjectRow(Guid id) : ObservableObject
{
    public Guid Id { get; } = id;
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IBrush Dot { get; init; } = CcBrush.Dim;

    /// <summary>Identity colour for the code (<see cref="ProjectBrushes"/>); <see cref="Dot"/>/<see cref="RiskBrush"/> stay status.</summary>
    public IBrush ProjectBrush { get; init; } = CcBrush.Body;

    /// <summary>False for standing projects: no bar and no %, a STANDING tag instead.</summary>
    public bool HasProgress { get; init; } = true;
    public int Pct { get; init; }
    public string PctText => $"{Pct}%";
    public double BarValue { get; init; }
    public IBrush BarBrush { get; init; } = CcBrush.Green;
    public string Focus { get; init; } = string.Empty;
    public string Risk { get; init; } = string.Empty;
    public IBrush RiskBrush { get; init; } = CcBrush.Green;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowBg))]
    private bool isSelected;

    public IBrush RowBg => IsSelected ? CcBrush.RowSelected : Brushes.Transparent;
}

public sealed class DeadlineRow
{
    public string Date { get; init; } = string.Empty;
    public string Item { get; init; } = string.Empty;
    public string Days { get; init; } = string.Empty;
    public IBrush Dot { get; init; } = CcBrush.Dim;

    /// <summary>A project target is drawn in the project's colour; a task in the normal text colour.</summary>
    public IBrush ItemBrush { get; init; } = CcBrush.Secondary;
}

public sealed class EvidenceRow
{
    public string Time { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public IBrush TagBrush { get; init; } = CcBrush.Dim;
    public string Project { get; init; } = string.Empty;

    /// <summary>The project's stable colour (<see cref="ProjectCodes"/>): code text + left bar.</summary>
    public IBrush ProjectBrush { get; init; } = CcBrush.Faint;
    public bool HasProject { get; init; }
    public string Text { get; init; } = string.Empty;
}

public sealed class DebtRow
{
    public IBrush Dot { get; init; } = CcBrush.Green;
    public string Code { get; init; } = string.Empty;

    /// <summary>The project's identity colour for <see cref="Code"/> (<see cref="ProjectBrushes"/>).</summary>
    public IBrush ProjectBrush { get; init; } = CcBrush.Body;
    public string Logged { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public IBrush NoteBrush { get; init; } = CcBrush.Dim;
}

/// <summary>One of this week's commitments on the Command Center.</summary>
public sealed class CommitRow(Guid id)
{
    public Guid Id { get; } = id;
    public string Text { get; init; } = string.Empty;
    public bool IsKept { get; init; }
    public bool IsLinked { get; init; }

    /// <summary>"#area · OCT 08" for a linked reminder; empty otherwise.</summary>
    public string Meta { get; init; } = string.Empty;
    public string? LinkTip { get; init; }

    /// <summary>Unlinked, still-open commitments get a checkbox; linked ones keep themselves.</summary>
    public bool CanCheck => !IsKept && !IsLinked;
    public bool ShowLinkMark => IsLinked && !IsKept;
    public bool IsOpen => !IsKept;
}

public sealed class HeatCell
{
    public IBrush Color { get; init; } = CcBrush.Heat0;
}

/// <summary>One Sunday-start column of the CONTRIBUTIONS graph; <see cref="Month"/> is its label (or empty).</summary>
public sealed class ContribWeekRow
{
    public string Month { get; init; } = string.Empty;
    public IReadOnlyList<ContribCellRow> Cells { get; init; } = [];
}

/// <summary>One day square; padding/future cells are transparent, unclickable and have no tooltip.</summary>
public sealed partial class ContribCellRow : ObservableObject
{
    public DateOnly Day { get; init; }
    public IBrush Color { get; init; } = CcBrush.Heat0;
    public string? Tip { get; init; }

    /// <summary>Only real in-window days can be clicked to filter the evidence feed.</summary>
    public bool IsClickable { get; init; }

    [ObservableProperty] private bool isSelected;
}

/// <summary>Frozen brushes matching the App.axaml palette, for computed visualisation colours.</summary>
internal static class CcBrush
{
    private static IBrush B(byte r, byte g, byte b) =>
        new SolidColorBrush(Color.FromRgb(r, g, b)).ToImmutable();

    public static readonly IBrush Green = B(0x46, 0xD1, 0x7F);
    public static readonly IBrush Amber = B(0xE6, 0xA1, 0x3A);
    public static readonly IBrush Red = B(0xE5, 0x48, 0x4D);
    public static readonly IBrush Body = B(0xC7, 0xCD, 0xD6);
    public static readonly IBrush Secondary = B(0xAA, 0xB1, 0xBE);
    public static readonly IBrush Dim = B(0x7A, 0x82, 0x8F);
    public static readonly IBrush Faint = B(0x5B, 0x63, 0x6F);
    public static readonly IBrush RowSelected = B(0x15, 0x1B, 0x24);

    // Green activity ramp (empty → dense).
    public static readonly IBrush Heat0 = B(0x14, 0x18, 0x1F);
    public static readonly IBrush Heat1 = B(0x17, 0x3A, 0x24);
    public static readonly IBrush Heat2 = B(0x1F, 0x5E, 0x37);
    public static readonly IBrush Heat3 = B(0x2F, 0x9A, 0x55);
    public static readonly IBrush Heat4 = B(0x46, 0xD1, 0x7F);
}
