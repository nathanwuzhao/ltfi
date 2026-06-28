using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Services;

namespace LTFI.ViewModels;

public partial class TodayViewModel : ViewModelBase
{
    private readonly TaskService _taskService;

    public event EventHandler<Guid?>? PlannerRequested;

    public ObservableCollection<TaskItemViewModel> Tasks { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartOrPauseSelectedTaskCommand))]
    [NotifyCanExecuteChangedFor(nameof(CompleteSelectedTaskCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedTaskCommand))]
    private TaskItemViewModel? selectedTask;

    public TodayViewModel(TaskService taskService)
    {
        _taskService = taskService;
        _taskService.TasksChanged += OnTasksChanged;
        ReloadTasks();
    }

    public string Header => "Today";

    [RelayCommand]
    private void CreateTask()
    {
        PlannerRequested?.Invoke(this, null);
    }

    [RelayCommand(CanExecute = nameof(CanStartOrPauseSelectedTask))]
    private void StartOrPauseSelectedTask()
    {
        if (SelectedTask is null)
        {
            return;
        }

        if (SelectedTask.CanPause)
        {
            _taskService.PauseTask(SelectedTask.Id);
            return;
        }

        _taskService.StartTask(SelectedTask.Id);
    }

    private bool CanStartOrPauseSelectedTask() =>
        SelectedTask is not null && !SelectedTask.IsCompleted;

    [RelayCommand(CanExecute = nameof(CanCompleteSelectedTask))]
    private void CompleteSelectedTask()
    {
        if (SelectedTask is null)
        {
            return;
        }

        _taskService.CompleteTask(SelectedTask.Id);
    }

    private bool CanCompleteSelectedTask() => SelectedTask?.CanComplete == true;

    [RelayCommand(CanExecute = nameof(CanEditSelectedTask))]
    private void EditSelectedTask()
    {
        if (SelectedTask is null)
        {
            return;
        }

        PlannerRequested?.Invoke(this, SelectedTask.Id);
    }

    private bool CanEditSelectedTask() => SelectedTask is not null;

    partial void OnSelectedTaskChanged(TaskItemViewModel? value)
    {
        StartOrPauseSelectedTaskCommand.NotifyCanExecuteChanged();
        CompleteSelectedTaskCommand.NotifyCanExecuteChanged();
        EditSelectedTaskCommand.NotifyCanExecuteChanged();
    }

    private void OnTasksChanged(object? sender, EventArgs e)
    {
        ReloadTasks(SelectedTask?.Id);
    }

    private void ReloadTasks(Guid? preferredTaskId = null)
    {
        var selectedId = preferredTaskId ?? SelectedTask?.Id;

        Tasks.Clear();

        foreach (var task in _taskService.GetTodayTasks())
        {
            Tasks.Add(new TaskItemViewModel(task));
        }

        SelectedTask = Tasks.FirstOrDefault(task => task.Id == selectedId)
            ?? Tasks.FirstOrDefault();

        StartOrPauseSelectedTaskCommand.NotifyCanExecuteChanged();
        CompleteSelectedTaskCommand.NotifyCanExecuteChanged();
        EditSelectedTaskCommand.NotifyCanExecuteChanged();
    }
}
