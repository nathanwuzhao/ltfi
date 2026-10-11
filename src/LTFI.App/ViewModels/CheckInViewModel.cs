using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;

namespace LTFI.ViewModels;

/// <summary>One free-text question on the check-in form; the answer is a live draft kept across navigations.</summary>
public partial class CheckInQuestion : ObservableObject
{
    public CheckInQuestion(int index, string prompt)
    {
        Index = index;
        Number = $"Q{index + 1}";
        Prompt = prompt;
    }

    public int Index { get; }
    public string Number { get; }
    public string Prompt { get; }

    [ObservableProperty] private string answer = string.Empty;
}

/// <summary>An entry in the "link to reminder" picker; <see cref="Id"/> null is "no link".</summary>
public sealed record LinkOption(Guid? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One of the three structured commitment rows (Q5): text plus an optional linked reminder.</summary>
public partial class CommitmentInput : ObservableObject
{
    public CommitmentInput(int index, ObservableCollection<LinkOption> linkOptions)
    {
        Number = $"{index + 1}";
        LinkOptions = linkOptions;
        selectedLink = linkOptions.FirstOrDefault();
    }

    public string Number { get; }

    /// <summary>Shared with the other rows (open reminder-backed tasks + "no link").</summary>
    public ObservableCollection<LinkOption> LinkOptions { get; }

    [ObservableProperty] private string text = string.Empty;
    [ObservableProperty] private LinkOption? selectedLink;

    /// <summary>The last-week commitment this row was carried over from, if any.</summary>
    public Guid? CarriedFrom { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    public void Clear()
    {
        Text = string.Empty;
        SelectedLink = LinkOptions.FirstOrDefault();
        CarriedFrom = null;
    }
}

/// <summary>What the user chose for one of last week's commitments in the review.</summary>
public enum ReviewChoice
{
    None,
    Kept,
    Missed,
    Carry
}

/// <summary>One of last week's commitments in the "LAST WEEK'S COMMITMENTS" review.</summary>
public partial class PastCommitment(CommitmentLine line, Action<PastCommitment, ReviewChoice, ReviewChoice> onChoice) : ObservableObject
{
    public CommitmentLine Line { get; } = line;
    public Guid Id => Line.Id;
    public string Text => Line.Text;
    public string Meta { get; init; } = string.Empty;

    /// <summary>Already kept (linked reminder completed, or checked on the Command Center).</summary>
    public bool IsAlreadyKept => Line.Status == CommitmentStatus.Kept;
    public bool NeedsChoice => !IsAlreadyKept;
    public string KeptLabel => Line.IsLinked ? "✓ KEPT · AUTO" : "✓ KEPT";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKeptChoice), nameof(IsMissedChoice), nameof(IsCarryChoice))]
    private ReviewChoice choice;

    public bool IsKeptChoice => Choice == ReviewChoice.Kept;
    public bool IsMissedChoice => Choice == ReviewChoice.Missed;
    public bool IsCarryChoice => Choice == ReviewChoice.Carry;

    partial void OnChoiceChanged(ReviewChoice oldValue, ReviewChoice newValue) => onChoice(this, oldValue, newValue);

    [RelayCommand] private void ChooseKept() => Choice = Choice == ReviewChoice.Kept ? ReviewChoice.None : ReviewChoice.Kept;
    [RelayCommand] private void ChooseMissed() => Choice = Choice == ReviewChoice.Missed ? ReviewChoice.None : ReviewChoice.Missed;
    [RelayCommand] private void ChooseCarry() => Choice = Choice == ReviewChoice.Carry ? ReviewChoice.None : ReviewChoice.Carry;
}

/// <summary>A past check-in, flattened for the history list.</summary>
public sealed record CheckInHistoryItem(string DateText, string Commitments, string Summary);

/// <summary>
/// The mandatory weekly check-in (plan §3.6). It reviews the Mon–Sun week that is ending (window
/// Sat 00:00 → Sun 23:59 by default, see <see cref="CheckInSchedule"/>). Shows this week's
/// deterministic review numbers for context, the commitments that applied to the reviewed week to
/// settle (kept / missed / carry over), then the fixed <see cref="WeeklyCheckIn.Questions"/> — Q5 as
/// three structured commitment rows for next week, each optionally linked to an open reminder.
/// Mon–Fri with last week reviewed the form is hidden and the page says when the next window opens.
/// The shell locks navigation onto this page from the gate time (or while overdue) until submitted
/// or snoozed; <see cref="GateCleared"/> tells it to re-check.
/// </summary>
public partial class CheckInViewModel : ViewModelBase, IRefreshable
{
    private static readonly LinkOption NoLink = new(null, "— no reminder link —");

    private readonly IReflectionService _reflections;
    private readonly IReviewService _review;
    private readonly ICommitmentService _commitments;
    private bool _restoringChoices;

    /// <summary>Raised after a successful submit or snooze, so the shell can re-check the gate.</summary>
    public event EventHandler? GateCleared;

    public string Header => "Weekly Check-In";

    /// <summary>Free-text questions before the commitments (Q1–Q4).</summary>
    public ObservableCollection<CheckInQuestion> Questions { get; }

    /// <summary>Free-text questions after the commitments (Q6).</summary>
    public ObservableCollection<CheckInQuestion> QuestionsAfter { get; }

    public string CommitmentsNumber => $"Q{WeeklyCheckIn.CommitmentsQuestionIndex + 1}";
    public string CommitmentsPrompt => WeeklyCheckIn.Questions[WeeklyCheckIn.CommitmentsQuestionIndex];

    public ObservableCollection<LinkOption> LinkOptions { get; } = [NoLink];
    public ObservableCollection<CommitmentInput> CommitmentInputs { get; }
    public ObservableCollection<PastCommitment> PastCommitments { get; } = [];
    public ObservableCollection<CheckInHistoryItem> History { get; } = [];

    [ObservableProperty] private string focusText = "0m";
    [ObservableProperty] private int tasksCompleted;
    [ObservableProperty] private int stalledCount;
    [ObservableProperty] private string activeText = "0/0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAmber), nameof(IsCalm))]
    private bool isDue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAmber))]
    private bool isOverdue;

    /// <summary>Status line colours: amber while due, red when overdue, green when done.</summary>
    public bool IsAmber => IsDue && !IsOverdue;
    public bool IsCalm => !IsDue;

    /// <summary>The form (review + questions + submit) is there; Mon–Fri with last week reviewed it is not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPastCommitments))]
    private bool canSubmit;

    /// <summary>The gate is (or would be) up: closing or overdue. Only then can it be snoozed.</summary>
    [ObservableProperty] private bool isGatePhase;

    /// <summary>"Reviews MON OCT 5 – SUN OCT 11 · due SUN 23:59" — the convention, in one line.</summary>
    [ObservableProperty] private string conventionText = string.Empty;

    /// <summary>"Next check-in opens SAT OCT 17" (shown when the form isn't available).</summary>
    [ObservableProperty] private string nextOpensText = string.Empty;

    [ObservableProperty] private string closedDetail = string.Empty;
    [ObservableProperty] private string pastHeader = "COMMITMENTS UNDER REVIEW";
    [ObservableProperty] private string submitLabel = "SUBMIT CHECK-IN";
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private string snoozeLabel = "SNOOZE 3H";
    [ObservableProperty] private string feedbackMessage = string.Empty;
    [ObservableProperty] private bool hasHistory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPastCommitments))]
    private bool hasPastCommitments;

    public bool ShowPastCommitments => HasPastCommitments && CanSubmit;
    [ObservableProperty] private string pastSummary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SnoozeCommand))]
    private bool canSnooze;

    public CheckInViewModel(IReflectionService reflections, IReviewService review, ICommitmentService commitments)
    {
        _reflections = reflections;
        _review = review;
        _commitments = commitments;

        var free = WeeklyCheckIn.Questions
            .Select((q, i) => (q, i))
            .Where(x => x.i != WeeklyCheckIn.CommitmentsQuestionIndex)
            .Select(x => new CheckInQuestion(x.i, x.q))
            .ToList();
        Questions = new ObservableCollection<CheckInQuestion>(free.Where(q => q.Index < WeeklyCheckIn.CommitmentsQuestionIndex));
        QuestionsAfter = new ObservableCollection<CheckInQuestion>(free.Where(q => q.Index > WeeklyCheckIn.CommitmentsQuestionIndex));
        CommitmentInputs = new ObservableCollection<CommitmentInput>(
            Enumerable.Range(0, WeeklyCommitments.MaxPerCheckIn).Select(i => new CommitmentInput(i, LinkOptions)));
    }

    public async Task RefreshAsync()
    {
        // Answers and commitment rows are deliberately left alone: a half-written draft survives
        // the 15s shell refresh.
        var r = await _review.GetWeeklyReviewAsync();
        FocusText = FormatHours(r.FocusTimeThisWeek);
        TasksCompleted = r.TasksCompletedThisWeek;
        StalledCount = r.StalledProjects.Count;
        ActiveText = $"{r.ActiveProjectCount}/{r.MaxActiveProjects}";

        ApplyStatus(await _reflections.GetWeeklyCheckInStatusAsync());

        await LoadLinkOptionsAsync();
        await LoadPastCommitmentsAsync();

        History.Clear();
        foreach (var record in await _reflections.GetWeeklyCheckInHistoryAsync(5))
        {
            History.Add(ToHistoryItem(record));
        }
        HasHistory = History.Count > 0;
    }

    private async Task LoadLinkOptionsAsync()
    {
        var tasks = await _commitments.GetLinkableTasksAsync();
        var fresh = new List<LinkOption> { NoLink };
        fresh.AddRange(tasks.Select(t => new LinkOption(t.Id, LinkLabel(t.Title, t.Area, t.DueAt))));

        if (fresh.SequenceEqual(LinkOptions))
        {
            return;
        }

        // Rebuilding the list can null a ComboBox's selection; remember and restore by id.
        var chosen = CommitmentInputs.Select(c => c.SelectedLink?.Id).ToList();
        LinkOptions.Clear();
        foreach (var o in fresh)
        {
            LinkOptions.Add(o);
        }

        for (var i = 0; i < CommitmentInputs.Count; i++)
        {
            CommitmentInputs[i].SelectedLink = LinkOptions.FirstOrDefault(o => o.Id == chosen[i]) ?? NoLink;
        }
    }

    private async Task LoadPastCommitmentsAsync()
    {
        var lines = await _commitments.GetPendingReviewAsync();
        var previous = PastCommitments.ToDictionary(p => p.Id, p => p.Choice);
        if (lines.Select(l => (l.Id, l.Status)).SequenceEqual(PastCommitments.Select(p => (p.Id, p.Line.Status))))
        {
            return; // unchanged: keep the user's choices and carried rows as they are
        }

        PastCommitments.Clear();
        _restoringChoices = true;
        try
        {
            foreach (var line in lines)
            {
                var item = new PastCommitment(line, OnPastChoice) { Meta = Meta(line) };
                PastCommitments.Add(item);
                if (previous.TryGetValue(line.Id, out var choice) && item.NeedsChoice)
                {
                    item.Choice = choice; // a choice made before a refresh survives it (rows untouched)
                }
            }
        }
        finally
        {
            _restoringChoices = false;
        }

        HasPastCommitments = PastCommitments.Count > 0;
        var kept = PastCommitments.Count(p => p.IsAlreadyKept);
        PastSummary = $"{kept} / {PastCommitments.Count} KEPT";
    }

    private void OnPastChoice(PastCommitment item, ReviewChoice oldValue, ReviewChoice newValue)
    {
        if (_restoringChoices)
        {
            return;
        }

        if (oldValue == ReviewChoice.Carry)
        {
            // Un-carry: clear the row it filled, unless the user already rewrote it.
            var row = CommitmentInputs.FirstOrDefault(c => c.CarriedFrom == item.Id);
            if (row is not null)
            {
                if (row.Text == item.Text)
                {
                    row.Clear();
                }
                else
                {
                    row.CarriedFrom = null;
                }
            }
        }

        if (newValue == ReviewChoice.Carry)
        {
            var row = CommitmentInputs.FirstOrDefault(c => c.IsEmpty);
            if (row is null)
            {
                FeedbackMessage = "All three commitment rows are in use — clear one to carry this over.";
                item.Choice = oldValue == ReviewChoice.Carry ? ReviewChoice.None : oldValue;
                return;
            }

            row.Text = item.Text;
            row.CarriedFrom = item.Id;
            // Keep the link only while the reminder is still open (it's in the picker then).
            row.SelectedLink = item.Line.LinkedTaskOpen
                ? LinkOptions.FirstOrDefault(o => o.Id == item.Line.LinkedTaskId) ?? NoLink
                : NoLink;
        }

        FeedbackMessage = string.Empty;
    }

    private void ApplyStatus(WeeklyCheckInStatus status)
    {
        IsDue = status.IsDue;
        IsOverdue = status.IsOverdue;
        CanSubmit = status.CanSubmit;
        IsGatePhase = status.IsGatePhase;
        CanSnooze = status.CanSnooze;
        SnoozeLabel = $"SNOOZE {status.SnoozeHours}H · {status.SnoozesRemaining} LEFT";
        SubmitLabel = status.IsRevision ? "REVISE CHECK-IN" : "SUBMIT CHECK-IN";

        var range = CheckInFormat.WeekRange(status.ReviewWeek);
        var due = CheckInFormat.DayTime(status.DueAt);
        var opens = CheckInFormat.Opens(status.NextOpensAt);

        // The convention line names the week the form reviews; when the form is closed, the next one.
        ConventionText = status.CanSubmit
            ? $"Reviews {range} · due {due}"
            : $"Reviews {CheckInFormat.WeekRange(status.ReviewWeek.AddDays(7))} · due {CheckInFormat.DayTime(status.DueAt.AddDays(7))}";
        NextOpensText = $"Next check-in opens {opens}";
        ClosedDetail = $"{range} is reviewed. The form comes back when the next window opens; "
                       + $"its commitments will cover {CheckInFormat.WeekRange(status.ReviewWeek.AddDays(14))}.";
        PastHeader = $"COMMITMENTS FOR {range}";

        var snooze = status switch
        {
            { IsSnoozed: true, SnoozedUntil: { } until } => $" Snoozed until {until:HH:mm}.",
            { IsGatePhase: true, SnoozesRemaining: 0 } => " No snoozes left — submit to continue (short answers are fine).",
            _ => string.Empty
        };

        StatusText = status.Phase switch
        {
            CheckInPhase.Done when status.IsRevision =>
                $"Done for {range}. You can revise it until {due} (no extra points). Next check-in opens {opens}.",
            CheckInPhase.Done => $"Done for {range}. Next check-in opens {opens}.",
            CheckInPhase.Open =>
                $"Window open — due {due}. The app locks from {CheckInFormat.DayTime(status.GateAt)} until you submit.",
            CheckInPhase.Closing => $"Due {due}. Answer briefly; only one commitment is required." + snooze,
            _ => $"Overdue — {due} passed. Submitting now reviews {range} (late)." + snooze
        };
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (!CanSubmit)
        {
            FeedbackMessage = NextOpensText + ".";
            return;
        }

        try
        {
            var answers = new string?[WeeklyCheckIn.Questions.Count];
            foreach (var q in Questions.Concat(QuestionsAfter))
            {
                answers[q.Index] = q.Answer;
            }

            var drafts = CommitmentInputs
                .Select(c => new CommitmentDraft(c.Text, c.SelectedLink?.Id))
                .ToList();

            // Kept → Kept; Missed / Carry over / no choice → Missed (the service's default).
            var resolutions = PastCommitments
                .Where(p => p.NeedsChoice && p.Choice != ReviewChoice.None)
                .ToDictionary(p => p.Id, p => p.Choice == ReviewChoice.Kept ? CommitmentStatus.Kept : CommitmentStatus.Missed);

            await _reflections.SaveWeeklyCheckInAsync(answers, drafts, resolutions);

            foreach (var q in Questions.Concat(QuestionsAfter))
            {
                q.Answer = string.Empty;
            }
            foreach (var c in CommitmentInputs)
            {
                c.Clear();
            }
            PastCommitments.Clear();
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

    /// <summary>"title · #area · due OCT 08" for the link picker.</summary>
    internal static string LinkLabel(string title, string? area, DateTimeOffset? due)
    {
        var parts = new List<string> { title };
        if (!string.IsNullOrWhiteSpace(area))
        {
            parts.Add($"#{area}");
        }
        if (due is { } d)
        {
            parts.Add($"due {FormatDue(d)}");
        }
        return string.Join(" · ", parts);
    }

    internal static string FormatDue(DateTimeOffset due) =>
        due.ToLocalTime().ToString("MMM dd", CultureInfo.InvariantCulture).ToUpperInvariant();

    private static string Meta(CommitmentLine line)
    {
        if (!line.IsLinked)
        {
            return string.Empty;
        }

        var parts = new List<string> { "↪ " + (line.LinkedTaskTitle ?? "reminder") };
        if (!string.IsNullOrWhiteSpace(line.LinkedArea))
        {
            parts.Add($"#{line.LinkedArea}");
        }
        if (line.LinkedDue is { } d)
        {
            parts.Add($"due {FormatDue(d)}");
        }
        if (!line.LinkedTaskOpen && line.Status != CommitmentStatus.Kept)
        {
            parts.Add("reminder closed");
        }
        return string.Join(" · ", parts);
    }

    private static CheckInHistoryItem ToHistoryItem(WeeklyCheckInRecord record)
    {
        string AnswerAt(int i) => record.Answers.Count > i ? record.Answers[i].Answer : string.Empty;

        var summary = string.Join("\n", record.Answers
            .Select((a, i) => (a, i))
            .Where(x => x.i != WeeklyCheckIn.CommitmentsQuestionIndex && !string.IsNullOrWhiteSpace(x.a.Answer))
            .Select(x => $"Q{x.i + 1}  {x.a.Answer}"));

        var late = record.IsLate ? " (LATE)" : string.Empty;
        return new CheckInHistoryItem(
            $"{Format(record.CreatedAt)} · FOR {CheckInFormat.WeekRange(record.ReviewWeek)}{late}",
            AnswerAt(WeeklyCheckIn.CommitmentsQuestionIndex),
            summary);
    }

    private static string Format(DateTimeOffset at) =>
        at.ToLocalTime().ToString("ddd MMM dd HH:mm", CultureInfo.InvariantCulture).ToUpperInvariant();

    private static string FormatHours(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes}m" : $"{time.Minutes}m";
}

/// <summary>Upper-case date labels for the weekly check-in ("MON OCT 5 – SUN OCT 11", "SUN 23:59").</summary>
internal static class CheckInFormat
{
    /// <summary>"MON OCT 5 – SUN OCT 11" for the Mon–Sun week starting <paramref name="monday"/>.</summary>
    public static string WeekRange(DateOnly monday) => $"{Day(monday)} – {Day(monday.AddDays(6))}";

    public static string Day(DateOnly day) =>
        day.ToString("ddd MMM d", CultureInfo.InvariantCulture).ToUpperInvariant();

    /// <summary>"SUN 23:59".</summary>
    public static string DayTime(DateTimeOffset at) =>
        at.ToString("ddd HH:mm", CultureInfo.InvariantCulture).ToUpperInvariant();

    /// <summary>"SAT OCT 17", plus the time when it isn't midnight ("FRI OCT 16 17:00").</summary>
    public static string Opens(DateTimeOffset at) =>
        Day(DateOnly.FromDateTime(at.DateTime))
        + (at.TimeOfDay == TimeSpan.Zero ? string.Empty : at.ToString(" HH:mm", CultureInfo.InvariantCulture));
}
