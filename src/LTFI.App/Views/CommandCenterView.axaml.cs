using Avalonia.Controls;

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
}
