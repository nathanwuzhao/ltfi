using Avalonia.Controls;
using Avalonia.Input;
using LTFI.ViewModels;

namespace LTFI.Views;

public partial class CommandCenterView : UserControl
{
    public CommandCenterView()
    {
        InitializeComponent();

        // Belt-and-suspenders: the dense layout is measured inside a ScrollViewer, so pin the
        // content's max width to the control's actual width to guarantee it never overflows.
        SizeChanged += (_, e) => RootStack.MaxWidth = e.NewSize.Width;
    }

    /// <summary>A contribution-graph day was clicked: select it (or clear it) to filter the evidence feed.</summary>
    private async void OnContribCellTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ContribCellRow cell } && DataContext is CommandCenterViewModel vm)
        {
            try
            {
                await vm.ToggleDayAsync(cell);
            }
            catch (System.Exception ex)
            {
                Serilog.Log.Error(ex, "Filtering the evidence feed to {Day} failed", cell.Day);
            }
        }
    }
}
