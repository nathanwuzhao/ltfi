using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Core.Domain;
using LTFI.Models;
using LTFI.Services;

namespace LTFI.ViewModels;

public partial class TaskPlannerViewModel : ViewModelBase
{
    private readonly TaskService _taskService;
    private bool _suppressSelectionLoad;

    public ObservableCollection<TaskItemViewModel> Tasks { get; } = [];

    public Array Priorities { get; } = Enum.GetValues<TaskPriority>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedTaskCommand))]
    private TaskItemViewModel? selectedTask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTaskCommand))]
    private string draftTitle = string.Empty;

    [ObservableProperty]
    private string draftDescription = string.Empty;

    [ObservableProperty]
    private TaskPriority selectedPriority = TaskPriority.Medium;

    [ObservableProperty]
    private string draftDueDateText = string.Empty;

    [ObservableProperty]
    private string requiredWorkMinutesText = "0";

    [ObservableProperty]
    private string pointsValueText = "5";

    [ObservableProperty]
    private string feedbackMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle))]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    private bool isCreatingNewTask = true;

    public TaskPlannerViewModel(TaskService taskService)
    {
        _taskService = taskService;
        _taskService.TasksChanged += OnTasksChanged;

        ReloadTasks(selectFirstTask: true);
        BeginNewTask();
    }

    public string Header => "Plan Tasks";

    public string EditorTitle => IsCreatingNewTask ? "New Task" : "Edit Task";

    public string SaveButtonText => IsCreatingNewTask ? "Create Task" : "Save Changes";

    public void LoadTask(Guid? taskId)
    {
        if (taskId is null)
        {
            BeginNewTask();
            return;
        }

        ReloadTasks(taskId, selectFirstTask: false);

        var task = _taskService.GetTaskById(taskId.Value);

        if (task is null)
        {
            BeginNewTask();
            return;
        }

        LoadDraft(TaskDraft.FromTask(task));
    }

    [RelayCommand]
    private void NewTask()
    {
        BeginNewTask();
    }

    [RelayCommand(CanExecute = nameof(CanSaveTask))]
    private void SaveTask()
    {
        if (!TryBuildDraft(out var draft))
        {
            return;
        }

        if (IsCreatingNewTask)
        {
            var createdTask = _taskService.CreateTask(draft);
            FeedbackMessage = "Task created.";
            LoadTask(createdTask.Id);
            return;
        }

        if (SelectedTask is null)
        {
            FeedbackMessage = "Select a task to save.";
            return;
        }

        _taskService.UpdateTask(SelectedTask.Id, draft);
        FeedbackMessage = "Changes saved.";
        LoadTask(SelectedTask.Id);
    }

    private bool CanSaveTask() => !string.IsNullOrWhiteSpace(DraftTitle);

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedTask))]
    private void DeleteSelectedTask()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var taskId = SelectedTask.Id;
        _taskService.DeleteTask(taskId);
        FeedbackMessage = "Task deleted.";
        BeginNewTask();
    }

    private bool CanDeleteSelectedTask() => SelectedTask is not null;

    partial void OnSelectedTaskChanged(TaskItemViewModel? value)
    {
        DeleteSelectedTaskCommand.NotifyCanExecuteChanged();

        if (_suppressSelectionLoad || value is null)
        {
            return;
        }

        LoadDraft(TaskDraft.FromTask(value.Task));
    }

    private void OnTasksChanged(object? sender, EventArgs e)
    {
        ReloadTasks(SelectedTask?.Id, selectFirstTask: !IsCreatingNewTask);
    }

    private void ReloadTasks(Guid? preferredTaskId = null, bool selectFirstTask = true)
    {
        var selectedId = preferredTaskId ?? SelectedTask?.Id;

        _suppressSelectionLoad = true;

        Tasks.Clear();

        foreach (var task in _taskService.GetAllTasks())
        {
            Tasks.Add(new TaskItemViewModel(task));
        }

        SelectedTask = Tasks.FirstOrDefault(task => task.Id == selectedId);

        if (SelectedTask is null && selectFirstTask)
        {
            SelectedTask = Tasks.FirstOrDefault();
        }

        _suppressSelectionLoad = false;
        DeleteSelectedTaskCommand.NotifyCanExecuteChanged();
    }

    private void BeginNewTask()
    {
        _suppressSelectionLoad = true;
        SelectedTask = null;
        _suppressSelectionLoad = false;

        LoadDraft(TaskDraft.CreateEmpty());
    }

    private void LoadDraft(TaskDraft draft)
    {
        IsCreatingNewTask = draft.TaskId is null;
        DraftTitle = draft.Title;
        DraftDescription = draft.Description ?? string.Empty;
        SelectedPriority = draft.Priority;
        DraftDueDateText = draft.DueDate?.ToString("yyyy-MM-dd") ?? string.Empty;
        RequiredWorkMinutesText = draft.RequiredWorkMinutes.ToString();
        PointsValueText = draft.PointsValue.ToString();
        FeedbackMessage = string.Empty;
    }

    private bool TryBuildDraft(out TaskDraft draft)
    {
        draft = new TaskDraft();

        if (string.IsNullOrWhiteSpace(DraftTitle))
        {
            FeedbackMessage = "Title is required.";
            return false;
        }

        if (!int.TryParse(RequiredWorkMinutesText, out var requiredWorkMinutes) || requiredWorkMinutes < 0)
        {
            FeedbackMessage = "Required work minutes must be a non-negative whole number.";
            return false;
        }

        if (!int.TryParse(PointsValueText, out var pointsValue) || pointsValue < 0)
        {
            FeedbackMessage = "Points must be a non-negative whole number.";
            return false;
        }

        DateTime? dueDate = null;

        if (!string.IsNullOrWhiteSpace(DraftDueDateText))
        {
            if (!DateTime.TryParse(DraftDueDateText, out var parsedDate))
            {
                FeedbackMessage = "Due date must be empty or use a valid date such as 2026-06-14.";
                return false;
            }

            dueDate = parsedDate.Date;
        }

        draft = new TaskDraft
        {
            TaskId = SelectedTask?.Id,
            Title = DraftTitle,
            Description = DraftDescription,
            Priority = SelectedPriority,
            DueDate = dueDate,
            RequiredWorkMinutes = requiredWorkMinutes,
            PointsValue = pointsValue
        };

        return true;
    }
}
