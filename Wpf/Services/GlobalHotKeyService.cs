using System.Runtime.InteropServices;
using System.Windows.Interop;
using RaycastPM.Models;
using WpfKey = System.Windows.Input.Key;
using WpfKeyInterop = System.Windows.Input.KeyInterop;

namespace RaycastPM.Services;

public sealed class GlobalHotKeyService : IDisposable
{
    private const int WmHotKey = 0x0312;
    private HwndSource? _source;
    private int _nextId = 100;
    private readonly Dictionary<int, Action> _actions = [];

    public void Attach(IntPtr hwnd)
    {
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    public void Register(HotKeyGesture gesture, Action action)
    {
        if (_source is null)
        {
            return;
        }

        var key = ParseKey(gesture.Key);
        if (key is WpfKey.None)
        {
            return;
        }

        var id = _nextId++;
        var modifiers = ToWin32Modifiers(gesture.Modifiers);
        var virtualKey = WpfKeyInterop.VirtualKeyFromKey(key);

        if (RegisterHotKey(_source.Handle, id, modifiers, (uint)virtualKey))
        {
            _actions[id] = action;
        }
    }

    public void Clear()
    {
        if (_source is null)
        {
            _actions.Clear();
            return;
        }

        foreach (var id in _actions.Keys.ToArray())
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _actions.Clear();
    }

    public void Dispose()
    {
        Clear();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static uint ToWin32Modifiers(ModifierKeys modifiers)
    {
        uint value = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) value |= 0x0001;
        if (modifiers.HasFlag(ModifierKeys.Control)) value |= 0x0002;
        if (modifiers.HasFlag(ModifierKeys.Shift)) value |= 0x0004;
        if (modifiers.HasFlag(ModifierKeys.Win)) value |= 0x0008;
        return value;
    }

    private static WpfKey ParseKey(string key)
    {
        if (key.Length == 1 && char.IsDigit(key[0]))
        {
            return Enum.TryParse<WpfKey>($"D{key}", true, out var digitKey)
                ? digitKey
                : WpfKey.None;
        }

        if (Enum.TryParse<WpfKey>(key, true, out var parsed))
        {
            return parsed;
        }

        return key.Length == 1 && char.IsLetterOrDigit(key[0])
            ? Enum.Parse<WpfKey>(key.ToUpperInvariant())
            : WpfKey.None;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
