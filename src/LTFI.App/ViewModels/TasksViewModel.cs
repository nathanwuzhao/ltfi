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
using LTFI.Infrastructure.Settings;
using TaskStatus = LTFI.Core.Domain.TaskStatus;

namespace LTFI.ViewModels;

/// <summary>A choice in the task's project dropdown; <c>Id == null</c> means "no project".</summary>
public sealed record ProjectOption(Guid? Id, string Name, bool IsStanding = false);

/// <summary>A choice in the task's area dropdown (areas of the selected project); <c>Id == null</c> = no area.</summary>
public sealed record AreaOption(Guid? Id, string Name);

/// <summary>
/// View, edit, and delete tasks, assign them to a project/area, and manage subtasks. Every task is
/// an iCloud reminder: "New Task" creates one pending on the iPhone (via the outbox), never a
/// local-only task.
/// </summary>
public partial class TasksViewModel : ViewModelBase, IRefreshable
{
    private readonly ITaskService _taskService;
    private readonly IProjectService _projectService;
    private readonly IAreaService _areaService;
    private readonly RemindersSettings _remindersSettings;
    private readonly List<TaskItem> _allTasks = [];
    private bool _suppressSelectionLoad;
    private bool _suppressProjectChange;
    private int _areaLoadVersion;

    public ObservableCollection<TaskItem> Tasks { get; } = [];

    public ObservableCollection<ProjectOption> ProjectOptions { get; } = [];

    public ObservableCollection<AreaOption> AreaOptions { get; } = [];

    public ObservableCollection<SubtaskItemViewModel> Subtasks { get; } = [];

    public Array Statuses { get; } = Enum.GetValues<TaskStatus>();

    public Array Priorities { get; } = Enum.GetValues<TaskPriority>();

    public string Header => "Tasks";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteTaskCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddSubtaskCommand))]
    [NotifyPropertyChangedFor(nameof(CanManageSubtasks))]
    [NotifyPropertyChangedFor(nameof(IsPlacementLocked))]
    [NotifyPropertyChangedFor(nameof(CanEditPlacement))]
    private TaskItem? selectedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreateTargetText))]
    private AreaOption? selectedAreaOption;

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
    [NotifyPropertyChangedFor(nameof(CreateTargetText))]
    private ProjectOption? selectedProjectOption;

    [ObservableProperty]
    private string newSubtaskTitle = string.Empty;

    [ObservableProperty]
    private string feedbackMessage = string.Empty;

    /// <summary>When false (default), Completed/Canceled tasks are hidden from the list.</summary>
    [ObservableProperty]
    private bool showArchived;

    public TasksViewModel(
        ITaskService taskService,
        IProjectService projectService,
        IAreaService areaService,
        RemindersSettings remindersSettings)
    {
        _taskService = taskService;
        _projectService = projectService;
        _areaService = areaService;
        _remindersSettings = remindersSettings;
        BeginNewTask();
    }

    public string EditorTitle => IsCreatingNew ? "New Task → iPhone Reminders" : "Edit Task";

    public string SaveButtonText => IsCreatingNew ? "Create on iPhone" : "Save Changes";

    public bool CanManageSubtasks => !IsCreatingNew && SelectedTask is not null;

    /// <summary>
    /// A reminder made on the phone gets its project/area from its list on every sync, so editing
    /// them here would be undone; only tasks LTFI created keep the placement LTFI gives them.
    /// </summary>
    public bool IsPlacementLocked =>
        !IsCreatingNew && SelectedTask is { IsExternal: true } task && !ReminderRules.IsLtfiCreatedUrl(task.ExternalId);

    public bool CanEditPlacement => !IsPlacementLocked;

    /// <summary>Which iPhone list a new task will be created in.</summary>
    public string CreateTargetText
    {
        get
        {
            var list = SelectedProjectOption is { IsStanding: true }
                ? SelectedAreaOption?.Id is null ? "(pick an area — it is the list)" : SelectedAreaOption.Name
                : string.IsNullOrWhiteSpace(_remindersSettings.LtfiList) ? "LTFI" : _remindersSettings.LtfiList.Trim();
            return $"Creates a reminder in the iPhone list \"{list}\". It shows as PENDING ON iPHONE until the " +
                   "LTFI Apply Shortcut has run and the next export comes back.";
        }
    }

    public async Task RefreshAsync()
    {
        var selectedId = SelectedTask?.Id;

        var projects = await _projectService.GetAllAsync();
        _suppressProjectChange = true;
        ProjectOptions.Clear();
        ProjectOptions.Add(new ProjectOption(null, "(No project)"));
        foreach (var project in projects)
        {
            ProjectOptions.Add(new ProjectOption(project.Id, project.Title, project.IsStanding));
        }
        _suppressProjectChange = false;

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
                FeedbackMessage = $"Queued for your iPhone (list \"{created.ExternalList}\"). It appears in Reminders after the LTFI Apply Shortcut runs.";
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

    /// <summary>
    /// The list row's checkbox: completes the task in one click via the same service path as the
    /// editor (focus gate + outbox "complete"). Un-completing is only offered for local tasks — the
    /// outbox has no "reopen" command, so a completed iCloud reminder stays completed.
    /// </summary>
    [RelayCommand]
    private async Task ToggleCompleteAsync(TaskItem? task)
    {
        if (task is null)
        {
            return;
        }

        var wasCompleted = task.Status == TaskStatus.Completed;
        string message;
        try
        {
            if (wasCompleted)
            {
                if (task.IsExternal)
                {
                    message = "A completed iCloud reminder can't be re-opened from LTFI — un-tick it on your iPhone.";
                }
                else
                {
                    await _taskService.SetStatusAsync(task.Id, TaskStatus.Ready);
                    message = $"\"{task.Title}\" re-opened.";
                }
            }
            else
            {
                await _taskService.SetStatusAsync(task.Id, TaskStatus.Completed);
                message = task.IsExternal
                    ? $"\"{task.Title}\" completed — it is ticked off on your iPhone after the LTFI Apply Shortcut runs."
                    : $"\"{task.Title}\" completed.";
            }
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }

        // Always reload: on failure this also resets the checkbox the click already toggled.
        await RefreshAsync();
        FeedbackMessage = message;
    }

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

    partial void OnIsCreatingNewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPlacementLocked));
        OnPropertyChanged(nameof(CanEditPlacement));
    }

    // The user picked another project: offer that project's areas.
    partial void OnSelectedProjectOptionChanged(ProjectOption? value)
    {
        if (!_suppressProjectChange)
        {
            _ = LoadAreaOptionsAsync(value?.Id, null);
        }
    }

    /// <summary>Fills <see cref="AreaOptions"/> for a project and selects <paramref name="selectAreaId"/>.
    /// A version counter drops results of loads that were overtaken by a newer one.</summary>
    private async Task LoadAreaOptionsAsync(Guid? projectId, Guid? selectAreaId)
    {
        var version = ++_areaLoadVersion;
        var areas = projectId is { } id ? await _areaService.GetByProjectAsync(id) : [];
        if (version != _areaLoadVersion)
        {
            return;
        }

        AreaOptions.Clear();
        AreaOptions.Add(new AreaOption(null, "(No area)"));
        foreach (var area in areas)
        {
            AreaOptions.Add(new AreaOption(area.Id, area.Name));
        }

        SelectedAreaOption = AreaOptions.FirstOrDefault(o => o.Id == selectAreaId) ?? AreaOptions[0];
    }

    private void SetPlacement(Guid? projectId, Guid? areaId)
    {
        _suppressProjectChange = true;
        SelectedProjectOption = ProjectOptions.FirstOrDefault(o => o.Id == projectId) ?? ProjectOptions.FirstOrDefault();
        _suppressProjectChange = false;
        _ = LoadAreaOptionsAsync(SelectedProjectOption?.Id, areaId);
    }

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
        SetPlacement(null, null);
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
        SetPlacement(task.ProjectId, task.AreaId);
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

        // Due is a date only: a newly chosen date is stored as local midnight; an unchanged date keeps
        // the task's existing DueAt (including any time it already had).
        DateTimeOffset? dueAt = null;
        var dueText = DraftDueDateText?.Trim() ?? string.Empty;
        if (dueText.Length > 0)
        {
            var existingDue = IsCreatingNew ? null : SelectedTask?.DueAt;
            if (existingDue is { } existing
                && dueText == existing.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            {
                dueAt = existing;
            }
            else if (DateTime.TryParse(dueText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                dueAt = new DateTimeOffset(DateTime.SpecifyKind(parsed.Date, DateTimeKind.Unspecified),
                    TimeZoneInfo.Local.GetUtcOffset(parsed.Date));
            }
            else
            {
                FeedbackMessage = "Due date must be empty or a valid date such as 2026-06-30.";
                return false;
            }
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
            AreaId = SelectedAreaOption?.Id,
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
