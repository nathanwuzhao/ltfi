using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using LTFI.Core.Abstractions;

namespace LTFI.ViewModels;

/// <summary>
/// Weekly review (plan §3.6): a deterministic, local-data summary plus stalled-project warnings.
/// </summary>
public partial class ReviewViewModel : ViewModelBase, IRefreshable
{
    private readonly IReviewService _review;

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

    public ReviewViewModel(IReviewService review)
    {
        _review = review;
    }

    public string ActiveLimitText => $"{ActiveProjectCount} / {MaxActiveProjects} active";

    public async Task RefreshAsync()
    {
        var r = await _review.GetWeeklyReviewAsync();

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
    }

    private static string FormatHours(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes}m" : $"{time.Minutes}m";
}
