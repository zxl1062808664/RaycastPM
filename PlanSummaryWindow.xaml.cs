using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace RaycastPM;

public partial class PlanSummaryWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private bool _isClickThrough;
    private bool _isTopmost;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    public PlanSummaryWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public void ApplyWindowOptions(bool topmost, bool clickThrough, double opacity)
    {
        _isClickThrough = clickThrough;
        _isTopmost = topmost;
        Opacity = Math.Clamp(opacity, 0.2, 1.0);
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            ApplyExtendedWindowStyles(_isClickThrough);
            ApplyTopmostState(bringToFront: IsVisible);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 18;
        Top = workArea.Top + 74;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyExtendedWindowStyles(_isClickThrough);
        ApplyTopmostState(bringToFront: IsVisible);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ApplyTopmostState(bringToFront: true);
        }
    }

    private void SummaryRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isClickThrough || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
            ApplyTopmostState(bringToFront: true);
        }
        catch
        {
        }
    }

    private void ApplyExtendedWindowStyles(bool clickThrough)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var currentStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        var newStyle = currentStyle | WsExToolWindow;
        if (clickThrough)
        {
            newStyle |= WsExTransparent;
        }
        else
        {
            newStyle &= ~WsExTransparent;
        }

        if (newStyle != currentStyle)
        {
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(newStyle));
        }
    }

    private void ApplyTopmostState(bool bringToFront)
    {
        Topmost = _isTopmost;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var flags = SwpNoMove | SwpNoSize | SwpNoActivate;
        if (bringToFront)
        {
            flags |= SwpShowWindow;
        }

        SetWindowPos(
            handle,
            _isTopmost ? HwndTopmost : HwndNotTopmost,
            0,
            0,
            0,
            0,
            flags);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    private static void SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hWnd, nIndex, value);
            return;
        }

        SetWindowLong32(hWnd, nIndex, value.ToInt32());
    }
}
