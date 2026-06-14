using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Services;

namespace LTFI.ViewModels;

public partial class TodayViewModel : ViewModelBase
{
    private readonly TaskService _taskService;

    public ObservableCollection<TaskItemViewModel> Tasks { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTaskCommand))]
    private string newTaskTitle = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteSelectedTaskCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedTaskCommand))]
    private TaskItemViewModel? selectedTask;

    public TodayViewModel(TaskService taskService)
    {
        _taskService = taskService;

        foreach (var task in _taskService.GetTodayTasks())
        {
            Tasks.Add(new TaskItemViewModel(task));
        }

        SelectedTask = Tasks.Count > 0 ? Tasks[0] : null;
    }

    public string Header => "Today";

    [RelayCommand(CanExecute = nameof(CanAddTask))]
    private void AddTask()
    {
        var task = _taskService.AddTask(NewTaskTitle);
        var viewModel = new TaskItemViewModel(task);

        Tasks.Add(viewModel);
        SelectedTask = viewModel;
        NewTaskTitle = string.Empty;
    }

    private bool CanAddTask() => !string.IsNullOrWhiteSpace(NewTaskTitle);

    [RelayCommand(CanExecute = nameof(CanCompleteSelectedTask))]
    private void CompleteSelectedTask()
    {
        if (SelectedTask is null)
        {
            return;
        }

        _taskService.CompleteTask(SelectedTask.Task);
        SelectedTask.Refresh();
        RefreshTasks();
    }

    private bool CanCompleteSelectedTask() => SelectedTask?.CanComplete == true;

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedTask))]
    private void DeleteSelectedTask()
    {
        if (SelectedTask is null)
        {
            return;
        }

        var taskToDelete = SelectedTask;
        var index = Tasks.IndexOf(taskToDelete);

        _taskService.DeleteTask(taskToDelete.Task);
        Tasks.Remove(taskToDelete);

        if (Tasks.Count == 0)
        {
            SelectedTask = null;
            return;
        }

        var nextIndex = index < Tasks.Count ? index : Tasks.Count - 1;
        SelectedTask = Tasks[nextIndex];
    }

    private bool CanDeleteSelectedTask() => SelectedTask is not null;

    partial void OnSelectedTaskChanged(TaskItemViewModel? value)
    {
        CompleteSelectedTaskCommand.NotifyCanExecuteChanged();
        DeleteSelectedTaskCommand.NotifyCanExecuteChanged();
    }

    private void RefreshTasks()
    {
        foreach (var task in Tasks)
        {
            task.Refresh();
        }

        CompleteSelectedTaskCommand.NotifyCanExecuteChanged();
    }
}
