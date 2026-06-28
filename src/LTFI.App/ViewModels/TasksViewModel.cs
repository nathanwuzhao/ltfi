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
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.ViewModels;

/// <summary>A choice in the task's project dropdown; <c>Id == null</c> means "no project".</summary>
public sealed record ProjectOption(Guid? Id, string Name);

/// <summary>
/// Create, view, edit, and delete tasks, assign them to a project, and manage subtasks
/// (Phase 1 acceptance).
/// </summary>
public partial class TasksViewModel : ViewModelBase, IRefreshable
{
    private readonly ITaskService _taskService;
    private readonly IProjectService _projectService;
    private readonly List<TaskItem> _allTasks = [];
    private bool _suppressSelectionLoad;

    public ObservableCollection<TaskItem> Tasks { get; } = [];

    public ObservableCollection<ProjectOption> ProjectOptions { get; } = [];

    public ObservableCollection<SubtaskItemViewModel> Subtasks { get; } = [];

    public Array Statuses { get; } = Enum.GetValues<TaskStatus>();

    public Array Priorities { get; } = Enum.GetValues<TaskPriority>();

    public string Header => "Tasks";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteTaskCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSubtaskCommand))]
    [NotifyPropertyChangedFor(nameof(CanManageSubtasks))]
    private TaskItem? selectedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle))]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    [NotifyPropertyChangedFor(nameof(CanManageSubtasks))]
    [NotifyCanExecuteChangedFor(nameof(AddSubtaskCommand))]
    private bool isCreatingNew = true;

    [ObservableProperty]
    private string draftTitle = string.Empty;

    [ObservableProperty]
    private string draftDescription = string.Empty;

    [ObservableProperty]
    private TaskStatus selectedStatus = TaskStatus.Ready;

    [ObservableProperty]
    private TaskPriority selectedPriority = TaskPriority.Medium;

    [ObservableProperty]
    private string draftDueDateText = string.Empty;

    [ObservableProperty]
    private string requiredMinutesText = string.Empty;

    [ObservableProperty]
    private ProjectOption? selectedProjectOption;

    [ObservableProperty]
    private string newSubtaskTitle = string.Empty;

    [ObservableProperty]
    private string feedbackMessage = string.Empty;

    /// <summary>When false (default), Completed/Canceled tasks are hidden from the list.</summary>
    [ObservableProperty]
    private bool showArchived;

    public TasksViewModel(ITaskService taskService, IProjectService projectService)
    {
        _taskService = taskService;
        _projectService = projectService;
        BeginNewTask();
    }

    public string EditorTitle => IsCreatingNew ? "New Task" : "Edit Task";

    public string SaveButtonText => IsCreatingNew ? "Create Task" : "Save Changes";

    public bool CanManageSubtasks => !IsCreatingNew && SelectedTask is not null;

    public async Task RefreshAsync()
    {
        var selectedId = SelectedTask?.Id;

        var projects = await _projectService.GetAllAsync();
        ProjectOptions.Clear();
        ProjectOptions.Add(new ProjectOption(null, "(No project)"));
        foreach (var project in projects)
        {
            ProjectOptions.Add(new ProjectOption(project.Id, project.Title));
        }

        var tasks = await _taskService.GetAllAsync();
        _allTasks.Clear();
        _allTasks.AddRange(tasks);
        ApplyFilter(selectedId);
    }

    partial void OnShowArchivedChanged(bool value) => ApplyFilter(SelectedTask?.Id);

    private static bool IsArchived(TaskItem task) =>
        task.Status is TaskStatus.Completed or TaskStatus.Canceled;

    private void ApplyFilter(Guid? preferredId)
    {
        _suppressSelectionLoad = true;
        Tasks.Clear();
        foreach (var task in _allTasks.Where(t => ShowArchived || !IsArchived(t)))
        {
            Tasks.Add(task);
        }

        SelectedTask = Tasks.FirstOrDefault(t => t.Id == preferredId);
        _suppressSelectionLoad = false;

        if (SelectedTask is null)
        {
            BeginNewTask();
        }
        else
        {
            LoadFromTask(SelectedTask);
        }
    }

    [RelayCommand]
    private void NewTask() => BeginNewTask();

    [RelayCommand]
    private async Task SaveTaskAsync()
    {
        if (!TryBuildDraft(out var draft))
        {
            return;
        }

        try
        {
            if (IsCreatingNew)
            {
                var created = await _taskService.CreateAsync(draft);
                await RefreshAsync();
                SelectById(created.Id);
                FeedbackMessage = "Task created.";
            }
            else if (SelectedTask is not null)
            {
                var id = SelectedTask.Id;
                await _taskService.UpdateAsync(id, draft);
                await RefreshAsync();
                SelectById(id);
                FeedbackMessage = "Changes saved.";
            }
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteTask))]
    private async Task DeleteTaskAsync()
    {
        if (SelectedTask is null)
        {
            return;
        }

        try
        {
            await _taskService.DeleteAsync(SelectedTask.Id);
            await RefreshAsync();
            BeginNewTask();
            FeedbackMessage = "Task deleted.";
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
    }

    private bool CanDeleteTask() => SelectedTask is not null;

    [RelayCommand(CanExecute = nameof(CanManageSubtasks))]
    private async Task AddSubtaskAsync()
    {
        if (SelectedTask is null || string.IsNullOrWhiteSpace(NewSubtaskTitle))
        {
            return;
        }

        try
        {
            await _taskService.AddSubtaskAsync(SelectedTask.Id, NewSubtaskTitle);
            NewSubtaskTitle = string.Empty;
            await ReloadSubtasksAsync(SelectedTask.Id);
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteSubtaskAsync(SubtaskItemViewModel? subtask)
    {
        if (subtask is null || SelectedTask is null)
        {
            return;
        }

        try
        {
            await _taskService.DeleteSubtaskAsync(subtask.Id);
            await ReloadSubtasksAsync(SelectedTask.Id);
        }
        catch (Exception ex)
        {
            FeedbackMessage = ex.Message;
        }
    }

    partial void OnSelectedTaskChanged(TaskItem? value)
    {
        DeleteTaskCommand.NotifyCanExecuteChanged();

        if (_suppressSelectionLoad || value is null)
        {
            return;
        }

        LoadFromTask(value);
    }

    private void SelectById(Guid id) =>
        SelectedTask = Tasks.FirstOrDefault(t => t.Id == id);

    private void BeginNewTask()
    {
        _suppressSelectionLoad = true;
        SelectedTask = null;
        _suppressSelectionLoad = false;

        IsCreatingNew = true;
        DraftTitle = string.Empty;
        DraftDescription = string.Empty;
        SelectedStatus = TaskStatus.Ready;
        SelectedPriority = TaskPriority.Medium;
        DraftDueDateText = string.Empty;
        RequiredMinutesText = string.Empty;
        SelectedProjectOption = ProjectOptions.FirstOrDefault();
        NewSubtaskTitle = string.Empty;
        FeedbackMessage = string.Empty;
        Subtasks.Clear();
    }

    private void LoadFromTask(TaskItem task)
    {
        IsCreatingNew = false;
        DraftTitle = task.Title;
        DraftDescription = task.Description ?? string.Empty;
        SelectedStatus = task.Status;
        SelectedPriority = task.Priority;
        DraftDueDateText = task.DueAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        RequiredMinutesText = task.RequiredTime is { } required
            ? ((int)required.TotalMinutes).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        SelectedProjectOption = ProjectOptions.FirstOrDefault(o => o.Id == task.ProjectId)
            ?? ProjectOptions.FirstOrDefault();
        NewSubtaskTitle = string.Empty;
        FeedbackMessage = string.Empty;

        Subtasks.Clear();
        foreach (var subtask in task.Subtasks.OrderBy(s => s.SortOrder))
        {
            Subtasks.Add(new SubtaskItemViewModel(subtask, _taskService));
        }
    }

    private async Task ReloadSubtasksAsync(Guid taskId)
    {
        var task = await _taskService.GetByIdAsync(taskId);
        Subtasks.Clear();
        if (task is null)
        {
            return;
        }

        foreach (var subtask in task.Subtasks.OrderBy(s => s.SortOrder))
        {
            Subtasks.Add(new SubtaskItemViewModel(subtask, _taskService));
        }
    }

    private bool TryBuildDraft(out TaskDraft draft)
    {
        draft = new TaskDraft();

        if (string.IsNullOrWhiteSpace(DraftTitle))
        {
            FeedbackMessage = "Title is required.";
            return false;
        }

        DateTimeOffset? dueAt = null;
        if (!string.IsNullOrWhiteSpace(DraftDueDateText))
        {
            if (!DateTimeOffset.TryParse(DraftDueDateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                FeedbackMessage = "Due date must be empty or a valid date such as 2026-06-30.";
                return false;
            }

            dueAt = parsed;
        }

        int? requiredMinutes = null;
        if (!string.IsNullOrWhiteSpace(RequiredMinutesText))
        {
            if (!int.TryParse(RequiredMinutesText, out var parsed) || parsed < 0)
            {
                FeedbackMessage = "Required minutes must be a non-negative whole number.";
                return false;
            }

            requiredMinutes = parsed;
        }

        draft = new TaskDraft
        {
            ProjectId = SelectedProjectOption?.Id,
            Title = DraftTitle,
            Description = DraftDescription,
            Status = SelectedStatus,
            Priority = SelectedPriority,
            DueAt = dueAt,
            RequiredMinutes = requiredMinutes
        };

        return true;
    }
}
