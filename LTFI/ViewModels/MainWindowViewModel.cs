using LTFI.Services;

namespace LTFI.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel()
    {
        Today = new TodayViewModel(new TaskService());
    }

    public TodayViewModel Today { get; }
}
