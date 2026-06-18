using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RaycastPM.Models;
using RaycastPM.Services;
using WinFormsDialogResult = System.Windows.Forms.DialogResult;
using WinFormsFolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace RaycastPM.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ExchangeRateRefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SystemMonitorRefreshInterval = TimeSpan.FromSeconds(1);
    private const string WebSearchUrlTemplate = "https://www.bing.com/search?q={0}";
    private static readonly IReadOnlyList<string> HotKeyOptions =
    [
        "Space",
        "Tab",
        "Escape",
        "Back",
        "A",
        "B",
        "C",
        "D",
        "E",
        "F",
        "G",
        "H",
        "I",
        "J",
        "K",
        "L",
        "M",
        "N",
        "O",
        "P",
        "Q",
        "R",
        "S",
        "T",
        "U",
        "V",
        "W",
        "X",
        "Y",
        "Z",
        "0",
        "1",
        "2",
        "3",
        "4",
        "5",
        "6",
        "7",
        "8",
        "9",
        "F1",
        "F2",
        "F3",
        "F4",
        "F5",
        "F6",
        "F7",
        "F8",
        "F9",
        "F10",
        "F11",
        "F12"
    ];

    private static readonly HotKeyGesture DefaultLauncherHotKey = new(ModifierKeys.Control | ModifierKeys.Alt, "Space");
    private static readonly HotKeyGesture DefaultClipboardHotKey = new(ModifierKeys.Control | ModifierKeys.Alt, "V");
    private static readonly HotKeyGesture DefaultNotesHotKey = new(ModifierKeys.Control | ModifierKeys.Alt, "N");
    private static readonly HotKeyGesture DefaultPlansHotKey = new(ModifierKeys.Control | ModifierKeys.Alt, "P");
    private static readonly HotKeyGesture DefaultSettingsHotKey = new(ModifierKeys.Control | ModifierKeys.Alt, "S");
    private static readonly IReadOnlyList<string> PlanTimeOptionsSource = CreatePlanTimeOptions();

    private readonly StateStore _stateStore = new();
    private readonly LocalFileSearchService _fileSearch;
    private readonly SystemMonitorService _systemMonitor = new();
    private readonly ClipboardMonitor _clipboardMonitor = new();
    private readonly DispatcherTimer _ratesRefreshTimer = new();
    private readonly DispatcherTimer _indexProgressTimer = new();
    private readonly DispatcherTimer _launcherSearchDebounceTimer = new();
    private readonly DispatcherTimer _systemMonitorTimer = new();
    private readonly CancellationTokenSource _fileInitializationCancellation = new();
    private readonly AppState _state;
    private AppSection _selectedSection = AppSection.Settings;
    private SettingsPage _selectedSettingsPage = SettingsPage.General;
    private string _launcherQuery = string.Empty;
    private string _launcherStatusText = "输入关键词搜索本机文件";
    private string _clipboardQuery = string.Empty;
    private string _clipboardTypeFilter = "All";
    private string _noteQuery = string.Empty;
    private string _planQuery = string.Empty;
    private string _planStatusFilter = "All";
    private string _planPriorityFilter = "All";
    private string _newNoteTitle = string.Empty;
    private string _newPlanTitle = string.Empty;
    private string _noteFontSizeText = "18";
    private DateTime _planCalendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selectedPlanDate = DateTime.Today;
    private LauncherSearchResult? _selectedApp;
    private ClipboardEntry? _selectedClipboardEntry;
    private NoteItem? _selectedNote;
    private NoteImageItem? _selectedNoteImage;
    private PlanItem? _selectedPlan;
    private NoteImageItem? _selectedPlanImage;
    private PlanCalendarDetailItem? _selectedPlanDayDetail;
    private string _selectedPlanTargetCompletedTimeText = string.Empty;
    private string _selectedPlanActualCompletedTimeText = string.Empty;
    private bool _ignoreTransientPlanSelectionClearing;
    private bool _preservePlanEditorStateOnRefresh;
    private bool _isPlanEditorOpen;
    private bool _isPopupOpen = true;
    private bool _isClearingCacheData;
    private bool _isSearchingLauncher;
    private bool _isIndexingFiles;
    private bool _isRefreshingSystemMonitor;
    private bool _isMainWindowVisible = true;
    private bool _isSystemMonitorPanelVisible;
    private double _indexProgressValue;
    private string _indexProgressText = "正在扫描磁盘...";
    private string _systemMonitorNetworkText = "↓ 0 B/s  ↑ 0 B/s";
    private string _systemMonitorCpuText = "CPU 0%";
    private string _systemMonitorGpuText = "GPU --";
    private string _systemMonitorMemoryText = "内存 0%";
    private double? _calculatorResult;
    private CurrencyResult? _currencyResult;
    private bool _isRefreshingRates;
    private bool _startWithWindows;
    private GlobalHotKeyService? _registeredHotKeys;
    private Action<AppSection>? _onRegisteredHotKeyOpened;
    private int _launcherSearchVersion;
    private CancellationTokenSource? _launcherSearchCancellation;
    private bool _disposed;

    public MainViewModel()
    {
        _state = _stateStore.Load();
        AppDiagnostics.Configure(Settings.LoggingEnabled, Settings.LogDirectory);
        _fileSearch = new LocalFileSearchService(_stateStore.FolderPath);
        SyncStartWithWindowsSetting();
        _noteFontSizeText = Settings.NoteFontSize.ToString("0", CultureInfo.InvariantCulture);
        ClipboardItems = new ObservableCollection<ClipboardEntry>(_state.ClipboardItems);
        Notes = new ObservableCollection<NoteItem>(_state.Notes);
        Plans = new ObservableCollection<PlanItem>(_state.PlanItems);
        FilteredApps = new ObservableCollection<LauncherSearchResult>();
        UsageItems = new ObservableCollection<LauncherSearchResult>();
        FilteredClipboardItems = new ObservableCollection<ClipboardEntry>(ClipboardItems);
        FilteredNotes = new ObservableCollection<NoteItem>(Notes);
        FilteredPlans = new ObservableCollection<PlanItem>(Plans);
        PlanCalendarDays = new ObservableCollection<PlanCalendarDayItem>();
        SelectedPlanDayDetails = new ObservableCollection<PlanCalendarDetailItem>();
        HotKeyEditors = new ObservableCollection<HotKeyEditorItem>();

        ShowLauncherCommand = new RelayCommand(() => ShowSection(AppSection.Launcher));
        ShowClipboardCommand = new RelayCommand(() => ShowSection(AppSection.Clipboard));
        ShowNotesCommand = new RelayCommand(() => ShowSection(AppSection.Notes));
        ShowPlansCommand = new RelayCommand(() => ShowSection(AppSection.Plans));
        ShowSettingsCommand = new RelayCommand(() => ShowSection(AppSection.Settings));
        ShowSettingsGeneralCommand = new RelayCommand(() => SelectedSettingsPage = SettingsPage.General);
        ShowUsageStatsCommand = new RelayCommand(() => SelectedSettingsPage = SettingsPage.Usage);
        ClearUsageStatsCommand = new RelayCommand(ClearUsageStats);
        RebuildFileIndexCommand = new RelayCommand(RebuildFileIndex);
        ClearCacheDataCommand = new RelayCommand(() => Observe(ClearCacheDataAsync(), "clear cache data command"), () => !_isClearingCacheData);
        OpenSelectedAppCommand = new RelayCommand(OpenSelectedApp, () => SelectedApp is not null);
        CopySelectedClipboardCommand = new RelayCommand(CopySelectedClipboard, () => SelectedClipboardEntry is not null);
        DeleteSelectedClipboardCommand = new RelayCommand(DeleteSelectedClipboard, () => SelectedClipboardEntry is not null);
        AddNoteCommand = new RelayCommand(AddNote);
        DeleteSelectedNoteCommand = new RelayCommand(DeleteSelectedNote, () => SelectedNote is not null);
        AddNoteImageFromClipboardCommand = new RelayCommand(AddNoteImageFromClipboard, () => SelectedNote is not null);
        AddNoteImageFromFileCommand = new RelayCommand(AddNoteImageFromFile, () => SelectedNote is not null);
        DeleteSelectedNoteImageCommand = new RelayCommand(DeleteSelectedNoteImage, () => SelectedNoteImage is not null);
        AddPlanCommand = new RelayCommand(AddPlan);
        DeleteSelectedPlanCommand = new RelayCommand(DeleteSelectedPlan, () => SelectedPlan is not null);
        CompleteSelectedPlanCommand = new RelayCommand(MarkSelectedPlanCompleted, () => SelectedPlan is not null && (SelectedPlan.Status != PlanItemStatus.Completed || SelectedPlan.ActualCompletedAt is null));
        TogglePlanEditorCommand = new RelayCommand(TogglePlanEditor, () => SelectedPlan is not null);
        ClosePlanEditorCommand = new RelayCommand(ClosePlanEditor, () => IsPlanEditorOpen);
        SetSelectedPlanStatusCommand = new RelayCommand(SetSelectedPlanStatus, _ => SelectedPlan is not null);
        SetSelectedPlanPriorityCommand = new RelayCommand(SetSelectedPlanPriority, _ => SelectedPlan is not null);
        SetSelectedPlanTargetCompletedAtCommand = new RelayCommand(SetSelectedPlanTargetCompletedAt, _ => SelectedPlan is not null);
        ClearSelectedPlanTargetCompletedAtCommand = new RelayCommand(ClearSelectedPlanTargetCompletedAt, () => SelectedPlan?.TargetCompletedAt is not null);
        ClearSelectedPlanActualCompletedAtCommand = new RelayCommand(ClearSelectedPlanActualCompletedAt, () => SelectedPlan?.ActualCompletedAt is not null);
        AddPlanImageFromClipboardCommand = new RelayCommand(AddPlanImageFromClipboard, () => SelectedPlan is not null);
        AddPlanImageFromFileCommand = new RelayCommand(AddPlanImageFromFile, () => SelectedPlan is not null);
        DeleteSelectedPlanImageCommand = new RelayCommand(DeleteSelectedPlanImage, () => SelectedPlanImage is not null);
        ShowPreviousPlanMonthCommand = new RelayCommand(() => ChangePlanCalendarMonth(-1));
        ShowCurrentPlanMonthCommand = new RelayCommand(ResetPlanCalendarToToday);
        ShowNextPlanMonthCommand = new RelayCommand(() => ChangePlanCalendarMonth(1));
        SelectPlanCalendarDayCommand = new RelayCommand(SelectPlanCalendarDay);
        SelectPlanDayDetailCommand = new RelayCommand(SelectPlanDayDetail);
        RefreshRatesCommand = new RelayCommand(() => Observe(RefreshRatesAsync(), "refresh rates command"));
        CopyCalculatorResultCommand = new RelayCommand(CopyCalculatorResult, () => CalculatorResult is not null || CurrencyResult is not null);
        SaveCommand = new RelayCommand(Save);
        BrowseLogDirectoryCommand = new RelayCommand(BrowseLogDirectory);
        InitializeHotKeyEditors();

        ClipboardItems.CollectionChanged += (_, _) => Save();
        Notes.CollectionChanged += (_, _) => Save();
        Plans.CollectionChanged += (_, _) =>
        {
            NotifyPlanSummaryChanged();
            Save();
        };
        _clipboardMonitor.ClipboardChanged += (_, entry) => UpsertClipboardEntry(entry);
        _clipboardMonitor.Start();
        _indexProgressTimer.Interval = TimeSpan.FromMilliseconds(400);
        _indexProgressTimer.Tick += OnIndexProgressTimerTick;
        _launcherSearchDebounceTimer.Interval = TimeSpan.FromMilliseconds(120);
        _launcherSearchDebounceTimer.Tick += OnLauncherSearchDebounceTimerTick;
        _systemMonitorTimer.Interval = SystemMonitorRefreshInterval;
        _systemMonitorTimer.Tick += OnSystemMonitorTimerTick;
        if (Settings.SystemMonitorEnabled)
        {
            ApplySystemMonitorTimerPolicy();
        }

        Observe(InitializeFileIndexAsync(), "initialize file index");
        _ratesRefreshTimer.Interval = ExchangeRateRefreshInterval;
        _ratesRefreshTimer.Tick += OnRatesRefreshTimerTick;
        _ratesRefreshTimer.Start();
        Observe(RefreshRatesIfStaleAsync(), "refresh rates on startup");
        RefreshUsageItems();
        RefreshPlanResults();
        SelectedNote = FilteredNotes.FirstOrDefault();
        SelectedPlan = FilteredPlans.FirstOrDefault();
        RefreshPlanCalendar();
        NotifyPlanSummaryChanged();
    }

    public AppSettings Settings => _state.Settings;
    public ObservableCollection<LauncherSearchResult> FilteredApps { get; }
    public ObservableCollection<LauncherSearchResult> UsageItems { get; }
    public ObservableCollection<ClipboardEntry> ClipboardItems { get; }
    public ObservableCollection<ClipboardEntry> FilteredClipboardItems { get; }
    public ObservableCollection<NoteItem> Notes { get; }
    public ObservableCollection<NoteItem> FilteredNotes { get; }
    public ObservableCollection<PlanItem> Plans { get; }
    public ObservableCollection<PlanItem> FilteredPlans { get; }
    public ObservableCollection<PlanCalendarDayItem> PlanCalendarDays { get; }
    public ObservableCollection<PlanCalendarDetailItem> SelectedPlanDayDetails { get; }
    public ObservableCollection<HotKeyEditorItem> HotKeyEditors { get; }
    public IReadOnlyList<string> PlanTimeOptions => PlanTimeOptionsSource;

    public RelayCommand ShowLauncherCommand { get; }
    public RelayCommand ShowClipboardCommand { get; }
    public RelayCommand ShowNotesCommand { get; }
    public RelayCommand ShowPlansCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand ShowSettingsGeneralCommand { get; }
    public RelayCommand ShowUsageStatsCommand { get; }
    public RelayCommand ClearUsageStatsCommand { get; }
    public RelayCommand RebuildFileIndexCommand { get; }
    public RelayCommand ClearCacheDataCommand { get; }
    public RelayCommand OpenSelectedAppCommand { get; }
    public RelayCommand CopySelectedClipboardCommand { get; }
    public RelayCommand DeleteSelectedClipboardCommand { get; }
    public RelayCommand AddNoteCommand { get; }
    public RelayCommand DeleteSelectedNoteCommand { get; }
    public RelayCommand AddNoteImageFromClipboardCommand { get; }
    public RelayCommand AddNoteImageFromFileCommand { get; }
    public RelayCommand DeleteSelectedNoteImageCommand { get; }
    public RelayCommand AddPlanCommand { get; }
    public RelayCommand DeleteSelectedPlanCommand { get; }
    public RelayCommand CompleteSelectedPlanCommand { get; }
    public RelayCommand TogglePlanEditorCommand { get; }
    public RelayCommand ClosePlanEditorCommand { get; }
    public RelayCommand SetSelectedPlanStatusCommand { get; }
    public RelayCommand SetSelectedPlanPriorityCommand { get; }
    public RelayCommand SetSelectedPlanTargetCompletedAtCommand { get; }
    public RelayCommand ClearSelectedPlanTargetCompletedAtCommand { get; }
    public RelayCommand ClearSelectedPlanActualCompletedAtCommand { get; }
    public RelayCommand AddPlanImageFromClipboardCommand { get; }
    public RelayCommand AddPlanImageFromFileCommand { get; }
    public RelayCommand DeleteSelectedPlanImageCommand { get; }
    public RelayCommand ShowPreviousPlanMonthCommand { get; }
    public RelayCommand ShowCurrentPlanMonthCommand { get; }
    public RelayCommand ShowNextPlanMonthCommand { get; }
    public RelayCommand SelectPlanCalendarDayCommand { get; }
    public RelayCommand SelectPlanDayDetailCommand { get; }
    public RelayCommand RefreshRatesCommand { get; }
    public RelayCommand CopyCalculatorResultCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand BrowseLogDirectoryCommand { get; }

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

    public string SystemMonitorNetworkText
    {
        get => _systemMonitorNetworkText;
        private set => SetProperty(ref _systemMonitorNetworkText, value);
    }

    public string SystemMonitorCpuText
    {
        get => _systemMonitorCpuText;
        private set => SetProperty(ref _systemMonitorCpuText, value);
    }

    public string SystemMonitorGpuText
    {
        get => _systemMonitorGpuText;
        private set => SetProperty(ref _systemMonitorGpuText, value);
    }

    public string SystemMonitorMemoryText
    {
        get => _systemMonitorMemoryText;
        private set => SetProperty(ref _systemMonitorMemoryText, value);
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

    public string PlanQuery
    {
        get => _planQuery;
        set
        {
            if (SetProperty(ref _planQuery, value))
            {
                RefreshPlanResults();
            }
        }
    }

    public string PlanStatusFilter
    {
        get => _planStatusFilter;
        set
        {
            if (SetProperty(ref _planStatusFilter, value))
            {
                RefreshPlanResults();
            }
        }
    }

    public string PlanPriorityFilter
    {
        get => _planPriorityFilter;
        set
        {
            if (SetProperty(ref _planPriorityFilter, value))
            {
                RefreshPlanResults();
            }
        }
    }

    public string NewPlanTitle
    {
        get => _newPlanTitle;
        set => SetProperty(ref _newPlanTitle, value);
    }

    public DateTime PlanCalendarMonth
    {
        get => _planCalendarMonth;
        private set
        {
            var normalized = new DateTime(value.Year, value.Month, 1);
            if (!SetProperty(ref _planCalendarMonth, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(PlanCalendarMonthTitle));
            RefreshPlanCalendar();
        }
    }

    public string PlanCalendarMonthTitle => $"{PlanCalendarMonth:yyyy年M月}";

    public DateTime SelectedPlanDate
    {
        get => _selectedPlanDate;
        private set
        {
            var normalized = value.Date;
            if (!SetProperty(ref _selectedPlanDate, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedPlanDateTitle));
            RefreshPlanCalendarSelection();
            RefreshSelectedPlanDayDetails();
        }
    }

    public string SelectedPlanDateTitle => $"{SelectedPlanDate:yyyy/M/d dddd}";

    public bool IsPlanEditorOpen
    {
        get => _isPlanEditorOpen;
        set
        {
            if (SetProperty(ref _isPlanEditorOpen, value))
            {
                ClosePlanEditorCommand.RaiseCanExecuteChanged();
            }
        }
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
                AddNoteImageFromClipboardCommand.RaiseCanExecuteChanged();
                AddNoteImageFromFileCommand.RaiseCanExecuteChanged();
                SelectedNoteImage = _selectedNote?.Images.FirstOrDefault();
            }

            if (_selectedNote is not null)
            {
                _selectedNote.PropertyChanged += OnSelectedNoteChanged;
            }
        }
    }

    public NoteImageItem? SelectedNoteImage
    {
        get => _selectedNoteImage;
        set
        {
            if (SetProperty(ref _selectedNoteImage, value))
            {
                DeleteSelectedNoteImageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public PlanItem? SelectedPlan
    {
        get => _selectedPlan;
        set
        {
            var previousId = _selectedPlan?.Id;
            if (_selectedPlan is not null)
            {
                _selectedPlan.PropertyChanged -= OnSelectedPlanChanged;
            }

            if (SetProperty(ref _selectedPlan, value))
            {
                var selectionChanged = previousId != value?.Id;
                DeleteSelectedPlanCommand.RaiseCanExecuteChanged();
                CompleteSelectedPlanCommand.RaiseCanExecuteChanged();
                TogglePlanEditorCommand.RaiseCanExecuteChanged();
                ClosePlanEditorCommand.RaiseCanExecuteChanged();
                SetSelectedPlanStatusCommand.RaiseCanExecuteChanged();
                SetSelectedPlanPriorityCommand.RaiseCanExecuteChanged();
                SetSelectedPlanTargetCompletedAtCommand.RaiseCanExecuteChanged();
                ClearSelectedPlanTargetCompletedAtCommand.RaiseCanExecuteChanged();
                ClearSelectedPlanActualCompletedAtCommand.RaiseCanExecuteChanged();
                AddPlanImageFromClipboardCommand.RaiseCanExecuteChanged();
                AddPlanImageFromFileCommand.RaiseCanExecuteChanged();
                SelectedPlanImage = _selectedPlan?.Images.FirstOrDefault();
                SyncSelectedPlanTimeEditors();
                if (value is null)
                {
                    if (!_ignoreTransientPlanSelectionClearing)
                    {
                        IsPlanEditorOpen = false;
                    }
                }
                else if (selectionChanged && !_preservePlanEditorStateOnRefresh)
                {
                    IsPlanEditorOpen = false;
                }
            }

            if (_selectedPlan is not null)
            {
                _selectedPlan.PropertyChanged += OnSelectedPlanChanged;
            }
        }
    }

    public NoteImageItem? SelectedPlanImage
    {
        get => _selectedPlanImage;
        set
        {
            if (SetProperty(ref _selectedPlanImage, value))
            {
                DeleteSelectedPlanImageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public PlanCalendarDetailItem? SelectedPlanDayDetail
    {
        get => _selectedPlanDayDetail;
        private set
        {
            if (SetProperty(ref _selectedPlanDayDetail, value) && value is not null)
            {
                SelectedPlan = value.Plan;
            }
        }
    }

    public string SelectedPlanTargetCompletedTimeText
    {
        get => _selectedPlanTargetCompletedTimeText;
        set
        {
            if (SetProperty(ref _selectedPlanTargetCompletedTimeText, value))
            {
                ApplySelectedPlanTargetCompletedTimeText();
            }
        }
    }

    public DateTime? SelectedPlanTargetCompletedDate
    {
        get => SelectedPlan?.TargetCompletedAt?.Date;
        set
        {
            if (SelectedPlan is null || value is null)
            {
                return;
            }

            SetSelectedPlanTargetCompletedDate(value.Value);
        }
    }

    public string SelectedPlanActualCompletedTimeText
    {
        get => _selectedPlanActualCompletedTimeText;
        set
        {
            if (SetProperty(ref _selectedPlanActualCompletedTimeText, value))
            {
                ApplySelectedPlanActualCompletedTimeText();
            }
        }
    }

    public DateTime? SelectedPlanActualCompletedDate
    {
        get => SelectedPlan?.ActualCompletedAt?.Date;
        set
        {
            if (SelectedPlan is null || value is null)
            {
                return;
            }

            SetSelectedPlanActualCompletedDate(value.Value);
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

    public int PlanTotalCount => Plans.Count;

    public int PlanCompletedCount => Plans.Count(item => item.Status == PlanItemStatus.Completed);

    public int PlanPendingCount => Plans.Count(item => item.Status != PlanItemStatus.Completed);

    public int PlanOverdueCount => Plans.Count(item =>
        item.Status != PlanItemStatus.Completed
        && item.TargetCompletedAt is { } targetCompletedAt
        && targetCompletedAt < DateTime.Now);

    public int SelectedPlanDateCount => SelectedPlanDayDetails.Count;

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value)
            {
                return;
            }

            if (!StartupManager.SetEnabled(value))
            {
                OnPropertyChanged();
                return;
            }

            var actual = StartupManager.IsEnabled();
            Settings.StartWithWindows = actual;
            if (!SetProperty(ref _startWithWindows, actual))
            {
                OnPropertyChanged();
            }

            Save();
        }
    }

    public bool AutoScanOnDailyFirstLaunch
    {
        get => Settings.AutoScanOnDailyFirstLaunch;
        set
        {
            if (Settings.AutoScanOnDailyFirstLaunch == value)
            {
                return;
            }

            Settings.AutoScanOnDailyFirstLaunch = value;
            OnPropertyChanged();
            Save();
        }
    }

    public bool LoggingEnabled
    {
        get => Settings.LoggingEnabled;
        set
        {
            if (Settings.LoggingEnabled == value)
            {
                return;
            }

            Settings.LoggingEnabled = value;
            AppDiagnostics.Configure(Settings.LoggingEnabled, Settings.LogDirectory);
            OnPropertyChanged();
            Save();
        }
    }

    public bool SystemMonitorEnabled
    {
        get => Settings.SystemMonitorEnabled;
        set
        {
            if (Settings.SystemMonitorEnabled == value)
            {
                return;
            }

            Settings.SystemMonitorEnabled = value;
            if (value)
            {
                ApplySystemMonitorTimerPolicy(refreshImmediately: true);
            }
            else
            {
                StopSystemMonitor();
            }

            OnPropertyChanged();
            Save();
        }
    }

    public string LogDirectory
    {
        get => AppDiagnostics.ResolveLogDirectory(Settings.LogDirectory);
        set
        {
            var normalized = AppDiagnostics.ResolveLogDirectory(value);
            if (Settings.LogDirectory.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Settings.LogDirectory = normalized;
            AppDiagnostics.Configure(Settings.LoggingEnabled, Settings.LogDirectory);
            OnPropertyChanged();
            Save();
        }
    }

    private void BrowseLogDirectory()
    {
        using var dialog = new WinFormsFolderBrowserDialog
        {
            Description = "选择日志输出文件夹",
            SelectedPath = Directory.Exists(LogDirectory) ? LogDirectory : _stateStore.FolderPath,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == WinFormsDialogResult.OK)
        {
            LogDirectory = dialog.SelectedPath;
        }
    }

    private void StopSystemMonitor()
    {
        _systemMonitorTimer.Stop();
        _systemMonitor.ResetSamplingBaseline();
        SystemMonitorNetworkText = "↓ 0 B/s  ↑ 0 B/s";
        SystemMonitorCpuText = "CPU 0%";
        SystemMonitorGpuText = "GPU --";
        SystemMonitorMemoryText = "内存 0%";
    }

    private void OnSystemMonitorTimerTick(object? sender, EventArgs e)
    {
        if (ShouldRunSystemMonitor)
        {
            Observe(RefreshSystemMonitorAsync(), "refresh system monitor");
        }
    }

    private async Task RefreshSystemMonitorAsync()
    {
        if (_disposed || !ShouldRunSystemMonitor || _isRefreshingSystemMonitor)
        {
            return;
        }

        _isRefreshingSystemMonitor = true;
        try
        {
            var snapshot = await Task.Run(_systemMonitor.GetSnapshot);
            SystemMonitorNetworkText = $"↓ {FormatBytesPerSecond(snapshot.DownloadBytesPerSecond)}  ↑ {FormatBytesPerSecond(snapshot.UploadBytesPerSecond)}";
            SystemMonitorCpuText = $"CPU {snapshot.CpuUsagePercent:0}%";
            SystemMonitorGpuText = snapshot.GpuUsagePercent is { } gpuUsage
                ? $"GPU {gpuUsage:0}%"
                : "GPU --";
            SystemMonitorMemoryText = $"内存 {snapshot.MemoryUsagePercent:0}%";
        }
        finally
        {
            _isRefreshingSystemMonitor = false;
        }
    }

    public void SetUiActivityState(bool isMainWindowVisible, bool isSystemMonitorPanelVisible)
    {
        if (_disposed
            || (_isMainWindowVisible == isMainWindowVisible
                && _isSystemMonitorPanelVisible == isSystemMonitorPanelVisible))
        {
            return;
        }

        _isMainWindowVisible = isMainWindowVisible;
        _isSystemMonitorPanelVisible = isSystemMonitorPanelVisible;
        AppDiagnostics.LogInfo(
            $"ui activity state changed; mainVisible={_isMainWindowVisible}; monitorVisible={_isSystemMonitorPanelVisible}",
            "idle");
        ApplyIdleTimerPolicy();
    }

    private bool ShouldRunSystemMonitor => Settings.SystemMonitorEnabled && _isSystemMonitorPanelVisible;

    private void ApplyIdleTimerPolicy()
    {
        if (_disposed)
        {
            return;
        }

        ApplySystemMonitorTimerPolicy(refreshImmediately: _isSystemMonitorPanelVisible);
        if (_isMainWindowVisible)
        {
            if (!_ratesRefreshTimer.IsEnabled)
            {
                _ratesRefreshTimer.Start();
                Observe(RefreshRatesIfStaleAsync(), "refresh rates foreground");
            }

            if (IsIndexingFiles && !_indexProgressTimer.IsEnabled)
            {
                _indexProgressTimer.Start();
                RefreshIndexProgress();
            }

            return;
        }

        _ratesRefreshTimer.Stop();
        _indexProgressTimer.Stop();
        _launcherSearchDebounceTimer.Stop();
        CancelSearch(_launcherSearchCancellation);
        IsSearchingLauncher = false;
    }

    private void ApplySystemMonitorTimerPolicy(bool refreshImmediately = false)
    {
        if (!ShouldRunSystemMonitor)
        {
            _systemMonitorTimer.Stop();
            _systemMonitor.ResetSamplingBaseline();
            return;
        }

        _systemMonitorTimer.Interval = SystemMonitorRefreshInterval;
        if (!_systemMonitorTimer.IsEnabled)
        {
            _systemMonitor.ResetSamplingBaseline();
            _systemMonitorTimer.Start();
        }

        if (refreshImmediately)
        {
            Observe(RefreshSystemMonitorAsync(), "refresh system monitor");
        }
    }

    private void InitializeHotKeyEditors()
    {
        HotKeyEditors.Add(new HotKeyEditorItem(
            "导航",
            AppSection.Launcher,
            Settings.LauncherHotKey,
            DefaultLauncherHotKey,
            HotKeyOptions,
            ShowLauncherCommand,
            ApplyHotKeyEditorChange));
        HotKeyEditors.Add(new HotKeyEditorItem(
            "剪贴板",
            AppSection.Clipboard,
            Settings.ClipboardHotKey,
            DefaultClipboardHotKey,
            HotKeyOptions,
            ShowClipboardCommand,
            ApplyHotKeyEditorChange));
        HotKeyEditors.Add(new HotKeyEditorItem(
            "记事本",
            AppSection.Notes,
            Settings.NotesHotKey,
            DefaultNotesHotKey,
            HotKeyOptions,
            ShowNotesCommand,
            ApplyHotKeyEditorChange));
        HotKeyEditors.Add(new HotKeyEditorItem(
            "设置",
            AppSection.Settings,
            Settings.SettingsHotKey,
            DefaultSettingsHotKey,
            HotKeyOptions,
            ShowSettingsCommand,
            ApplyHotKeyEditorChange));
        HotKeyEditors.Add(new HotKeyEditorItem(
            "计划",
            AppSection.Plans,
            Settings.PlansHotKey,
            DefaultPlansHotKey,
            HotKeyOptions,
            ShowPlansCommand,
            ApplyHotKeyEditorChange));
    }

    private void ApplyHotKeyEditorChange(HotKeyEditorItem editor)
    {
        switch (editor.Section)
        {
            case AppSection.Launcher:
                Settings.LauncherHotKey = editor.Gesture;
                break;
            case AppSection.Clipboard:
                Settings.ClipboardHotKey = editor.Gesture;
                break;
            case AppSection.Notes:
                Settings.NotesHotKey = editor.Gesture;
                break;
            case AppSection.Plans:
                Settings.PlansHotKey = editor.Gesture;
                break;
            case AppSection.Settings:
                Settings.SettingsHotKey = editor.Gesture;
                break;
        }

        Save();
        ApplyRegisteredHotKeys();
    }

    public void ShowSection(AppSection section)
    {
        SelectedSection = section;
        IsPopupOpen = true;
    }

    public void RegisterHotKeys(GlobalHotKeyService hotKeys, Action<AppSection>? onSectionOpened = null)
    {
        _registeredHotKeys = hotKeys;
        _onRegisteredHotKeyOpened = onSectionOpened;
        ApplyRegisteredHotKeys();
    }

    private void ApplyRegisteredHotKeys()
    {
        if (_registeredHotKeys is null)
        {
            return;
        }

        void Register(HotKeyGesture gesture, AppSection section)
        {
            _registeredHotKeys.Register(gesture, () =>
            {
                ShowSection(section);
                _onRegisteredHotKeyOpened?.Invoke(section);
            });
        }

        _registeredHotKeys.Clear();
        Register(Settings.LauncherHotKey, AppSection.Launcher);
        Register(Settings.ClipboardHotKey, AppSection.Clipboard);
        Register(Settings.NotesHotKey, AppSection.Notes);
        Register(Settings.PlansHotKey, AppSection.Plans);
        Register(Settings.SettingsHotKey, AppSection.Settings);
    }

    public void Save()
    {
        try
        {
            _state.ClipboardItems = ClipboardItems.Take(120).ToList();
            _state.Notes = Notes.ToList();
            _state.PlanItems = Plans.ToList();
            _stateStore.Save(_state);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "view model save");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _indexProgressTimer.Stop();
        _indexProgressTimer.Tick -= OnIndexProgressTimerTick;
        _launcherSearchDebounceTimer.Stop();
        _launcherSearchDebounceTimer.Tick -= OnLauncherSearchDebounceTimerTick;
        _systemMonitorTimer.Stop();
        _systemMonitorTimer.Tick -= OnSystemMonitorTimerTick;
        _ratesRefreshTimer.Stop();
        _ratesRefreshTimer.Tick -= OnRatesRefreshTimerTick;
        _clipboardMonitor.Stop();
        _fileInitializationCancellation.Cancel();
        CancelSearch(_launcherSearchCancellation);
        _launcherSearchCancellation?.Dispose();
        _fileSearch.Dispose();
        _systemMonitor.Dispose();
        _fileInitializationCancellation.Dispose();
    }

    private void SyncStartWithWindowsSetting()
    {
        try
        {
            var savedValue = Settings.StartWithWindows;
            if (savedValue)
            {
                StartupManager.SetEnabled(true);
            }

            _startWithWindows = StartupManager.IsEnabled();
            Settings.StartWithWindows = _startWithWindows;
            if (savedValue != _startWithWindows)
            {
                _stateStore.Save(_state);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "sync startup setting");
            _startWithWindows = false;
            Settings.StartWithWindows = false;
        }
    }

    private void QueueLauncherSearch()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _launcherSearchVersion);
        CancelSearch(_launcherSearchCancellation);
        _launcherSearchDebounceTimer.Stop();
        _launcherSearchDebounceTimer.Start();
    }

    private void OnLauncherSearchDebounceTimerTick(object? sender, EventArgs e)
    {
        _launcherSearchDebounceTimer.Stop();
        if (_disposed)
        {
            return;
        }

        Observe(RefreshLauncherResultsAsync(), "launcher search");
    }

    private static void CancelSearch(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task InitializeFileIndexAsync()
    {
        try
        {
            var cancellationToken = _fileInitializationCancellation.Token;
            IndexProgressValue = 0;
            IndexProgressText = "正在加载本地索引缓存...";
            LauncherStatusText = IndexProgressText;
            IsIndexingFiles = true;
            _indexProgressTimer.Start();
            AppDiagnostics.LogInfo("init: load local index cache first", "index startup");

            var loadedCache = await _fileSearch.LoadCacheAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (loadedCache)
            {
                var completingCacheLoad = _fileSearch.IsCompletingCacheLoad;
                AppDiagnostics.LogInfo(
                    completingCacheLoad
                        ? "app cache loaded; full file cache continues in background"
                        : Settings.AutoScanOnDailyFirstLaunch
                            ? "cache loaded; enable NTFS USN delta + FileSystemWatcher"
                            : "cache loaded; startup auto update is disabled",
                    "index startup");
                IndexProgressValue = 100;
                IndexProgressText = completingCacheLoad
                    ? $"已加载应用缓存，收录 {_fileSearch.IndexedCount:N0} 项"
                    : Settings.AutoScanOnDailyFirstLaunch
                    ? $"已加载本地索引缓存，收录 {_fileSearch.IndexedCount:N0} 项，已启用增量更新"
                    : $"已加载本地索引缓存，收录 {_fileSearch.IndexedCount:N0} 项";
                IsIndexingFiles = false;
                if (Settings.AutoScanOnDailyFirstLaunch)
                {
                    _fileSearch.StartIncrementalIndexing();
                }

                LauncherStatusText = "输入关键词搜索本机文件";
                _indexProgressTimer.Stop();
                QueueLauncherSearch();
                return;
            }

            if (!Settings.AutoScanOnDailyFirstLaunch)
            {
                AppDiagnostics.LogInfo("no cache; startup auto update is disabled", "index startup");
                IndexProgressValue = 0;
                IndexProgressText = "没有本地索引缓存，可手动重新扫描硬盘数据";
                IsIndexingFiles = false;
                LauncherStatusText = IndexProgressText;
                _indexProgressTimer.Stop();
                QueueLauncherSearch();
                return;
            }

            AppDiagnostics.LogInfo("no cache; rebuild index; NTFS roots try MFT first", "index startup");
            _fileSearch.RebuildIndex();
            Settings.LastFileIndexScanDate = DateOnly.FromDateTime(DateTime.Now);
            Save();
            _indexProgressTimer.Start();
            RefreshIndexProgress();
            QueueLauncherSearch();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "initialize file index");
            IsIndexingFiles = false;
            _indexProgressTimer.Stop();
        }
    }

    private void OnIndexProgressTimerTick(object? sender, EventArgs e)
    {
        try
        {
            RefreshIndexProgress();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "refresh index progress");
            IsIndexingFiles = false;
            _indexProgressTimer.Stop();
        }
    }

    private void RefreshIndexProgress()
    {
        var progress = _fileSearch.GetProgress();
        IsIndexingFiles = progress.IsIndexing;
        IndexProgressValue = progress.CompletionRatio * 100;

        if (progress.IsLoadingCache)
        {
            IndexProgressText = $"正在加载本地索引缓存，已收录 {progress.IndexedCount:N0} 项";
            if (progress.CacheBytesTotal > 0)
            {
                var readMb = progress.CacheBytesRead / 1024d / 1024d;
                var totalMb = progress.CacheBytesTotal / 1024d / 1024d;
                IndexProgressText = progress.IndexedCount > 0
                    ? $"正在构建本地索引缓存，已收录 {progress.IndexedCount:N0} 项，已读取 {readMb:N0}/{totalMb:N0} MB"
                    : $"正在读取本地索引缓存，已读取 {readMb:N0}/{totalMb:N0} MB";
            }

            if (string.IsNullOrWhiteSpace(LauncherQuery) && FilteredApps.Count == 0)
            {
                LauncherStatusText = IndexProgressText;
            }
            return;
        }

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
        _fileInitializationCancellation.Cancel();
        CancelSearch(_launcherSearchCancellation);
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

    private async Task ClearCacheDataAsync()
    {
        var firstConfirm = WpfMessageBox.Show(
            "确定要清除所有导航缓存数据吗？清除后需要重新扫描硬盘数据才能恢复完整文件/文件夹搜索。",
            "清除缓存数据",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (firstConfirm != MessageBoxResult.Yes)
        {
            return;
        }

        var secondConfirm = WpfMessageBox.Show(
            "请再次确认：这会删除本地索引缓存和 NTFS 增量缓存，当前导航索引也会被清空。",
            "再次确认清除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (secondConfirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _isClearingCacheData = true;
            ClearCacheDataCommand.RaiseCanExecuteChanged();
            _fileInitializationCancellation.Cancel();
            var deleted = await _fileSearch.ClearCacheDataAsync();
            Interlocked.Increment(ref _launcherSearchVersion);
            CancelSearch(_launcherSearchCancellation);
            _launcherSearchCancellation = null;
            _indexProgressTimer.Stop();
            FilteredApps.Clear();
            SelectedApp = null;
            IsIndexingFiles = false;
            IndexProgressValue = 0;
            IndexProgressText = $"已清除导航缓存数据，删除 {deleted:N0} 个缓存文件，可手动重新扫描硬盘数据";
            LauncherStatusText = IndexProgressText;
            Settings.LastFileIndexScanDate = null;
            Save();
            WpfMessageBox.Show(
                $"已清除导航缓存数据，删除 {deleted:N0} 个缓存文件。",
                "清除完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "clear cache data");
            WpfMessageBox.Show(
                "清除缓存数据失败，详情已写入日志。",
                "清除失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isClearingCacheData = false;
            ClearCacheDataCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RefreshLauncherResultsAsync()
    {
        var query = LauncherQuery.Trim();
        var version = Interlocked.Increment(ref _launcherSearchVersion);
        var oldCancellation = _launcherSearchCancellation;
        CancelSearch(oldCancellation);

        if (string.IsNullOrWhiteSpace(query))
        {
            _launcherSearchCancellation = null;
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

        if (TryCreateDirectNavigationResult(query, out var directNavigationResult))
        {
            _launcherSearchCancellation = null;
            Replace(FilteredApps, new[] { directNavigationResult });
            SelectedApp = FilteredApps.FirstOrDefault();
            IsSearchingLauncher = false;
            LauncherStatusText = directNavigationResult.ActionKind == LauncherActionKind.OpenUrl
                ? "按回车或双击使用默认浏览器打开网址"
                : "按回车或双击使用默认浏览器搜索网页";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _launcherSearchCancellation = cancellation;
        IsSearchingLauncher = true;
        LauncherStatusText = "正在搜索本机文件...";

        try
        {
            var response = await _fileSearch.SearchAsync(query, 200, cancellation.Token);
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
                .Take(80)
                .ToList();
            var localResultCount = rankedResults.Count;
            var webSearchResult = ShouldOfferWebSearch(query, localResultCount)
                ? CreateWebSearchResult(query)
                : null;

            if (webSearchResult is not null)
            {
                if (localResultCount == 0 || LooksLikeNaturalWebSearch(query))
                {
                    rankedResults.Insert(0, webSearchResult);
                }
                else if (rankedResults.Count < 80)
                {
                    rankedResults.Add(webSearchResult);
                }
            }

            Replace(FilteredApps, rankedResults);
            SelectedApp = FilteredApps.FirstOrDefault();
            RefreshIndexProgress();
            var indexText = response.IsIndexing ? $"，索引中 {response.IndexedCount:N0} 项" : string.Empty;
            LauncherStatusText = localResultCount == 0 && webSearchResult is not null
                ? $"没有本机文件结果，可使用默认浏览器搜索网页{indexText}"
                : localResultCount == 0
                ? $"没有找到本机文件结果{indexText}"
                : $"{localResultCount} 个本机文件结果{indexText}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "launcher search");
        }
        finally
        {
            if (version == _launcherSearchVersion)
            {
                IsSearchingLauncher = false;
                if (ReferenceEquals(_launcherSearchCancellation, cancellation))
                {
                    _launcherSearchCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private void RefreshToolResults()
    {
        try
        {
            CurrencyResult = CurrencyConverter.Convert(LauncherQuery, Settings.ExchangeRatesToCny);
            CalculatorResult = CurrencyResult is null && ExpressionEvaluator.LooksLikeCalculation(LauncherQuery)
                ? ExpressionEvaluator.Evaluate(LauncherQuery)
                : null;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "refresh tool results");
            CurrencyResult = null;
            CalculatorResult = null;
        }
        finally
        {
            OnPropertyChanged(nameof(CalculatorResultText));
            OnPropertyChanged(nameof(CurrencyResultText));
            OnPropertyChanged(nameof(HasToolResult));
        }
    }

    private bool ShouldOfferWebSearch(string query, int localResultCount)
    {
        if (CalculatorResult is not null || CurrencyResult is not null)
        {
            return false;
        }

        if (!HasSearchableText(query) || IsLikelyLocalSearchSyntax(query))
        {
            return false;
        }

        return localResultCount == 0 || LooksLikeNaturalWebSearch(query);
    }

    private static bool TryCreateDirectNavigationResult(string query, out LauncherSearchResult result)
    {
        if (TryCreateUrlResult(query, out result))
        {
            return true;
        }

        if (TryGetExplicitWebSearchText(query, out var searchText))
        {
            result = CreateWebSearchResult(searchText);
            return true;
        }

        result = null!;
        return false;
    }

    private static bool TryCreateUrlResult(string query, out LauncherSearchResult result)
    {
        result = null!;
        var text = query.Trim();
        if (!HasSearchableText(text)
            || text.Any(char.IsWhiteSpace)
            || text.Contains('\\')
            || IsLikelyDrivePath(text))
        {
            return false;
        }

        string url;
        if (Uri.TryCreate(text, UriKind.Absolute, out var absoluteUri)
            && (absoluteUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || absoluteUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            url = absoluteUri.AbsoluteUri;
        }
        else if (LooksLikeUrlWithoutScheme(text))
        {
            url = $"https://{text}";
        }
        else
        {
            return false;
        }

        result = new LauncherSearchResult
        {
            Name = $"打开网址：{text}",
            Path = url,
            Kind = "网址",
            ActionKind = LauncherActionKind.OpenUrl,
            SearchScore = int.MaxValue,
            SearchSortName = text.ToLowerInvariant()
        };
        return true;
    }

    private static bool TryGetExplicitWebSearchText(string query, out string searchText)
    {
        var text = query.Trim();
        if (text.StartsWith("?", StringComparison.Ordinal))
        {
            searchText = text[1..].Trim();
            return HasSearchableText(searchText);
        }

        foreach (var prefix in new[] { "web:", "search:", "搜索:", "搜:" })
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            searchText = text[prefix.Length..].Trim();
            return HasSearchableText(searchText);
        }

        foreach (var prefix in new[] { "搜索 ", "搜 " })
        {
            if (!text.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            searchText = text[prefix.Length..].Trim();
            return HasSearchableText(searchText);
        }

        searchText = string.Empty;
        return false;
    }

    private static LauncherSearchResult CreateWebSearchResult(string query)
    {
        var searchText = query.Trim();
        return new LauncherSearchResult
        {
            Name = $"搜索网页：{searchText}",
            Path = BuildWebSearchUrl(searchText),
            Kind = "网页搜索",
            ActionKind = LauncherActionKind.WebSearch,
            SearchScore = int.MaxValue - 1,
            SearchSortName = searchText.ToLowerInvariant()
        };
    }

    private static string BuildWebSearchUrl(string query)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            WebSearchUrlTemplate,
            WebUtility.UrlEncode(query));
    }

    private static bool LooksLikeNaturalWebSearch(string query)
    {
        var text = query.Trim();
        if (!HasSearchableText(text) || IsLikelyLocalSearchSyntax(text))
        {
            return false;
        }

        if (text.EndsWith("?", StringComparison.Ordinal) || text.EndsWith("？", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var marker in new[]
                 {
                     "如何",
                     "怎么",
                     "怎样",
                     "为什么",
                     "是什么",
                     "哪里",
                     "教程",
                     "报错",
                     "错误",
                     "安装",
                     "配置"
                 })
        {
            if (text.Contains(marker, StringComparison.CurrentCultureIgnoreCase))
            {
                return true;
            }
        }

        var lower = $" {text.ToLowerInvariant()} ";
        foreach (var marker in new[]
                 {
                     " how ",
                     " what ",
                     " why ",
                     " where ",
                     " when ",
                     " who ",
                     " tutorial ",
                     " error ",
                     " exception ",
                     " install ",
                     " configure ",
                     " config ",
                     " fix ",
                     " download "
                 })
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeUrlWithoutScheme(string text)
    {
        var host = text;
        var hostEnd = host.IndexOfAny(['/', '?', '#']);
        if (hostEnd >= 0)
        {
            host = host[..hostEnd];
        }

        var portStart = host.LastIndexOf(':');
        if (portStart >= 0)
        {
            var port = host[(portStart + 1)..];
            if (port.Length == 0 || !port.All(char.IsDigit))
            {
                return false;
            }

            host = host[..portStart];
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out _))
        {
            return true;
        }

        if (host.Length == 0 || !host.Contains('.') || host.Contains(".."))
        {
            return false;
        }

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2)
        {
            return false;
        }

        var tld = labels[^1];
        return tld.Length >= 2
            && tld.All(char.IsLetter)
            && (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || IsCommonWebTld(tld))
            && labels.All(IsValidDomainLabel);
    }

    private static bool IsCommonWebTld(string tld)
    {
        return tld.ToLowerInvariant() is
            "com" or
            "net" or
            "org" or
            "io" or
            "dev" or
            "app" or
            "ai" or
            "co" or
            "cn" or
            "us" or
            "uk" or
            "jp" or
            "de" or
            "fr" or
            "ru" or
            "edu" or
            "gov" or
            "info" or
            "biz" or
            "me" or
            "tv" or
            "xyz" or
            "site" or
            "online" or
            "store" or
            "tech" or
            "top" or
            "cc" or
            "cloud";
    }

    private static bool IsValidDomainLabel(string label)
    {
        return label.Length > 0
            && label[0] != '-'
            && label[^1] != '-'
            && label.All(character => char.IsLetterOrDigit(character) || character == '-');
    }

    private static bool IsLikelyLocalSearchSyntax(string query)
    {
        var text = query.Trim();
        if (text.Contains('\\')
            || IsLikelyDrivePath(text)
            || text.StartsWith("/", StringComparison.Ordinal)
            || text.StartsWith("./", StringComparison.Ordinal)
            || text.StartsWith("../", StringComparison.Ordinal)
            || text.Contains('*'))
        {
            return true;
        }

        if (text.Contains('?')
            && !text.EndsWith("?", StringComparison.Ordinal)
            && !text.EndsWith("？", StringComparison.Ordinal))
        {
            return true;
        }

        var lower = text.ToLowerInvariant();
        foreach (var marker in new[]
                 {
                     "file:",
                     "files:",
                     "folder:",
                     "folders:",
                     "ext:",
                     "path:",
                     "parent:",
                     "regex:",
                     "case:",
                     "nocase:",
                     "noregex:",
                     "doc:",
                     "pic:",
                     "audio:",
                     "video:",
                     "zip:",
                     "exe:"
                 })
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLikelyDrivePath(string text)
    {
        return text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':';
    }

    private static bool HasSearchableText(string text)
    {
        return !string.IsNullOrWhiteSpace(text) && text.Any(char.IsLetterOrDigit);
    }

    private LauncherSearchResult ApplyUsageCount(LauncherSearchResult item)
    {
        return new LauncherSearchResult
        {
            Name = item.Name,
            Path = item.Path,
            Kind = item.Kind,
            ActionKind = item.ActionKind,
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
            ActionKind = item.ActionKind,
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
        Observe(RefreshLauncherResultsAsync(), "refresh launcher after clearing usage");
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
            if (SelectedApp.ActionKind is LauncherActionKind.OpenUrl or LauncherActionKind.WebSearch)
            {
                Process.Start(new ProcessStartInfo(SelectedApp.Path) { UseShellExecute = true });
                IsPopupOpen = false;
                return;
            }

            Process.Start(new ProcessStartInfo(SelectedApp.Path) { UseShellExecute = true });
            IncrementLaunchCount(SelectedApp);
            IsPopupOpen = false;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "open launcher item");
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
            "File" => items.Where(item => item.Kind == ClipboardItemKind.File),
            "Link" => items.Where(item => item.Kind == ClipboardItemKind.Link),
            "Color" => items.Where(item => item.Kind == ClipboardItemKind.Color),
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
            switch (SelectedClipboardEntry.Kind)
            {
                case ClipboardItemKind.Image when SelectedClipboardEntry.ImageBytes is not null:
                {
                    using var stream = new MemoryStream(SelectedClipboardEntry.ImageBytes);
                    var image = System.Windows.Media.Imaging.BitmapFrame.Create(
                        stream,
                        System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    var normalizedImage = ClipboardImageNormalizer.RestoreOpaqueAlphaIfFullyTransparent(image);
                    WpfClipboard.SetImage(normalizedImage);
                    _clipboardMonitor.RememberImage(normalizedImage);
                    break;
                }
                case ClipboardItemKind.File:
                    CopyFileClipboardEntry(SelectedClipboardEntry);
                    break;
                default:
                    WpfClipboard.SetText(SelectedClipboardEntry.Content);
                    _clipboardMonitor.RememberText(SelectedClipboardEntry.Content);
                    break;
            }
        }
        catch (Exception ex)
        {
            _clipboardMonitor.IgnoreNextChange = false;
            AppDiagnostics.LogException(ex, "copy clipboard item");
            return;
        }

        IsPopupOpen = false;
    }

    private void CopyFileClipboardEntry(ClipboardEntry entry)
    {
        var paths = ClipboardClassifier.ExtractExistingFilePaths(entry.Content);
        if (paths.Count == 0)
        {
            WpfClipboard.SetText(entry.Content);
            _clipboardMonitor.RememberText(entry.Content);
            return;
        }

        var fileDropList = new StringCollection();
        foreach (var path in paths)
        {
            fileDropList.Add(path);
        }

        WpfClipboard.SetFileDropList(fileDropList);
        _clipboardMonitor.RememberText(ClipboardClassifier.NormalizeFileContent(paths));
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

    private void RefreshPlanResults()
    {
        var selectedId = SelectedPlan?.Id;
        var keepEditorOpen = IsPlanEditorOpen;
        var query = PlanQuery.Trim();
        IEnumerable<PlanItem> items = Plans;

        items = PlanStatusFilter switch
        {
            "NotStarted" => items.Where(item => item.Status == PlanItemStatus.NotStarted),
            "InProgress" => items.Where(item => item.Status == PlanItemStatus.InProgress),
            "Completed" => items.Where(item => item.Status == PlanItemStatus.Completed),
            "Blocked" => items.Where(item => item.Status == PlanItemStatus.Blocked),
            _ => items
        };

        items = PlanPriorityFilter switch
        {
            "High" => items.Where(item => item.Priority == PlanPriority.High),
            "Medium" => items.Where(item => item.Priority == PlanPriority.Medium),
            "Low" => items.Where(item => item.Priority == PlanPriority.Low),
            _ => items
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            items = items.Where(item =>
                item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || item.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        items = items
            .OrderBy(item => item.Status == PlanItemStatus.Completed)
            .ThenByDescending(item => item.Priority)
            .ThenBy(item => item.TargetCompletedAt ?? DateTime.MaxValue)
            .ThenByDescending(item => item.UpdatedAt);

        Replace(FilteredPlans, items);
        var nextSelectedPlan = selectedId is null
            ? FilteredPlans.FirstOrDefault()
            : FilteredPlans.FirstOrDefault(item => item.Id == selectedId)
                ?? FilteredPlans.FirstOrDefault();
        _ignoreTransientPlanSelectionClearing = true;
        _preservePlanEditorStateOnRefresh = true;
        try
        {
            SelectedPlan = nextSelectedPlan;
        }
        finally
        {
            _preservePlanEditorStateOnRefresh = false;
            _ignoreTransientPlanSelectionClearing = false;
        }

        if (nextSelectedPlan is not null)
        {
            IsPlanEditorOpen = keepEditorOpen;
        }

        RefreshPlanCalendar();
        NotifyPlanSummaryChanged();
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

    private void AddPlan()
    {
        var now = DateTime.Now;
        var item = new PlanItem
        {
            Title = string.IsNullOrWhiteSpace(NewPlanTitle) ? "新计划" : NewPlanTitle.Trim(),
            Status = PlanItemStatus.NotStarted,
            Priority = PlanPriority.Medium,
            StartAt = now
        };

        Plans.Insert(0, item);
        NewPlanTitle = string.Empty;
        RefreshPlanResults();
        SelectedPlan = item;
        SelectedPlanDate = item.StartAt.Date;
        IsPlanEditorOpen = false;
        Save();
    }

    private void DeleteSelectedPlan()
    {
        if (SelectedPlan is null)
        {
            return;
        }

        IsPlanEditorOpen = false;
        Plans.Remove(SelectedPlan);
        RefreshPlanResults();
        Save();
    }

    private void MarkSelectedPlanCompleted()
    {
        ApplySelectedPlanStatus(PlanItemStatus.Completed);
    }

    private void TogglePlanEditor()
    {
        if (SelectedPlan is null)
        {
            return;
        }

        IsPlanEditorOpen = !IsPlanEditorOpen;
    }

    private void ClosePlanEditor()
    {
        IsPlanEditorOpen = false;
    }

    private void SetSelectedPlanStatus(object? parameter)
    {
        if (parameter is PlanItemStatus status)
        {
            ApplySelectedPlanStatus(status);
            return;
        }

        if (parameter is string text && Enum.TryParse<PlanItemStatus>(text, out var parsed))
        {
            ApplySelectedPlanStatus(parsed);
        }
    }

    private void SetSelectedPlanPriority(object? parameter)
    {
        if (SelectedPlan is null)
        {
            return;
        }

        if (parameter is PlanPriority priority)
        {
            SelectedPlan.Priority = priority;
            TouchSelectedPlan();
            return;
        }

        if (parameter is string text && Enum.TryParse<PlanPriority>(text, out var parsed))
        {
            SelectedPlan.Priority = parsed;
            TouchSelectedPlan();
        }
    }

    private void SetSelectedPlanTargetCompletedAt(object? parameter)
    {
        if (SelectedPlan is null || parameter is not DateTime date)
        {
            return;
        }

        SetSelectedPlanTargetCompletedDate(date);
    }

    private void ClearSelectedPlanTargetCompletedAt()
    {
        if (SelectedPlan is null)
        {
            return;
        }

        SelectedPlan.TargetCompletedAt = null;
        SelectedPlanTargetCompletedTimeText = string.Empty;
        TouchSelectedPlan();
    }

    private void ClearSelectedPlanActualCompletedAt()
    {
        if (SelectedPlan is null)
        {
            return;
        }

        SelectedPlan.ActualCompletedAt = null;
        SelectedPlanActualCompletedTimeText = string.Empty;
        TouchSelectedPlan();
    }

    private void ApplySelectedPlanStatus(PlanItemStatus status)
    {
        if (SelectedPlan is null)
        {
            return;
        }

        var changed = false;
        if (SelectedPlan.Status != status)
        {
            SelectedPlan.Status = status;
            changed = true;
        }

        if (status == PlanItemStatus.Completed)
        {
            if (SelectedPlan.ActualCompletedAt is null)
            {
                SelectedPlan.ActualCompletedAt = DateTime.Now;
                SelectedPlanActualCompletedTimeText = FormatPlanTime(SelectedPlan.ActualCompletedAt.Value);
                changed = true;
            }
        }
        else if (SelectedPlan.ActualCompletedAt is not null)
        {
            SelectedPlan.ActualCompletedAt = null;
            SelectedPlanActualCompletedTimeText = string.Empty;
            changed = true;
        }

        if (changed)
        {
            if (SelectedPlan is not null)
            {
                SelectedPlanDate = SelectedPlan.TargetCompletedAt?.Date
                    ?? SelectedPlan.ActualCompletedAt?.Date
                    ?? SelectedPlan.StartAt.Date;
            }

            TouchSelectedPlan();
        }
    }

    private void ApplySelectedPlanTargetCompletedTimeText()
    {
        if (SelectedPlan?.TargetCompletedAt is not { } targetCompletedAt)
        {
            return;
        }

        var time = ParsePlanTimeText(SelectedPlanTargetCompletedTimeText);
        var next = targetCompletedAt.Date.Add(time);
        if (next == targetCompletedAt)
        {
            return;
        }

        SelectedPlan.TargetCompletedAt = next;
        SelectedPlanDate = next.Date;
        TouchSelectedPlan();
    }

    private void ApplySelectedPlanActualCompletedTimeText()
    {
        if (SelectedPlan?.ActualCompletedAt is not { } actualCompletedAt)
        {
            return;
        }

        var time = ParsePlanTimeText(SelectedPlanActualCompletedTimeText);
        var next = actualCompletedAt.Date.Add(time);
        if (next == actualCompletedAt)
        {
            return;
        }

        SelectedPlan.ActualCompletedAt = next;
        SelectedPlanDate = next.Date;
        TouchSelectedPlan();
    }

    private void SetSelectedPlanTargetCompletedDate(DateTime date)
    {
        if (SelectedPlan is null)
        {
            return;
        }

        var time = SelectedPlan.TargetCompletedAt?.TimeOfDay
            ?? ParsePlanTimeText(SelectedPlanTargetCompletedTimeText);
        var next = date.Date.Add(time);
        if (SelectedPlan.TargetCompletedAt == next)
        {
            return;
        }

        SelectedPlan.TargetCompletedAt = next;
        TouchSelectedPlan();
    }

    private void SetSelectedPlanActualCompletedDate(DateTime date)
    {
        if (SelectedPlan is null)
        {
            return;
        }

        var time = SelectedPlan.ActualCompletedAt?.TimeOfDay
            ?? ParsePlanTimeText(SelectedPlanActualCompletedTimeText);
        var next = date.Date.Add(time);
        if (SelectedPlan.ActualCompletedAt == next)
        {
            return;
        }

        SelectedPlan.ActualCompletedAt = next;
        TouchSelectedPlan();
    }

    private void SyncSelectedPlanTimeEditors()
    {
        var targetText = SelectedPlan?.TargetCompletedAt is { } targetCompletedAt
            ? FormatPlanTime(targetCompletedAt)
            : string.Empty;
        var actualText = SelectedPlan?.ActualCompletedAt is { } actualCompletedAt
            ? FormatPlanTime(actualCompletedAt)
            : string.Empty;

        SetProperty(ref _selectedPlanTargetCompletedTimeText, targetText, nameof(SelectedPlanTargetCompletedTimeText));
        SetProperty(ref _selectedPlanActualCompletedTimeText, actualText, nameof(SelectedPlanActualCompletedTimeText));
        OnPropertyChanged(nameof(SelectedPlanTargetCompletedDate));
        OnPropertyChanged(nameof(SelectedPlanActualCompletedDate));
    }

    private void ChangePlanCalendarMonth(int offset)
    {
        PlanCalendarMonth = PlanCalendarMonth.AddMonths(offset);
    }

    private void ResetPlanCalendarToToday()
    {
        PlanCalendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        SelectedPlanDate = DateTime.Today;
    }

    private void SelectPlanCalendarDay(object? parameter)
    {
        if (parameter is PlanCalendarDayItem day)
        {
            if (!day.IsCurrentMonth)
            {
                PlanCalendarMonth = new DateTime(day.Date.Year, day.Date.Month, 1);
            }

            SelectedPlanDate = day.Date;
        }
    }

    private void SelectPlanDayDetail(object? parameter)
    {
        if (parameter is not PlanCalendarDetailItem detail)
        {
            return;
        }

        SelectedPlanDayDetail = detail;
        SelectedPlan = detail.Plan;
        IsPlanEditorOpen = false;
        RefreshSelectedPlanDayDetails();
    }

    private void RefreshPlanCalendar()
    {
        var firstVisibleDate = GetCalendarStartDate(PlanCalendarMonth);
        var days = Enumerable.Range(0, 42)
            .Select(index =>
            {
                var date = firstVisibleDate.AddDays(index);
                return new PlanCalendarDayItem
                {
                    Date = date,
                    IsCurrentMonth = date.Month == PlanCalendarMonth.Month && date.Year == PlanCalendarMonth.Year,
                    IsToday = date == DateTime.Today,
                    Plans = GetPlansForDate(date).ToList()
                };
            })
            .ToList();

        Replace(PlanCalendarDays, days);
        RefreshPlanCalendarSelection();

        if (SelectedPlanDate < firstVisibleDate || SelectedPlanDate > firstVisibleDate.AddDays(41))
        {
            SelectedPlanDate = PlanCalendarMonth;
            return;
        }

        RefreshSelectedPlanDayDetails();
    }

    private void RefreshPlanCalendarSelection()
    {
        foreach (var day in PlanCalendarDays)
        {
            day.IsSelected = day.Date == SelectedPlanDate;
        }
    }

    private void RefreshSelectedPlanDayDetails()
    {
        var items = GetPlansForDate(SelectedPlanDate)
            .Select(plan => new PlanCalendarDetailItem
            {
                Plan = plan,
                DateText = BuildPlanDateRangeText(plan),
                TimeText = BuildPlanTimeText(plan, SelectedPlanDate),
                DurationText = BuildPlanDurationText(plan),
                StatusText = plan.StatusText,
                PriorityText = plan.PriorityText,
                SummaryText = plan.SummaryText,
                TagText = BuildPlanTagText(plan)
            })
            .ToList();

        Replace(SelectedPlanDayDetails, items);

        PlanCalendarDetailItem? nextDetail = null;
        if (SelectedPlan is not null)
        {
            nextDetail = items.FirstOrDefault(item => item.Plan.Id == SelectedPlan.Id);
        }

        nextDetail ??= items.FirstOrDefault();
        SelectedPlanDayDetail = nextDetail;

        if (nextDetail is null)
        {
            SelectedPlan = null;
            IsPlanEditorOpen = false;
        }

        foreach (var item in SelectedPlanDayDetails)
        {
            item.IsSelected = item == nextDetail;
        }

        OnPropertyChanged(nameof(SelectedPlanDateCount));
    }

    private IEnumerable<PlanItem> GetPlansForDate(DateTime date)
    {
        return FilteredPlans
            .Where(plan => IsPlanOnDate(plan, date))
            .OrderBy(plan => plan.Status == PlanItemStatus.Completed)
            .ThenByDescending(plan => plan.Priority)
            .ThenBy(plan => GetPlanSortDate(plan, date))
            .ThenByDescending(plan => plan.UpdatedAt);
    }

    private static bool IsPlanOnDate(PlanItem plan, DateTime date)
    {
        var day = date.Date;
        var startDate = plan.StartAt.Date;
        var endDate = plan.TargetCompletedAt?.Date
            ?? plan.ActualCompletedAt?.Date
            ?? startDate;

        if (endDate < startDate)
        {
            endDate = startDate;
        }

        return day >= startDate && day <= endDate;
    }

    private static DateTime GetPlanSortDate(PlanItem plan, DateTime fallbackDate)
    {
        if (plan.TargetCompletedAt is { } target)
        {
            return target;
        }

        if (plan.ActualCompletedAt is { } actual)
        {
            return actual;
        }

        return plan.StartAt;
    }

    private static DateTime GetCalendarStartDate(DateTime month)
    {
        var firstDay = new DateTime(month.Year, month.Month, 1);
        var offset = ((int)firstDay.DayOfWeek + 6) % 7;
        return firstDay.AddDays(-offset);
    }

    private static string BuildPlanDateRangeText(PlanItem plan)
    {
        var start = plan.StartAt.ToString("yyyy/M/d");
        var end = plan.TargetCompletedAt?.ToString("yyyy/M/d");
        return end is null ? $"日程 {start}" : $"日程 {start} 至 {end}";
    }

    private static string BuildPlanTimeText(PlanItem plan, DateTime selectedDate)
    {
        if (plan.TargetCompletedAt is { } target && target.Date == selectedDate.Date)
        {
            return $"计划完成 {target:HH:mm}";
        }

        if (plan.ActualCompletedAt is { } actual && actual.Date == selectedDate.Date)
        {
            return $"实际完成 {actual:HH:mm}";
        }

        if (plan.StartAt.Date == selectedDate.Date)
        {
            return $"开始时间 {plan.StartAt:HH:mm}";
        }

        return "全天安排";
    }

    private static string BuildPlanDurationText(PlanItem plan)
    {
        var end = plan.TargetCompletedAt?.Date ?? plan.StartAt.Date;
        var days = Math.Max(1, (end - plan.StartAt.Date).Days + 1);
        return days == 1 ? "1天安排" : $"{days}天安排";
    }

    private static string BuildPlanTagText(PlanItem plan)
    {
        return plan.Status switch
        {
            PlanItemStatus.Completed => "已完成",
            PlanItemStatus.Blocked => "已阻塞",
            PlanItemStatus.InProgress when plan.TargetCompletedAt is { } target && target < DateTime.Now => "已延期",
            PlanItemStatus.NotStarted when plan.TargetCompletedAt is { } target && target < DateTime.Now => "已延期",
            _ => "进行中"
        };
    }

    private static TimeSpan ParsePlanTimeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.Zero;
        }

        if (TimeSpan.TryParseExact(value.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var exact))
        {
            return exact;
        }

        if (TimeSpan.TryParse(value.Trim(), CultureInfo.CurrentCulture, out var parsed))
        {
            return new TimeSpan(parsed.Hours, parsed.Minutes, 0);
        }

        return TimeSpan.Zero;
    }

    private static string FormatPlanTime(DateTime value)
    {
        return value.ToString("HH:mm");
    }

    private void TouchSelectedPlan()
    {
        if (SelectedPlan is null)
        {
            return;
        }

        SelectedPlan.UpdatedAt = DateTimeOffset.Now;
        RefreshPlanResults();
        Save();
    }

    private void AddNoteImageFromClipboard()
    {
        if (SelectedNote is null)
        {
            return;
        }

        try
        {
            BitmapSource? image = null;
            if (WpfClipboard.ContainsImage())
            {
                image = WpfClipboard.GetImage();
            }
            else if (WpfClipboard.ContainsFileDropList())
            {
                foreach (var file in WpfClipboard.GetFileDropList().Cast<string>())
                {
                    if (TryLoadNoteImage(file) is not { } fileImage)
                    {
                        continue;
                    }

                    AddNoteImage(fileImage);
                }

                return;
            }

            if (image is null)
            {
                return;
            }

            AddNoteImage(image);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "add note image from clipboard");
        }
    }

    private void AddNoteImageFromFile()
    {
        if (SelectedNote is null)
        {
            return;
        }

        var dialog = new WpfOpenFileDialog
        {
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|所有文件|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            if (TryLoadNoteImage(file) is { } image)
            {
                AddNoteImage(image);
            }
        }
    }

    private void AddNoteImage(BitmapSource image)
    {
        if (SelectedNote is null)
        {
            return;
        }

        var normalized = ClipboardImageNormalizer.RestoreOpaqueAlphaIfFullyTransparent(image);
        var item = new NoteImageItem
        {
            ImageBytes = EncodeNoteImage(normalized)
        };

        SelectedNote.Images.Add(item);
        SelectedNoteImage = item;
        TouchSelectedNote();
    }

    private void AddPlanImageFromClipboard()
    {
        if (SelectedPlan is null)
        {
            return;
        }

        try
        {
            BitmapSource? image = null;
            if (WpfClipboard.ContainsImage())
            {
                image = WpfClipboard.GetImage();
            }
            else if (WpfClipboard.ContainsFileDropList())
            {
                foreach (var file in WpfClipboard.GetFileDropList().Cast<string>())
                {
                    if (TryLoadNoteImage(file) is not { } fileImage)
                    {
                        continue;
                    }

                    AddPlanImage(fileImage);
                }

                return;
            }

            if (image is null)
            {
                return;
            }

            AddPlanImage(image);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "add plan image from clipboard");
        }
    }

    private void AddPlanImageFromFile()
    {
        if (SelectedPlan is null)
        {
            return;
        }

        var dialog = new WpfOpenFileDialog
        {
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|所有文件|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            if (TryLoadNoteImage(file) is { } image)
            {
                AddPlanImage(image);
            }
        }
    }

    private void AddPlanImage(BitmapSource image)
    {
        if (SelectedPlan is null)
        {
            return;
        }

        var normalized = ClipboardImageNormalizer.RestoreOpaqueAlphaIfFullyTransparent(image);
        var item = new NoteImageItem
        {
            ImageBytes = EncodeNoteImage(normalized)
        };

        SelectedPlan.Images.Add(item);
        SelectedPlanImage = item;
        TouchSelectedPlan();
    }

    private void DeleteSelectedPlanImage()
    {
        if (SelectedPlan is null || SelectedPlanImage is null)
        {
            return;
        }

        var index = SelectedPlan.Images.IndexOf(SelectedPlanImage);
        SelectedPlan.Images.Remove(SelectedPlanImage);
        SelectedPlanImage = SelectedPlan.Images.Count == 0
            ? null
            : SelectedPlan.Images[Math.Clamp(index, 0, SelectedPlan.Images.Count - 1)];
        TouchSelectedPlan();
    }

    private void DeleteSelectedNoteImage()
    {
        if (SelectedNote is null || SelectedNoteImage is null)
        {
            return;
        }

        var index = SelectedNote.Images.IndexOf(SelectedNoteImage);
        SelectedNote.Images.Remove(SelectedNoteImage);
        SelectedNoteImage = SelectedNote.Images.Count == 0
            ? null
            : SelectedNote.Images[Math.Clamp(index, 0, SelectedNote.Images.Count - 1)];
        TouchSelectedNote();
    }

    private void TouchSelectedNote()
    {
        if (SelectedNote is null)
        {
            return;
        }

        SelectedNote.UpdatedAt = DateTimeOffset.Now;
        RefreshNoteResults();
        Save();
    }

    private static BitmapSource? TryLoadNoteImage(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var image = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, $"load note image {path}");
            return null;
        }
    }

    private static byte[] EncodeNoteImage(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
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
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "refresh rates");
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
        if (!_isMainWindowVisible)
        {
            return;
        }

        Observe(RefreshRatesAsync(), "refresh rates timer");
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

        try
        {
            _clipboardMonitor.IgnoreNextChange = true;
            WpfClipboard.SetText(text);
            UpsertClipboardEntry(new ClipboardEntry
            {
                Kind = ClipboardItemKind.Text,
                Content = text,
                Source = "RaycastPM"
            });
        }
        catch (Exception ex)
        {
            _clipboardMonitor.IgnoreNextChange = false;
            AppDiagnostics.LogException(ex, "copy calculator result");
        }
    }

    private static string FormatCalculationNumber(double value)
    {
        return value.ToString("#,0.########", CultureInfo.InvariantCulture);
    }

    private static string FormatCurrencyNumber(double value)
    {
        return value.ToString("#,0.##", CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> CreatePlanTimeOptions()
    {
        var values = new List<string>(48);
        for (var hour = 0; hour < 24; hour++)
        {
            values.Add($"{hour:00}:00");
            values.Add($"{hour:00}:30");
        }

        return values;
    }

    private static string FormatBytesPerSecond(double bytesPerSecond)
    {
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        var value = Math.Max(0, bytesPerSecond);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:0} {units[unitIndex]}"
            : $"{value:0.0} {units[unitIndex]}";
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

    private void OnSelectedPlanChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PlanItem item || e.PropertyName == nameof(PlanItem.UpdatedAt))
        {
            return;
        }

        item.UpdatedAt = DateTimeOffset.Now;
        CompleteSelectedPlanCommand.RaiseCanExecuteChanged();
        ClosePlanEditorCommand.RaiseCanExecuteChanged();
        SetSelectedPlanTargetCompletedAtCommand.RaiseCanExecuteChanged();
        ClearSelectedPlanTargetCompletedAtCommand.RaiseCanExecuteChanged();
        ClearSelectedPlanActualCompletedAtCommand.RaiseCanExecuteChanged();
        NotifyPlanSummaryChanged();
        Save();
    }

    private void NotifyPlanSummaryChanged()
    {
        OnPropertyChanged(nameof(PlanTotalCount));
        OnPropertyChanged(nameof(PlanCompletedCount));
        OnPropertyChanged(nameof(PlanPendingCount));
        OnPropertyChanged(nameof(PlanOverdueCount));
    }

    private static void Observe(Task task, string context)
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = ObserveAsync(task, context);
    }

    private static async Task ObserveAsync(Task task, string context)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, context);
        }
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
