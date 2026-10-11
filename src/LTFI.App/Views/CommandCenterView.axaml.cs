using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
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

    /// <summary>
    /// An UPCOMING / WEEKLY COMMITMENTS row was clicked: open it in Tasks (or Projects), or with
    /// Ctrl held, on the Focus page ready to start. A click on the commitment checkbox is its own.
    /// </summary>
    private void OnOpenRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<CheckBox>(includeSelf: true) is not null)
        {
            return;
        }

        if (sender is Control { DataContext: IOpenableRow row } && DataContext is CommandCenterViewModel vm)
        {
            vm.OpenRow(row, startFocus: e.KeyModifiers.HasFlag(KeyModifiers.Control));
            e.Handled = true;
        }
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
