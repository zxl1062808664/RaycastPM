using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using RaycastPM.Services;

namespace RaycastPM;

public partial class SystemMonitorWindow : Window
{
    private bool _dragMovePending;

    public SystemMonitorWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Topmost = true;
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 18;
        Top = workArea.Top + 18;
    }

    private void MonitorRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _dragMovePending = false;
            OpenSystemResourceMonitor();
            e.Handled = true;
            return;
        }

        _dragMovePending = e.ButtonState == MouseButtonState.Pressed;
    }

    private void MonitorRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragMovePending = false;
    }

    private void MonitorRoot_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragMovePending || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _dragMovePending = false;
        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private static void OpenSystemResourceMonitor()
    {
        try
        {
            var resourceMonitorPath = Path.Combine(Environment.SystemDirectory, "resmon.exe");
            Process.Start(new ProcessStartInfo
            {
                FileName = File.Exists(resourceMonitorPath) ? resourceMonitorPath : "resmon.exe",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "open resource monitor");
        }
    }
}
