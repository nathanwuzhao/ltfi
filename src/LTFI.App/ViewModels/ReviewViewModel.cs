using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Core.Abstractions;

namespace LTFI.ViewModels;

/// <summary>
/// Weekly review (plan §3.6): a deterministic, local-data summary plus stalled-project warnings,
/// and an optional LLM coach card on top. The coach is display-only — it never mutates data
/// (plan §2.4); turning its suggestions into commitments is the weekly check-in's job.
/// </summary>
public partial class ReviewViewModel : ViewModelBase, IRefreshable
{
    private readonly IReviewService _review;
    private readonly ICoachService _coach;
    private readonly ILlmProvider _llm;
    private readonly IApiKeyStore _keys;
    private readonly IEvidenceService _evidence;
    private readonly IInsightsService _insights;

    private WeeklyReview? _lastReview;

    public string Header => "Weekly Review";

    public ObservableCollection<ProjectActivityLine> ProjectActivity { get; } = [];
    public ObservableCollection<StalledProjectLine> StalledProjects { get; } = [];

    [ObservableProperty] private int activeProjectCount;
    [ObservableProperty] private int maxActiveProjects;
    [ObservableProperty] private bool isOverLimit;
    [ObservableProperty] private int tasksCompletedThisWeek;
    [ObservableProperty] private string focusTimeThisWeekText = "0m";
    [ObservableProperty] private int newProjectsThisWeek;
    [ObservableProperty] private int archivedProjectsThisWeek;
    [ObservableProperty] private bool hasStalled;
    [ObservableProperty] private bool hasActivity;

    // ---- coach ----
    public ObservableCollection<CoachWin> CoachWins { get; } = [];
    public ObservableCollection<CoachPattern> CoachPatterns { get; } = [];
    public ObservableCollection<CoachRisk> CoachRisks { get; } = [];
    public ObservableCollection<CoachLastWeekItem> CoachLastWeek { get; } = [];
    public ObservableCollection<CoachCommitment> CoachCommitments { get; } = [];
    public ObservableCollection<CoachDropOrPause> CoachDropOrPause { get; } = [];

    [ObservableProperty] private bool isCoachConfigured;
    [ObservableProperty] private string coachStatusText = string.Empty;
    [ObservableProperty] private bool isCoachBusy;
    [ObservableProperty] private string? coachError;
    [ObservableProperty] private bool hasCoachReport;
    [ObservableProperty] private string coachHeadline = string.Empty;
    [ObservableProperty] private string coachQuestion = string.Empty;
    [ObservableProperty] private string? coachCaveat;
    [ObservableProperty] private string coachMeta = string.Empty;
    [ObservableProperty] private string apiKeyInput = string.Empty;

    public ReviewViewModel(
        IReviewService review,
        ICoachService coach,
        ILlmProvider llm,
        IApiKeyStore keys,
        IEvidenceService evidence,
        IInsightsService insights)
    {
        _review = review;
        _coach = coach;
        _llm = llm;
        _keys = keys;
        _evidence = evidence;
        _insights = insights;
        UpdateCoachStatus();
    }

    public string ActiveLimitText => $"{ActiveProjectCount} / {MaxActiveProjects} active";
    public bool HasCoachError => !string.IsNullOrEmpty(CoachError);
    public bool HasCoachCaveat => !string.IsNullOrEmpty(CoachCaveat);
    public bool HasCoachLastWeek => CoachLastWeek.Count > 0;
    public bool HasCoachDropOrPause => CoachDropOrPause.Count > 0;
    public string CoachButtonText => IsCoachBusy ? "ASKING…" : HasCoachReport ? "ASK AGAIN" : "ASK COACH";

    partial void OnCoachErrorChanged(string? value) => OnPropertyChanged(nameof(HasCoachError));
    partial void OnCoachCaveatChanged(string? value) => OnPropertyChanged(nameof(HasCoachCaveat));
    partial void OnIsCoachBusyChanged(bool value) => OnPropertyChanged(nameof(CoachButtonText));
    partial void OnHasCoachReportChanged(bool value) => OnPropertyChanged(nameof(CoachButtonText));

    public async Task RefreshAsync()
    {
        var r = await _review.GetWeeklyReviewAsync();
        _lastReview = r;

        ActiveProjectCount = r.ActiveProjectCount;
        MaxActiveProjects = r.MaxActiveProjects;
        IsOverLimit = r.IsOverLimit;
        TasksCompletedThisWeek = r.TasksCompletedThisWeek;
        FocusTimeThisWeekText = FormatHours(r.FocusTimeThisWeek);
        NewProjectsThisWeek = r.NewProjectsThisWeek;
        ArchivedProjectsThisWeek = r.ArchivedProjectsThisWeek;

        ProjectActivity.Clear();
        foreach (var line in r.ProjectActivity)
        {
            ProjectActivity.Add(line);
        }
        HasActivity = ProjectActivity.Count > 0;

        StalledProjects.Clear();
        foreach (var line in r.StalledProjects)
        {
            StalledProjects.Add(line);
        }
        HasStalled = StalledProjects.Count > 0;

        OnPropertyChanged(nameof(ActiveLimitText));
        UpdateCoachStatus();
    }

    /// <summary>
    /// Builds a <see cref="CoachInput"/> from the same deterministic data this page shows (no check-in
    /// answers yet — the weekly check-in supplies those) and renders the coach's card. Read-only.
    /// </summary>
    [RelayCommand]
    private async Task AskCoachAsync()
    {
        if (IsCoachBusy) return;
        CoachError = null;
        UpdateCoachStatus();
        if (!IsCoachConfigured)
        {
            CoachError = "OpenAI key not configured — set OPENAI_API_KEY or paste a key below.";
            return;
        }

        IsCoachBusy = true;
        try
        {
            var review = _lastReview ?? await _review.GetWeeklyReviewAsync();
            var today = DateOnly.FromDateTime(DateTime.Now);
            var since = DateTimeOffset.Now.AddDays(-7);

            var daily = await _evidence.GetDailyActivityAsync(7);
            var recent = (await _evidence.GetRecentAsync(120))
                .Where(e => e.OccurredAt >= since)
                .ToList();
            var snapshot = await _insights.GetTodaySnapshotAsync();

            var input = new CoachInput(
                WeekStart: today.AddDays(-6),
                WeekEnd: today,
                Review: review,
                Daily: daily,
                RecentEvidence: recent,
                Answers: Array.Empty<CheckInAnswer>(),
                LastWeekCommitments: Array.Empty<string>(),
                CurrentStreakDays: snapshot.FocusStreakDays);

            ShowReport(await _coach.AnalyzeWeekAsync(input));
        }
        catch (LlmException ex)
        {
            CoachError = ex.Message;
        }
        catch (Exception ex)
        {
            CoachError = $"Coach unavailable: {ex.Message}";
        }
        finally
        {
            IsCoachBusy = false;
        }
    }

    /// <summary>Saves a pasted key to the DPAPI-protected store (never to SQLite, never logged).</summary>
    [RelayCommand]
    private void SaveApiKey()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ApiKeyInput)) return;
            _keys.SetKey(ApiKeyInput);
            CoachError = null;
        }
        catch (Exception ex)
        {
            CoachError = $"Couldn't save the key: {ex.Message}";
        }
        finally
        {
            ApiKeyInput = string.Empty;
            UpdateCoachStatus();
        }
    }

    [RelayCommand]
    private void ClearApiKey()
    {
        _keys.ClearKey();
        UpdateCoachStatus();
    }

    private void ShowReport(CoachReport report)
    {
        CoachHeadline = report.Headline;
        CoachQuestion = report.QuestionToSitWith;
        CoachCaveat = report.DataCaveat;
        CoachMeta = $"{report.Model} · {report.Usage.InputTokens:N0} in / {report.Usage.OutputTokens:N0} out tokens · suggestions only";

        Fill(CoachWins, report.Wins);
        Fill(CoachPatterns, report.Patterns);
        Fill(CoachRisks, report.Risks);
        Fill(CoachLastWeek, report.LastWeekReview);
        Fill(CoachCommitments, report.Commitments);
        Fill(CoachDropOrPause, report.DropOrPause);
        OnPropertyChanged(nameof(HasCoachLastWeek));
        OnPropertyChanged(nameof(HasCoachDropOrPause));

        HasCoachReport = true;
    }

    private void UpdateCoachStatus()
    {
        IsCoachConfigured = _coach.IsConfigured;
        CoachStatusText = IsCoachConfigured
            ? $"{_llm.Model} · key from {_keys.KeySource}"
            : "not configured";
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static string FormatHours(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes}m" : $"{time.Minutes}m";
}
