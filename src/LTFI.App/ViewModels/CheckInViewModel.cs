using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;

namespace LTFI.ViewModels;

/// <summary>One question on the check-in form; the answer is a live draft kept across navigations.</summary>
public partial class CheckInQuestion : ObservableObject
{
    public CheckInQuestion(int index, string prompt, bool isRequired)
    {
        Number = $"Q{index + 1}";
        Prompt = prompt;
        IsRequired = isRequired;
    }

    public string Number { get; }
    public string Prompt { get; }
    public bool IsRequired { get; }
    public string RequiredMark => IsRequired ? "REQUIRED" : string.Empty;

    [ObservableProperty] private string answer = string.Empty;
}

/// <summary>A past check-in, flattened for the history list.</summary>
public sealed record CheckInHistoryItem(string DateText, string Commitments, string Summary);

/// <summary>
/// The mandatory weekly check-in (plan §3.6). Shows this week's deterministic review numbers for
/// context, then the fixed <see cref="WeeklyCheckIn.Questions"/>. The shell locks navigation onto this
/// page while a check-in is due and not snoozed; <see cref="GateCleared"/> tells it to unlock.
/// </summary>
public partial class CheckInViewModel : ViewModelBase, IRefreshable
{
    private readonly IReflectionService _reflections;
    private readonly IReviewService _review;

    /// <summary>Raised after a successful submit or snooze, so the shell can re-check the gate.</summary>
    public event EventHandler? GateCleared;

    public string Header => "Weekly Check-In";

    public ObservableCollection<CheckInQuestion> Questions { get; }
    public ObservableCollection<CheckInHistoryItem> History { get; } = [];

    [ObservableProperty] private string focusText = "0m";
    [ObservableProperty] private int tasksCompleted;
    [ObservableProperty] private int stalledCount;
    [ObservableProperty] private string activeText = "0/0";

    [ObservableProperty] private bool isDue;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private string snoozeLabel = "SNOOZE 3H";
    [ObservableProperty] private string feedbackMessage = string.Empty;
    [ObservableProperty] private bool hasHistory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnoozeCommand))]
    private bool canSnooze;

    public CheckInViewModel(IReflectionService reflections, IReviewService review)
    {
        _reflections = reflections;
        _review = review;
        Questions = new ObservableCollection<CheckInQuestion>(
            WeeklyCheckIn.Questions.Select((q, i) =>
                new CheckInQuestion(i, q, i == WeeklyCheckIn.CommitmentsQuestionIndex)));
    }

    public async Task RefreshAsync()
    {
        // Answers are deliberately left alone: a half-written draft survives the 15s shell refresh.
        var r = await _review.GetWeeklyReviewAsync();
        FocusText = FormatHours(r.FocusTimeThisWeek);
        TasksCompleted = r.TasksCompletedThisWeek;
        StalledCount = r.StalledProjects.Count;
        ActiveText = $"{r.ActiveProjectCount}/{r.MaxActiveProjects}";

        ApplyStatus(await _reflections.GetWeeklyCheckInStatusAsync());

        History.Clear();
        foreach (var record in await _reflections.GetWeeklyCheckInHistoryAsync(5))
        {
            History.Add(ToHistoryItem(record));
        }
        HasHistory = History.Count > 0;
    }

    private void ApplyStatus(WeeklyCheckInStatus status)
    {
        IsDue = status.IsDue;
        CanSnooze = status.CanSnooze;
        SnoozeLabel = $"SNOOZE {ProjectPolicy.CheckInSnoozeHours}H · {status.SnoozesRemaining} LEFT";

        StatusText = status switch
        {
            { IsDue: false } => $"Done for this week. Next due {Format(status.NextDueAt)}.",
            { IsSnoozed: true, SnoozedUntil: { } until } => $"Due — snoozed until {until:HH:mm}.",
            { SnoozesRemaining: 0 } => "Due now. No snoozes left this week — submit to continue (short answers are fine).",
            _ => "Due now. Answer briefly; only the commitments are required."
        };
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        try
        {
            await _reflections.SaveWeeklyCheckInAsync(Questions.Select(q => (string?)q.Answer).ToList());
            foreach (var q in Questions)
            {
                q.Answer = string.Empty;
            }
            FeedbackMessage = string.Empty;
            await RefreshAsync();
            GateCleared?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSnooze))]
    private async Task SnoozeAsync()
    {
        try
        {
            ApplyStatus(await _reflections.SnoozeWeeklyCheckInAsync());
            FeedbackMessage = string.Empty;
            GateCleared?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
    }

    private static CheckInHistoryItem ToHistoryItem(WeeklyCheckInRecord record)
    {
        string AnswerAt(int i) => record.Answers.Count > i ? record.Answers[i].Answer : string.Empty;

        var summary = string.Join("\n", record.Answers
            .Select((a, i) => (a, i))
            .Where(x => x.i != WeeklyCheckIn.CommitmentsQuestionIndex && !string.IsNullOrWhiteSpace(x.a.Answer))
            .Select(x => $"Q{x.i + 1}  {x.a.Answer}"));

        return new CheckInHistoryItem(
            Format(record.CreatedAt),
            AnswerAt(WeeklyCheckIn.CommitmentsQuestionIndex),
            summary);
    }

    private static string Format(DateTimeOffset at) =>
        at.ToLocalTime().ToString("ddd MMM dd HH:mm", CultureInfo.InvariantCulture).ToUpperInvariant();

    private static string FormatHours(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes}m" : $"{time.Minutes}m";
}
