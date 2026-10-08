using Avalonia.Controls;
using LTFI.ViewModels;

namespace LTFI.Views;

public partial class TasksView : UserControl
{
    public TasksView()
    {
        InitializeComponent();

        // Section headers share the list with tasks but must not be clickable or focusable.
        // Containers are recycled, so every prepare sets both states.
        TaskList.ContainerPrepared += (_, e) =>
        {
            var isHeader = e.Container is ContentControl { Content: TaskGroupHeader };
            e.Container.IsHitTestVisible = !isHeader;
            e.Container.Focusable = !isHeader;
        };
    }
}
