using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using LTFI.Core.Abstractions;
using LTFI.Core.Domain;
using Serilog;

namespace LTFI.ViewModels;

/// <summary>
/// Wraps a <see cref="SubtaskItem"/> so toggling its checkbox persists immediately.
/// </summary>
public partial class SubtaskItemViewModel : ViewModelBase
{
    private readonly ITaskService _taskService;
    private bool _suppress;

    [ObservableProperty]
    private bool isCompleted;

    public SubtaskItemViewModel(SubtaskItem model, ITaskService taskService)
    {
        _taskService = taskService;
        Id = model.Id;
        Title = model.Title;

        _suppress = true;
        IsCompleted = model.IsCompleted;
        _suppress = false;
    }

    public Guid Id { get; }

    public string Title { get; }

    partial void OnIsCompletedChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        _ = PersistAsync(value);
    }

    private async Task PersistAsync(bool value)
    {
        try
        {
            await _taskService.SetSubtaskCompletedAsync(Id, value);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update subtask {SubtaskId}", Id);
        }
    }
}
