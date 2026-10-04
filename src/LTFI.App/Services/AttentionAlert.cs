using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Serilog;

namespace LTFI.Services;

/// <summary>
/// Gets the user's attention when a pomodoro phase ends: a Windows system sound (beep elsewhere)
/// and the main window brought forward. Best effort — any failure is logged, never thrown.
/// </summary>
public static class AttentionAlert
{
    public static void Raise()
    {
        PlaySound();
        Dispatcher.UIThread.Post(BringToFront);
    }

    private static void PlaySound()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Media.SystemSounds.Exclamation.Play();
            }
            else
            {
                Console.Beep();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Attention sound failed");
        }
    }

    private static void BringToFront()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            // Toggling Topmost pops the window above others even when Activate() is refused
            // by the OS foreground-lock rules (the taskbar button flashes instead).
            window.Topmost = true;
            window.Activate();
            window.Topmost = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Bringing the window to attention failed");
        }
    }
}
