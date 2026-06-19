using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RaycastPM.Models;

public enum AppSection
{
    Launcher,
    Clipboard,
    Notes,
    Plans,
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
    public HotKeyGesture PlansHotKey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Alt, "P");
    public HotKeyGesture SettingsHotKey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Alt, "S");
    public bool StartWithWindows { get; set; }
    public bool AutoScanOnDailyFirstLaunch { get; set; } = true;
    public bool LoggingEnabled { get; set; } = true;
    public string LogDirectory { get; set; } = string.Empty;
    public bool SystemMonitorEnabled { get; set; }
    public bool PlanSummaryWindowEnabled { get; set; }
    public bool PlanSummaryWindowTopmost { get; set; } = true;
    public bool PlanSummaryWindowClickThrough { get; set; }
    public double PlanSummaryWindowOpacity { get; set; } = 0.88;
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
    private string _title = "新笔记";
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

public enum PlanItemStatus
{
    NotStarted,
    InProgress,
    Completed,
    Blocked
}

public enum PlanPriority
{
    Low,
    Medium,
    High
}

public sealed class PlanItem : INotifyPropertyChanged
{
    private string _title = "新计划";
    private string _description = string.Empty;
    private PlanItemStatus _status;
    private PlanPriority _priority = PlanPriority.Medium;
    private DateTime _startAt = DateTime.Now;
    private DateTime? _targetCompletedAt;
    private DateTime? _actualCompletedAt;
    private DateTimeOffset _updatedAt = DateTimeOffset.Now;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Description
    {
        get => _description;
        set
        {
            if (SetProperty(ref _description, value))
            {
                OnPropertyChanged(nameof(SummaryText));
            }
        }
    }

    public ObservableCollection<NoteImageItem> Images { get; set; } = [];

    public PlanItemStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public PlanPriority Priority
    {
        get => _priority;
        set
        {
            if (SetProperty(ref _priority, value))
            {
                OnPropertyChanged(nameof(PriorityText));
            }
        }
    }

    public DateTime StartAt
    {
        get => _startAt;
        set
        {
            if (SetProperty(ref _startAt, value))
            {
                OnPropertyChanged(nameof(StartAtText));
            }
        }
    }

    public DateTime? TargetCompletedAt
    {
        get => _targetCompletedAt;
        set
        {
            if (SetProperty(ref _targetCompletedAt, value))
            {
                OnPropertyChanged(nameof(TargetCompletedAtText));
            }
        }
    }

    public DateTime? ActualCompletedAt
    {
        get => _actualCompletedAt;
        set
        {
            if (SetProperty(ref _actualCompletedAt, value))
            {
                OnPropertyChanged(nameof(ActualCompletedAtText));
            }
        }
    }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        set => SetProperty(ref _updatedAt, value);
    }

    public string SummaryText => string.IsNullOrWhiteSpace(Description) ? "未填写说明" : Description.Trim();

    public string StatusText => Status switch
    {
        PlanItemStatus.InProgress => "进行中",
        PlanItemStatus.Completed => "已完成",
        PlanItemStatus.Blocked => "已阻塞",
        _ => "未开始"
    };

    public string PriorityText => Priority switch
    {
        PlanPriority.High => "高优先级",
        PlanPriority.Low => "低优先级",
        _ => "中优先级"
    };

    public string StartAtText => StartAt.ToString("yyyy-MM-dd HH:mm");

    public string TargetCompletedAtText => TargetCompletedAt?.ToString("yyyy-MM-dd HH:mm") ?? "未设置";

    public string ActualCompletedAtText => ActualCompletedAt?.ToString("yyyy-MM-dd HH:mm") ?? "未完成";

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class AppState
{
    public AppSettings Settings { get; set; } = new();
    public Dictionary<string, int> AppLaunchCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ClipboardEntry> ClipboardItems { get; set; } = [];
    public List<NoteItem> Notes { get; set; } = [];
    public List<PlanItem> PlanItems { get; set; } = [];
}

public sealed record CurrencyResult(double Amount, double Cny, string SourceLabel, string CurrencyCode);
