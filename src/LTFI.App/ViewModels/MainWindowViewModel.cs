using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LTFI.Services;

namespace LTFI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly TaskPlannerViewModel _planner;

    public MainWindowViewModel()
    {
        var taskService = new TaskService();

        Today = new TodayViewModel(taskService);
        _planner = new TaskPlannerViewModel(taskService);

        Today.PlannerRequested += OnPlannerRequested;
        CurrentViewModel = Today;
    }

    public TodayViewModel Today { get; }

    [ObservableProperty]
    private ViewModelBase currentViewModel = null!;

    [RelayCommand]
    private void ShowToday()
    {
        CurrentViewModel = Today;
    }

    [RelayCommand]
    private void ShowPlanner()
    {
        _planner.LoadTask(null);
        CurrentViewModel = _planner;
    }

    private void OnPlannerRequested(object? sender, Guid? taskId)
    {
        _planner.LoadTask(taskId);
        CurrentViewModel = _planner;
    }
}
