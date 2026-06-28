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
/// The landing page: tasks due today/overdue plus a light summary. Richer "current
/// operation" content arrives with focus sessions in Phase 2.
/// </summary>
public partial class TodayViewModel : ViewModelBase, IRefreshable
{
    private readonly ITaskService _taskService;
    private readonly IProjectService _projectService;

    public ObservableCollection<TaskItem> TodayTasks { get; } = [];

    public string Header => "Today";

    [ObservableProperty]
    private int activeProjectCount;

    [ObservableProperty]
    private bool hasTasks;

    public TodayViewModel(ITaskService taskService, IProjectService projectService)
    {
        _taskService = taskService;
        _projectService = projectService;
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

        var projects = await _projectService.GetAllAsync();
        ActiveProjectCount = projects.Count(p => p.Status == ProjectStatus.Active);

        HasTasks = TodayTasks.Count > 0;
        OnPropertyChanged(nameof(SummaryText));
    }

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
