using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RaycastPM.Models;

public enum AppSection
{
    Launcher,
    Clipboard,
    Notes,
    Settings
}

public enum SettingsPage
{
    General,
    Usage
}

public enum ClipboardItemKind
{
    Text,
    Image,
    File,
    Link,
    Color
}

public sealed class AppSettings
{
    public HotKeyGesture LauncherHotKey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Alt, "Space");
    public HotKeyGesture ClipboardHotKey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Alt, "V");
    public HotKeyGesture NotesHotKey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Alt, "N");
    public HotKeyGesture SettingsHotKey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Alt, "S");
    public bool StartWithWindows { get; set; }
    public bool AutoScanOnDailyFirstLaunch { get; set; } = true;
    public bool LoggingEnabled { get; set; } = true;
    public string LogDirectory { get; set; } = string.Empty;
    public bool SystemMonitorEnabled { get; set; }
    public double NoteFontSize { get; set; } = 18;
    public List<string> CustomAppDirectories { get; set; } = [];
    public Dictionary<string, double> ExchangeRatesToCny { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CNY"] = 1,
        ["USD"] = 6.8
    };
    public DateTimeOffset? ExchangeRatesUpdatedAt { get; set; }
    public DateOnly? LastFileIndexScanDate { get; set; }
}

[Flags]
public enum ModifierKeys
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8
}

public sealed record HotKeyGesture(ModifierKeys Modifiers, string Key)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Win)) parts.Add("Win");
        parts.Add(DisplayKey(Key));
        return string.Join("+", parts);
    }

    private static string DisplayKey(string key)
    {
        return key.Length == 2 && key[0] is 'D' or 'd' && char.IsDigit(key[1])
            ? key[1].ToString()
            : key;
    }
}

public sealed class InstalledApp
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public int LaunchCount { get; set; }
}

public enum LauncherActionKind
{
    OpenPath,
    OpenUrl,
    WebSearch
}

public sealed class LauncherSearchResult
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public LauncherActionKind ActionKind { get; init; } = LauncherActionKind.OpenPath;
    public int LaunchCount { get; set; }
    public int SearchScore { get; set; }
    public string SearchSortName { get; set; } = string.Empty;
}

public sealed class ClipboardEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ClipboardItemKind Kind { get; set; }
    public string Content { get; set; } = string.Empty;
    public byte[]? ImageBytes { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? Source { get; set; }

    public string DisplayText => string.IsNullOrWhiteSpace(Content) ? KindTitle : Content.Trim();
    public string KindTitle => Kind switch
    {
        ClipboardItemKind.Image => "图片",
        ClipboardItemKind.File => "文件",
        ClipboardItemKind.Link => "链接",
        ClipboardItemKind.Color => "颜色",
        _ => "文本"
    };
}

public sealed class NoteItem : INotifyPropertyChanged
{
    private string _title = "新记事";
    private string _body = string.Empty;
    private DateTimeOffset _updatedAt = DateTimeOffset.Now;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Body
    {
        get => _body;
        set => SetProperty(ref _body, value);
    }

    public ObservableCollection<NoteImageItem> Images { get; set; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        set => SetProperty(ref _updatedAt, value);
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class NoteImageItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public byte[] ImageBytes { get; set; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed class AppState
{
    public AppSettings Settings { get; set; } = new();
    public Dictionary<string, int> AppLaunchCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ClipboardEntry> ClipboardItems { get; set; } = [];
    public List<NoteItem> Notes { get; set; } = [];
}

public sealed record CurrencyResult(double Amount, double Cny, string SourceLabel, string CurrencyCode);
