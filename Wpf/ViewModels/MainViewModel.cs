using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using RaycastPM.Models;
using RaycastPM.Services;
using WpfClipboard = System.Windows.Clipboard;

namespace RaycastPM.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ExchangeRateRefreshInterval = TimeSpan.FromMinutes(10);

    private readonly StateStore _stateStore = new();
    private readonly LocalFileSearchService _fileSearch;
    private readonly ClipboardMonitor _clipboardMonitor = new();
    private readonly DispatcherTimer _ratesRefreshTimer = new();
    private readonly DispatcherTimer _indexProgressTimer = new();
    private readonly DispatcherTimer _launcherSearchTimer = new();
    private readonly AppState _state;
    private AppSection _selectedSection = AppSection.Settings;
    private SettingsPage _selectedSettingsPage = SettingsPage.General;
    private string _launcherQuery = string.Empty;
    private string _launcherStatusText = "输入关键词搜索本机文件";
    private string _clipboardQuery = string.Empty;
    private string _clipboardTypeFilter = "All";
    private string _noteQuery = string.Empty;
    private string _newNoteTitle = string.Empty;
    private string _noteFontSizeText = "18";
    private LauncherSearchResult? _selectedApp;
    private ClipboardEntry? _selectedClipboardEntry;
    private NoteItem? _selectedNote;
    private bool _isPopupOpen = true;
    private bool _isSearchingLauncher;
    private bool _isIndexingFiles;
    private double _indexProgressValue;
    private string _indexProgressText = "正在扫描磁盘...";
    private double? _calculatorResult;
    private CurrencyResult? _currencyResult;
    private bool _isRefreshingRates;
    private int _launcherSearchVersion;
    private CancellationTokenSource? _launcherSearchCancellation;

    public MainViewModel()
    {
        _state = _stateStore.Load();
        _fileSearch = new LocalFileSearchService(_stateStore.FolderPath);
        _noteFontSizeText = Settings.NoteFontSize.ToString("0", CultureInfo.InvariantCulture);
        ClipboardItems = new ObservableCollection<ClipboardEntry>(_state.ClipboardItems);
        Notes = new ObservableCollection<NoteItem>(_state.Notes);
        FilteredApps = new ObservableCollection<LauncherSearchResult>();
        UsageItems = new ObservableCollection<LauncherSearchResult>();
        FilteredClipboardItems = new ObservableCollection<ClipboardEntry>(ClipboardItems);
        FilteredNotes = new ObservableCollection<NoteItem>(Notes);

        ShowLauncherCommand = new RelayCommand(() => ShowSection(AppSection.Launcher));
        ShowClipboardCommand = new RelayCommand(() => ShowSection(AppSection.Clipboard));
        ShowNotesCommand = new RelayCommand(() => ShowSection(AppSection.Notes));
        ShowSettingsCommand = new RelayCommand(() => ShowSection(AppSection.Settings));
        ShowSettingsGeneralCommand = new RelayCommand(() => SelectedSettingsPage = SettingsPage.General);
        ShowUsageStatsCommand = new RelayCommand(() => SelectedSettingsPage = SettingsPage.Usage);
        ClearUsageStatsCommand = new RelayCommand(ClearUsageStats);
        RebuildFileIndexCommand = new RelayCommand(RebuildFileIndex);
        OpenSelectedAppCommand = new RelayCommand(OpenSelectedApp, () => SelectedApp is not null);
        CopySelectedClipboardCommand = new RelayCommand(CopySelectedClipboard, () => SelectedClipboardEntry is not null);
        DeleteSelectedClipboardCommand = new RelayCommand(DeleteSelectedClipboard, () => SelectedClipboardEntry is not null);
        AddNoteCommand = new RelayCommand(AddNote);
        DeleteSelectedNoteCommand = new RelayCommand(DeleteSelectedNote, () => SelectedNote is not null);
        RefreshRatesCommand = new RelayCommand(async () => await RefreshRatesAsync());
        CopyCalculatorResultCommand = new RelayCommand(CopyCalculatorResult, () => CalculatorResult is not null || CurrencyResult is not null);
        SaveCommand = new RelayCommand(Save);

        ClipboardItems.CollectionChanged += (_, _) => Save();
        Notes.CollectionChanged += (_, _) => Save();
        _clipboardMonitor.ClipboardChanged += (_, entry) => UpsertClipboardEntry(entry);
        _clipboardMonitor.Start();
        _launcherSearchTimer.Interval = TimeSpan.FromMilliseconds(180);
        _launcherSearchTimer.Tick += OnLauncherSearchTimerTick;
        _indexProgressTimer.Interval = TimeSpan.FromMilliseconds(400);
        _indexProgressTimer.Tick += OnIndexProgressTimerTick;
        InitializeFileIndex();
        _ratesRefreshTimer.Interval = ExchangeRateRefreshInterval;
        _ratesRefreshTimer.Tick += OnRatesRefreshTimerTick;
        _ratesRefreshTimer.Start();
        _ = RefreshRatesIfStaleAsync();
        RefreshUsageItems();
        SelectedNote = FilteredNotes.FirstOrDefault();
    }

    public AppSettings Settings => _state.Settings;
    public ObservableCollection<LauncherSearchResult> FilteredApps { get; }
    public ObservableCollection<LauncherSearchResult> UsageItems { get; }
    public ObservableCollection<ClipboardEntry> ClipboardItems { get; }
    public ObservableCollection<ClipboardEntry> FilteredClipboardItems { get; }
    public ObservableCollection<NoteItem> Notes { get; }
    public ObservableCollection<NoteItem> FilteredNotes { get; }

    public RelayCommand ShowLauncherCommand { get; }
    public RelayCommand ShowClipboardCommand { get; }
    public RelayCommand ShowNotesCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand ShowSettingsGeneralCommand { get; }
    public RelayCommand ShowUsageStatsCommand { get; }
    public RelayCommand ClearUsageStatsCommand { get; }
    public RelayCommand RebuildFileIndexCommand { get; }
    public RelayCommand OpenSelectedAppCommand { get; }
    public RelayCommand CopySelectedClipboardCommand { get; }
    public RelayCommand DeleteSelectedClipboardCommand { get; }
    public RelayCommand AddNoteCommand { get; }
    public RelayCommand DeleteSelectedNoteCommand { get; }
    public RelayCommand RefreshRatesCommand { get; }
    public RelayCommand CopyCalculatorResultCommand { get; }
    public RelayCommand SaveCommand { get; }

    public AppSection SelectedSection
    {
        get => _selectedSection;
        set => SetProperty(ref _selectedSection, value);
    }

    public SettingsPage SelectedSettingsPage
    {
        get => _selectedSettingsPage;
        set => SetProperty(ref _selectedSettingsPage, value);
    }

    public bool IsPopupOpen
    {
        get => _isPopupOpen;
        set => SetProperty(ref _isPopupOpen, value);
    }

    public bool IsSearchingLauncher
    {
        get => _isSearchingLauncher;
        private set => SetProperty(ref _isSearchingLauncher, value);
    }

    public string LauncherStatusText
    {
        get => _launcherStatusText;
        private set => SetProperty(ref _launcherStatusText, value);
    }

    public bool IsIndexingFiles
    {
        get => _isIndexingFiles;
        private set => SetProperty(ref _isIndexingFiles, value);
    }

    public double IndexProgressValue
    {
        get => _indexProgressValue;
        private set => SetProperty(ref _indexProgressValue, value);
    }

    public string IndexProgressText
    {
        get => _indexProgressText;
        private set => SetProperty(ref _indexProgressText, value);
    }

    public string LauncherQuery
    {
        get => _launcherQuery;
        set
        {
            if (SetProperty(ref _launcherQuery, value))
            {
                QueueLauncherSearch();
                RefreshToolResults();
            }
        }
    }

    public string ClipboardQuery
    {
        get => _clipboardQuery;
        set
        {
            if (SetProperty(ref _clipboardQuery, value))
            {
                RefreshClipboardResults();
            }
        }
    }

    public string ClipboardTypeFilter
    {
        get => _clipboardTypeFilter;
        set
        {
            if (SetProperty(ref _clipboardTypeFilter, value))
            {
                RefreshClipboardResults();
            }
        }
    }

    public string NoteQuery
    {
        get => _noteQuery;
        set
        {
            if (SetProperty(ref _noteQuery, value))
            {
                RefreshNoteResults();
            }
        }
    }

    public string NewNoteTitle
    {
        get => _newNoteTitle;
        set => SetProperty(ref _newNoteTitle, value);
    }

    public double NoteFontSize
    {
        get => Settings.NoteFontSize;
        set => ApplyNoteFontSize(value, true);
    }

    public string NoteFontSizeText
    {
        get => _noteFontSizeText;
        set
        {
            if (!SetProperty(ref _noteFontSizeText, value))
            {
                return;
            }

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed)
                || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                ApplyNoteFontSize(parsed, false);
            }
        }
    }

    private void ApplyNoteFontSize(double value, bool updateText)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return;
        }

        var normalized = Math.Clamp(value, 12, 36);
        if (updateText)
        {
            NoteFontSizeText = normalized.ToString("0", CultureInfo.InvariantCulture);
        }

        if (Math.Abs(Settings.NoteFontSize - normalized) < 0.1)
        {
            return;
        }

        Settings.NoteFontSize = normalized;
        OnPropertyChanged(nameof(NoteFontSize));
        Save();
    }

    public LauncherSearchResult? SelectedApp
    {
        get => _selectedApp;
        set
        {
            if (SetProperty(ref _selectedApp, value))
            {
                OpenSelectedAppCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ClipboardEntry? SelectedClipboardEntry
    {
        get => _selectedClipboardEntry;
        set
        {
            if (SetProperty(ref _selectedClipboardEntry, value))
            {
                CopySelectedClipboardCommand.RaiseCanExecuteChanged();
                DeleteSelectedClipboardCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public NoteItem? SelectedNote
    {
        get => _selectedNote;
        set
        {
            if (_selectedNote is not null)
            {
                _selectedNote.PropertyChanged -= OnSelectedNoteChanged;
            }

            if (SetProperty(ref _selectedNote, value))
            {
                DeleteSelectedNoteCommand.RaiseCanExecuteChanged();
            }

            if (_selectedNote is not null)
            {
                _selectedNote.PropertyChanged += OnSelectedNoteChanged;
            }
        }
    }

    public double? CalculatorResult
    {
        get => _calculatorResult;
        private set
        {
            if (SetProperty(ref _calculatorResult, value))
            {
                CopyCalculatorResultCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public CurrencyResult? CurrencyResult
    {
        get => _currencyResult;
        private set
        {
            if (SetProperty(ref _currencyResult, value))
            {
                CopyCalculatorResultCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? CalculatorResultText => CalculatorResult is null
        ? null
        : $"{LauncherQuery.Trim()} = {FormatCalculationNumber(CalculatorResult.Value)}";

    public string? CurrencyResultText => CurrencyResult is null
        ? null
        : $"{FormatCurrencyNumber(CurrencyResult.Amount)} {CurrencyResult.SourceLabel} = {FormatCurrencyNumber(CurrencyResult.Cny)} 人民币";

    public bool HasToolResult => CalculatorResult is not null || CurrencyResult is not null;

    public string DataFolder => _stateStore.FolderPath;

    public void ShowSection(AppSection section)
    {
        SelectedSection = section;
        IsPopupOpen = true;
    }

    public void RegisterHotKeys(GlobalHotKeyService hotKeys, Action<AppSection>? onSectionOpened = null)
    {
        void Register(HotKeyGesture gesture, AppSection section)
        {
            hotKeys.Register(gesture, () =>
            {
                ShowSection(section);
                onSectionOpened?.Invoke(section);
            });
        }

        hotKeys.Clear();
        Register(Settings.LauncherHotKey, AppSection.Launcher);
        Register(Settings.ClipboardHotKey, AppSection.Clipboard);
        Register(Settings.NotesHotKey, AppSection.Notes);
        Register(Settings.SettingsHotKey, AppSection.Settings);
    }

    public void Save()
    {
        _state.ClipboardItems = ClipboardItems.Take(120).ToList();
        _state.Notes = Notes.ToList();
        _stateStore.Save(_state);
    }

    public void Dispose()
    {
        _launcherSearchTimer.Stop();
        _launcherSearchTimer.Tick -= OnLauncherSearchTimerTick;
        _indexProgressTimer.Stop();
        _indexProgressTimer.Tick -= OnIndexProgressTimerTick;
        _ratesRefreshTimer.Stop();
        _ratesRefreshTimer.Tick -= OnRatesRefreshTimerTick;
        _launcherSearchCancellation?.Cancel();
        _launcherSearchCancellation?.Dispose();
    }

    private void QueueLauncherSearch()
    {
        _launcherSearchTimer.Stop();
        _launcherSearchCancellation?.Cancel();
        _launcherSearchTimer.Start();
    }

    private void OnLauncherSearchTimerTick(object? sender, EventArgs e)
    {
        _launcherSearchTimer.Stop();
        _ = RefreshLauncherResultsAsync();
    }

    private void InitializeFileIndex()
    {
        var loadedCache = _fileSearch.LoadCache();
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (Settings.LastFileIndexScanDate == today && loadedCache)
        {
            IndexProgressValue = 100;
            IndexProgressText = $"已加载本地索引缓存，收录 {_fileSearch.IndexedCount:N0} 项";
            IsIndexingFiles = false;
            LauncherStatusText = "输入关键词搜索本机文件";
            return;
        }

        _fileSearch.StartIndexing();
        Settings.LastFileIndexScanDate = today;
        Save();
        _indexProgressTimer.Start();
        RefreshIndexProgress();
    }

    private void OnIndexProgressTimerTick(object? sender, EventArgs e)
    {
        RefreshIndexProgress();
    }

    private void RefreshIndexProgress()
    {
        var progress = _fileSearch.GetProgress();
        IsIndexingFiles = progress.IsIndexing;
        IndexProgressValue = progress.CompletionRatio * 100;

        if (progress.IsIndexing)
        {
            var rootText = string.IsNullOrWhiteSpace(progress.CurrentRoot) ? "磁盘" : progress.CurrentRoot;
            var rootProgress = progress.RootsTotal > 0
                ? $"{progress.RootsCompleted}/{progress.RootsTotal} 个磁盘"
                : "准备扫描磁盘";
            IndexProgressText = $"正在扫描 {rootText}，{rootProgress}，已收录 {progress.IndexedCount:N0} 项，已扫描 {progress.DirectoriesScanned:N0} 个文件夹";
            if (string.IsNullOrWhiteSpace(LauncherQuery) && FilteredApps.Count == 0)
            {
                LauncherStatusText = IndexProgressText;
            }
            return;
        }

        IndexProgressValue = 100;
        IndexProgressText = $"索引完成，已收录 {progress.IndexedCount:N0} 项";
        if (string.IsNullOrWhiteSpace(LauncherQuery) && FilteredApps.Count == 0)
        {
            LauncherStatusText = "输入关键词搜索本机文件";
        }

        _indexProgressTimer.Stop();
    }

    private void RebuildFileIndex()
    {
        _launcherSearchCancellation?.Cancel();
        FilteredApps.Clear();
        SelectedApp = null;
        LauncherStatusText = "正在重新扫描硬盘数据...";
        _fileSearch.RebuildIndex();
        Settings.LastFileIndexScanDate = DateOnly.FromDateTime(DateTime.Now);
        Save();
        _indexProgressTimer.Start();
        RefreshIndexProgress();
        QueueLauncherSearch();
    }

    private async Task RefreshLauncherResultsAsync()
    {
        var query = LauncherQuery.Trim();
        var version = Interlocked.Increment(ref _launcherSearchVersion);

            _launcherSearchCancellation?.Cancel();
            _launcherSearchCancellation?.Dispose();
            _launcherSearchCancellation = null;

        if (string.IsNullOrWhiteSpace(query))
        {
            var frequentItems = UsageItems
                .OrderByDescending(item => item.LaunchCount)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(80)
                .Select(CloneLauncherResult)
                .ToArray();
            Replace(FilteredApps, frequentItems);
            SelectedApp = FilteredApps.FirstOrDefault();
            IsSearchingLauncher = false;
            LauncherStatusText = FilteredApps.Count > 0
                ? "按使用次数排序的常用项目"
                : IsIndexingFiles
                ? IndexProgressText
                : "输入关键词搜索本机文件";
            return;
        }

        var oldCancellation = _launcherSearchCancellation;
        oldCancellation?.Cancel();
        oldCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _launcherSearchCancellation = cancellation;
        IsSearchingLauncher = true;
        LauncherStatusText = "正在搜索本机文件...";

        try
        {
            var response = await _fileSearch.SearchAsync(query, 400, cancellation.Token);
            if (version != _launcherSearchVersion || cancellation.IsCancellationRequested)
            {
                return;
            }

            var rankedResults = response.Results
                .Select(ApplyUsageCount)
                .OrderByDescending(item => item.SearchScore)
                .ThenBy(item => KindRank(item))
                .ThenBy(item => item.SearchSortName, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(item => item.LaunchCount)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Take(80);
            Replace(FilteredApps, rankedResults);
            SelectedApp = FilteredApps.FirstOrDefault();
            RefreshIndexProgress();
            var indexText = response.IsIndexing ? $"，索引中 {response.IndexedCount:N0} 项" : string.Empty;
            LauncherStatusText = FilteredApps.Count == 0
                ? $"没有找到本机文件结果{indexText}"
                : $"{FilteredApps.Count} 个本机文件结果{indexText}";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (version == _launcherSearchVersion)
            {
                IsSearchingLauncher = false;
                _launcherSearchCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void RefreshToolResults()
    {
        CurrencyResult = CurrencyConverter.Convert(LauncherQuery, Settings.ExchangeRatesToCny);
        CalculatorResult = CurrencyResult is null && ExpressionEvaluator.LooksLikeCalculation(LauncherQuery)
            ? ExpressionEvaluator.Evaluate(LauncherQuery)
            : null;
        OnPropertyChanged(nameof(CalculatorResultText));
        OnPropertyChanged(nameof(CurrencyResultText));
        OnPropertyChanged(nameof(HasToolResult));
    }

    private LauncherSearchResult ApplyUsageCount(LauncherSearchResult item)
    {
        return new LauncherSearchResult
        {
            Name = item.Name,
            Path = item.Path,
            Kind = item.Kind,
            LaunchCount = GetLaunchCount(item.Path),
            SearchScore = item.SearchScore,
            SearchSortName = item.SearchSortName
        };
    }

    private static LauncherSearchResult CloneLauncherResult(LauncherSearchResult item)
    {
        return new LauncherSearchResult
        {
            Name = item.Name,
            Path = item.Path,
            Kind = item.Kind,
            LaunchCount = item.LaunchCount,
            SearchScore = item.SearchScore,
            SearchSortName = item.SearchSortName
        };
    }

    private int GetLaunchCount(string path)
    {
        return _state.AppLaunchCounts.TryGetValue(path, out var count) ? count : 0;
    }

    private void IncrementLaunchCount(LauncherSearchResult item)
    {
        var nextCount = GetLaunchCount(item.Path) + 1;
        _state.AppLaunchCounts[item.Path] = nextCount;
        item.LaunchCount = nextCount;
        RefreshUsageItems();
        Save();
    }

    private void RefreshUsageItems()
    {
        var items = _state.AppLaunchCounts
            .Where(pair => pair.Value > 0)
            .Select(pair => CreateUsageItem(pair.Key, pair.Value))
            .OrderByDescending(item => item.LaunchCount)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase);

        Replace(UsageItems, items);
    }

    private void ClearUsageStats()
    {
        _state.AppLaunchCounts.Clear();
        foreach (var item in FilteredApps)
        {
            item.LaunchCount = 0;
        }

        RefreshUsageItems();
        _ = RefreshLauncherResultsAsync();
        Save();
    }

    private static LauncherSearchResult CreateUsageItem(string path, int count)
    {
        var isDirectory = Directory.Exists(path);
        var kind = LauncherItemKind(path, isDirectory);
        return new LauncherSearchResult
        {
            Name = DisplayName(path),
            Path = path,
            Kind = kind,
            LaunchCount = count,
            SearchSortName = DisplayName(path).ToLowerInvariant()
        };
    }

    private static int KindRank(LauncherSearchResult item)
    {
        return item.Kind switch
        {
            "应用" => 0,
            "文件" => 1,
            "文件夹" => 2,
            _ => 3
        };
    }

    private static string LauncherItemKind(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return "文件夹";
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase)
            ? "应用"
            : "文件";
    }

    private static string DisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private void OpenSelectedApp()
    {
        if (SelectedApp is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(SelectedApp.Path) { UseShellExecute = true });
            IncrementLaunchCount(SelectedApp);
            IsPopupOpen = false;
        }
        catch (Exception ex)
        {
            LauncherStatusText = $"打开失败：{ex.Message}";
        }
    }

    private void UpsertClipboardEntry(ClipboardEntry entry)
    {
        var existing = ClipboardItems.FirstOrDefault(item =>
            item.Kind == entry.Kind &&
            item.Content == entry.Content &&
            SequenceEqual(item.ImageBytes, entry.ImageBytes));

        if (existing is not null)
        {
            existing.UpdatedAt = DateTimeOffset.Now;
            ClipboardItems.Remove(existing);
            ClipboardItems.Insert(0, existing);
        }
        else
        {
            ClipboardItems.Insert(0, entry);
        }

        while (ClipboardItems.Count > 120)
        {
            ClipboardItems.RemoveAt(ClipboardItems.Count - 1);
        }

        RefreshClipboardResults();
        Save();
    }

    private void RefreshClipboardResults()
    {
        var selectedId = SelectedClipboardEntry?.Id;
        var query = ClipboardQuery.Trim();
        IEnumerable<ClipboardEntry> items = ClipboardItems;

        items = ClipboardTypeFilter switch
        {
            "Text" => items.Where(item => item.Kind == ClipboardItemKind.Text),
            "Image" => items.Where(item => item.Kind == ClipboardItemKind.Image),
            _ => items
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            items = items.Where(item => item.DisplayText.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        Replace(FilteredClipboardItems, items);
        SelectedClipboardEntry = selectedId is null
            ? FilteredClipboardItems.FirstOrDefault()
            : FilteredClipboardItems.FirstOrDefault(item => item.Id == selectedId)
                ?? FilteredClipboardItems.FirstOrDefault();
    }

    private void CopySelectedClipboard()
    {
        if (SelectedClipboardEntry is null)
        {
            return;
        }

        _clipboardMonitor.IgnoreNextChange = true;
        try
        {
            if (SelectedClipboardEntry.Kind == ClipboardItemKind.Text)
            {
                WpfClipboard.SetText(SelectedClipboardEntry.Content);
                _clipboardMonitor.RememberText(SelectedClipboardEntry.Content);
            }
            else if (SelectedClipboardEntry.ImageBytes is not null)
            {
                using var stream = new MemoryStream(SelectedClipboardEntry.ImageBytes);
                var image = System.Windows.Media.Imaging.BitmapFrame.Create(
                    stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                image.Freeze();
                WpfClipboard.SetImage(image);
                _clipboardMonitor.RememberImage(image);
            }
        }
        catch
        {
            return;
        }

        IsPopupOpen = false;
    }

    private void DeleteSelectedClipboard()
    {
        if (SelectedClipboardEntry is null)
        {
            return;
        }

        ClipboardItems.Remove(SelectedClipboardEntry);
        RefreshClipboardResults();
        Save();
    }

    private void RefreshNoteResults()
    {
        var selectedId = SelectedNote?.Id;
        var query = NoteQuery.Trim();
        var notes = string.IsNullOrWhiteSpace(query)
            ? Notes
            : Notes.Where(note => note.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || note.Body.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        Replace(FilteredNotes, notes);
        SelectedNote = selectedId is null
            ? FilteredNotes.FirstOrDefault()
            : FilteredNotes.FirstOrDefault(note => note.Id == selectedId)
                ?? FilteredNotes.FirstOrDefault();
    }

    private void AddNote()
    {
        var note = new NoteItem
        {
            Title = string.IsNullOrWhiteSpace(NewNoteTitle) ? "新记事" : NewNoteTitle.Trim()
        };

        Notes.Insert(0, note);
        NewNoteTitle = string.Empty;
        RefreshNoteResults();
        SelectedNote = note;
        Save();
    }

    private void DeleteSelectedNote()
    {
        if (SelectedNote is null)
        {
            return;
        }

        Notes.Remove(SelectedNote);
        RefreshNoteResults();
        Save();
    }

    private async Task RefreshRatesAsync()
    {
        if (_isRefreshingRates)
        {
            return;
        }

        _isRefreshingRates = true;
        try
        {
            var rates = await CurrencyConverter.FetchRatesToCnyAsync();
            if (rates is null)
            {
                return;
            }

            Settings.ExchangeRatesToCny = rates;
            Settings.ExchangeRatesUpdatedAt = DateTimeOffset.Now;
            RefreshToolResults();
            Save();
        }
        catch
        {
        }
        finally
        {
            _isRefreshingRates = false;
        }
    }

    private Task RefreshRatesIfStaleAsync()
    {
        if (Settings.ExchangeRatesUpdatedAt is { } updatedAt
            && DateTimeOffset.Now - updatedAt < ExchangeRateRefreshInterval)
        {
            return Task.CompletedTask;
        }

        return RefreshRatesAsync();
    }

    private void OnRatesRefreshTimerTick(object? sender, EventArgs e)
    {
        _ = RefreshRatesAsync();
    }

    private void CopyCalculatorResult()
    {
        var text = CurrencyResult is not null
            ? $"{FormatCurrencyNumber(CurrencyResult.Cny)} 人民币"
            : CalculatorResult is null
                ? null
                : FormatCalculationNumber(CalculatorResult.Value);

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _clipboardMonitor.IgnoreNextChange = true;
        WpfClipboard.SetText(text);
        UpsertClipboardEntry(new ClipboardEntry
        {
            Kind = ClipboardItemKind.Text,
            Content = text,
            Source = "RaycastPM"
        });
    }

    private static string FormatCalculationNumber(double value)
    {
        return value.ToString("#,0.########", CultureInfo.InvariantCulture);
    }

    private static string FormatCurrencyNumber(double value)
    {
        return value.ToString("#,0.##", CultureInfo.InvariantCulture);
    }

    private void OnSelectedNoteChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NoteItem note || e.PropertyName == nameof(NoteItem.UpdatedAt))
        {
            return;
        }

        note.UpdatedAt = DateTimeOffset.Now;
        Save();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static bool SequenceEqual(byte[]? left, byte[]? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.AsSpan().SequenceEqual(right);
    }
}
