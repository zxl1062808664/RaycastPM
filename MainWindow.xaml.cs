using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using RaycastPM.Models;
using RaycastPM.Services;
using RaycastPM.ViewModels;
using Forms = System.Windows.Forms;

namespace RaycastPM;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly GlobalHotKeyService _hotKeys = new();
    private readonly System.Drawing.Icon _appIcon = LoadTrayIcon();
    private readonly Forms.NotifyIcon _trayIcon;
    private SystemMonitorWindow? _systemMonitorWindow;
    private IntPtr _pasteTargetWindow;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _trayIcon = CreateTrayIcon();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        StateChanged += OnStateChanged;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hotKeys.Attach(hwnd);
        _viewModel.RegisterHotKeys(_hotKeys, section =>
        {
            if (section == AppSection.Clipboard)
            {
                CapturePasteTargetWindow();
            }

            RestoreWindow();
        });
        UpdateSystemMonitorWindow();
        UpdateUiActivityState();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SystemMonitorEnabled))
        {
            UpdateSystemMonitorWindow();
            UpdateUiActivityState();
        }
    }

    private void UpdateSystemMonitorWindow()
    {
        if (_viewModel.SystemMonitorEnabled)
        {
            if (_systemMonitorWindow is null)
            {
                _systemMonitorWindow = new SystemMonitorWindow
                {
                    DataContext = _viewModel
                };
                _systemMonitorWindow.IsVisibleChanged += OnSystemMonitorWindowIsVisibleChanged;
                _systemMonitorWindow.Closed += (_, _) =>
                {
                    _systemMonitorWindow.IsVisibleChanged -= OnSystemMonitorWindowIsVisibleChanged;
                    _systemMonitorWindow = null;
                    UpdateUiActivityState();
                };
            }

            _systemMonitorWindow.Topmost = true;
            if (!_systemMonitorWindow.IsVisible)
            {
                _systemMonitorWindow.Show();
            }

            UpdateUiActivityState();
            return;
        }

        _systemMonitorWindow?.Hide();
        UpdateUiActivityState();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateUiActivityState();
    }

    private void OnSystemMonitorWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateUiActivityState();
    }

    private void UpdateUiActivityState()
    {
        _viewModel.SetUiActivityState(IsVisible, _systemMonitorWindow?.IsVisible == true);
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(CreateTrayMenuItem("退出", ExitFromTray));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(CreateTrayMenuItem("导航", () => ShowFromTray(AppSection.Launcher)));
        menu.Items.Add(CreateTrayMenuItem("剪贴板", () => ShowFromTray(AppSection.Clipboard)));
        menu.Items.Add(CreateTrayMenuItem("记事本", () => ShowFromTray(AppSection.Notes)));
        menu.Items.Add(CreateTrayMenuItem("设置", () => ShowFromTray(AppSection.Settings)));

        var trayIcon = new Forms.NotifyIcon
        {
            Text = "RaycastPM",
            Icon = _appIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => ShowFromTray(AppSection.Launcher);
        return trayIcon;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        return File.Exists(iconPath)
            ? new System.Drawing.Icon(iconPath)
            : (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private static Forms.ToolStripMenuItem CreateTrayMenuItem(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowFromTray(AppSection section)
    {
        Dispatcher.Invoke(() =>
        {
            if (section == AppSection.Clipboard)
            {
                CapturePasteTargetWindow();
            }

            _viewModel.ShowSection(section);
            RestoreWindow();
        });
    }

    private void RestoreWindow()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        UpdateUiActivityState();
    }

    private bool ShouldAutoHide()
    {
        return !_isExiting && _viewModel.SelectedSection != AppSection.Notes;
    }

    private void HideInterface()
    {
        Hide();
    }

    private void ExitFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            _isExiting = true;
            Close();
        });
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (ShouldAutoHide())
        {
            HideInterface();
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ShouldAutoHide())
        {
            e.Handled = true;
            HideInterface();
            return;
        }

        if (e.Key == Key.Enter
            && _viewModel.SelectedSection == AppSection.Launcher
            && _viewModel.OpenSelectedAppCommand.CanExecute(null))
        {
            e.Handled = true;
            _viewModel.OpenSelectedAppCommand.Execute(null);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _viewModel.Save();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _systemMonitorWindow?.Close();
        _systemMonitorWindow = null;
        _viewModel.Dispose();
        _hotKeys.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _appIcon.Dispose();
    }

    private void WindowChrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveElement(e.OriginalSource))
        {
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private static bool IsInteractiveElement(object source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.Primitives.Selector
                or System.Windows.Controls.PasswordBox)
            {
                return true;
            }

            DependencyObject? visualParent = null;
            try
            {
                visualParent = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            catch
            {
            }

            current = visualParent ?? LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsInsideListBoxItem(object source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is System.Windows.Controls.ListBoxItem)
            {
                return true;
            }

            DependencyObject? visualParent = null;
            try
            {
                visualParent = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            catch
            {
            }

            current = visualParent ?? LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideInterface();
    }

    private void NotesHistoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox { SelectedItem: not null } list)
        {
            return;
        }

        if (!list.IsMouseOver && !list.IsKeyboardFocusWithin)
        {
            return;
        }

        if (list.Tag is System.Windows.Controls.Primitives.Popup popup)
        {
            popup.IsOpen = false;
        }
    }

    private void AppsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.OpenSelectedAppCommand.CanExecute(null))
        {
            _viewModel.OpenSelectedAppCommand.Execute(null);
        }
    }

    private void ClipboardList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsInsideListBoxItem(e.OriginalSource))
        {
            return;
        }

        if (_viewModel.CopySelectedClipboardCommand.CanExecute(null))
        {
            _viewModel.CopySelectedClipboardCommand.Execute(null);
            e.Handled = true;
            HideInterface();
            _ = PasteIntoCapturedWindowAsync();
        }
    }

    private void CapturePasteTargetWindow()
    {
        _pasteTargetWindow = IntPtr.Zero;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsWindow(foreground))
        {
            return;
        }

        if (IsCurrentProcessWindow(foreground) || IsShellWindow(foreground))
        {
            return;
        }

        _pasteTargetWindow = foreground;
    }

    private async Task PasteIntoCapturedWindowAsync()
    {
        var target = _pasteTargetWindow;
        if (target == IntPtr.Zero || !IsWindow(target))
        {
            return;
        }

        await Task.Delay(120);

        if (IsIconic(target))
        {
            ShowWindow(target, SwRestore);
        }

        SetForegroundWindow(target);
        await Task.Delay(80);
        SendCtrlVPaste();
    }

    private static bool IsCurrentProcessWindow(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        return processId == Environment.ProcessId;
    }

    private static bool IsShellWindow(IntPtr window)
    {
        var className = new StringBuilder(128);
        if (GetClassName(window, className, className.Capacity) == 0)
        {
            return false;
        }

        return className.ToString() is "Shell_TrayWnd" or "NotifyIconOverflowWindow" or "Progman" or "WorkerW";
    }

    private static void SendCtrlVPaste()
    {
        var inputs = new[]
        {
            KeyboardInput(VkControl, 0),
            KeyboardInput(VkV, 0),
            KeyboardInput(VkV, KeyEventKeyUp),
            KeyboardInput(VkControl, KeyEventKeyUp)
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input KeyboardInput(ushort virtualKey, uint flags)
    {
        return new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInputData
                {
                    VirtualKey = virtualKey,
                    Flags = flags
                }
            }
        };
    }

    private const int SwRestore = 9;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);
}
