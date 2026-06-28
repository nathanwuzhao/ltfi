using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace LTFI.ViewModels;

/// <summary>
/// The app shell: owns the sidebar navigation items and the currently displayed page.
/// Pages are injected; the placeholder sections are created inline.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty]
    private NavItem? selectedNav;

    [ObservableProperty]
    private ViewModelBase? currentViewModel;

    public MainWindowViewModel(TodayViewModel today, ProjectsViewModel projects, TasksViewModel tasks)
    {
        NavItems =
        [
            new NavItem("Today", today),
            new NavItem("Projects", projects),
            new NavItem("Tasks", tasks),
            new NavItem("Focus", new PlaceholderViewModel(
                "Focus", "Focus sessions and the work timer arrive in Phase 2.")),
            new NavItem("Review", new PlaceholderViewModel(
                "Review", "Daily and weekly review loops arrive in Phase 3.")),
            new NavItem("Settings", new PlaceholderViewModel(
                "Settings", "Settings and optional integrations arrive in later phases.")),
        ];

        SelectedNav = NavItems[0];
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null)
        {
            return;
        }

        CurrentViewModel = value.ViewModel;

        if (value.ViewModel is IRefreshable refreshable)
        {
            _ = SafeRefreshAsync(refreshable);
        }
    }

    private static async Task SafeRefreshAsync(IRefreshable refreshable)
    {
        try
        {
            await refreshable.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to refresh {Page}", refreshable.GetType().Name);
        }
    }
}
