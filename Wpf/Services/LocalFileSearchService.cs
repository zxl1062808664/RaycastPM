using System.Collections.Concurrent;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MemoryPack;
using RaycastPM.Models;

namespace RaycastPM.Services;

public sealed partial class LocalFileSearchService : IDisposable
{
    private const int JsonCacheVersion = 1;
    private const int BinaryCacheVersion = 2;
    private const int LegacyMemoryPackCacheVersion = 3;
    private const int CompactMemoryPackCacheVersion = 4;
    private const int MemoryPackCacheVersion = 5;
    private const int FastMemoryPackCacheVersion = 6;
    private const int AppMemoryPackCacheVersion = 1;
    private const int CacheFileBufferSize = 1024 * 1024;
    private const int SingleCharacterCandidateMultiplier = 80;
    private const int StartPrefixCandidateMultiplier = 120;
    private const string BinaryCacheMagic = "RPMIDX2";
    private const string AppKind = "应用";
    private const string FileKind = "文件";
    private const string FolderKind = "文件夹";

    private static readonly string[] LaunchableExtensions = [".exe", ".lnk", ".appref-ms"];
    private static readonly byte[] JsonVersionProperty = Encoding.UTF8.GetBytes("Version");
    private static readonly byte[] JsonItemsProperty = Encoding.UTF8.GetBytes("Items");
    private static readonly byte[] JsonPathProperty = Encoding.UTF8.GetBytes("Path");
    private static readonly byte[] JsonIsDirectoryProperty = Encoding.UTF8.GetBytes("IsDirectory");

    private static readonly EnumerationOptions IndexEnumerationOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    private static readonly string[] SkippedDirectoryNames =
    [
        "$Recycle.Bin",
        "System Volume Information"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> MacroExtensions =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["audio"] = [".aac", ".aif", ".aiff", ".flac", ".m4a", ".mid", ".midi", ".mp3", ".ogg", ".wav", ".wma"],
            ["zip"] = [".7z", ".bz2", ".cab", ".gz", ".iso", ".rar", ".tar", ".tgz", ".xz", ".zip"],
            ["doc"] = [".csv", ".doc", ".docx", ".md", ".pdf", ".ppt", ".pptx", ".rtf", ".txt", ".xls", ".xlsx"],
            ["exe"] = [".appref-ms", ".bat", ".cmd", ".com", ".exe", ".lnk", ".msi"],
            ["pic"] = [".bmp", ".gif", ".heic", ".jpeg", ".jpg", ".png", ".svg", ".tif", ".tiff", ".webp"],
            ["video"] = [".avi", ".flv", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".webm", ".wmv"]
        };

    private ConcurrentDictionary<string, IndexedFileItem> _index = new(StringComparer.OrdinalIgnoreCase);
    private ConcurrentDictionary<string, ConcurrentBag<IndexedFileItem>> _pathRootIndex = new(StringComparer.OrdinalIgnoreCase);
    private ConcurrentDictionary<char, ConcurrentBag<IndexedFileItem>> _nameStartCharacterIndex = new();
    private ConcurrentDictionary<NameBigram, ConcurrentBag<IndexedFileItem>> _nameStartBigramIndex = new();
    private ConcurrentDictionary<char, ConcurrentBag<IndexedFileItem>> _nameCharacterIndex = new();
    private ConcurrentDictionary<NameBigram, ConcurrentBag<IndexedFileItem>> _nameBigramIndex = new();
    private ConcurrentDictionary<NameGram, ConcurrentBag<IndexedFileItem>> _nameTrigramIndex = new();
    private readonly object _indexStartLock = new();
    private readonly object _watcherLock = new();
    private readonly object _cacheSaveLock = new();
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private readonly string _jsonCachePath;
    private readonly string _appMemoryPackCachePath;
    private readonly string _memoryPackCachePath;
    private readonly string _fastMemoryPackCachePath;
    private readonly string _compactMemoryPackCachePath;
    private readonly string _legacyMemoryPackCachePath;
    private readonly string _binaryCachePath;
    private readonly string _ntfsJournalCachePath;
    private readonly ConcurrentDictionary<string, NtfsVolumeIndex> _ntfsVolumeIndexes = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<IndexedFileItem> _itemsSnapshot = [];
    private IReadOnlyDictionary<string, IndexedFileItem[]> _cachedPathRootIndex = new Dictionary<string, IndexedFileItem[]>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<char, IndexedFileItem[]> _cachedNameStartCharacterIndex = new Dictionary<char, IndexedFileItem[]>();
    private IReadOnlyDictionary<NameBigram, IndexedFileItem[]> _cachedNameStartBigramIndex = new Dictionary<NameBigram, IndexedFileItem[]>();
    private IReadOnlyDictionary<char, IndexedFileItem[]> _cachedNameCharacterIndex = new Dictionary<char, IndexedFileItem[]>();
    private IReadOnlyDictionary<NameBigram, IndexedFileItem[]> _cachedNameBigramIndex = new Dictionary<NameBigram, IndexedFileItem[]>();
    private IReadOnlyDictionary<NameGram, IndexedFileItem[]> _cachedNameTrigramIndex = new Dictionary<NameGram, IndexedFileItem[]>();
    private CancellationTokenSource _indexCancellation = new();
    private Task? _indexTask;
    private List<FileSystemWatcher> _watchers = [];
    private int _indexedCount;
    private int _cacheSaveQueued;
    private int _cacheOperationVersion;
    private int _ntfsJournalUpdateQueued;
    private int _nameIndexBuildVersion;
    private int _rootsTotal;
    private int _rootsCompleted;
    private int _directoriesScanned;
    private int _directoriesQueued;
    private int _skippedCount;
    private long _cacheBytesRead;
    private long _cacheBytesTotal;
    private string _currentRoot = string.Empty;
    private bool _isIndexing;
    private bool _isLoadingCache;
    private bool _isCompletingCacheLoad;
    private bool _disposed;

    public LocalFileSearchService(string cacheFolder)
    {
        Directory.CreateDirectory(cacheFolder);
        _jsonCachePath = Path.Combine(cacheFolder, "file-index-cache.json");
        _appMemoryPackCachePath = Path.Combine(cacheFolder, "file-index-app-cache-v1.mpack");
        _fastMemoryPackCachePath = Path.Combine(cacheFolder, "file-index-cache-v4.mpack");
        _memoryPackCachePath = Path.Combine(cacheFolder, "file-index-cache-v3.mpack");
        _compactMemoryPackCachePath = Path.Combine(cacheFolder, "file-index-cache-v2.mpack");
        _legacyMemoryPackCachePath = Path.Combine(cacheFolder, "file-index-cache.mpack");
        _binaryCachePath = Path.Combine(cacheFolder, "file-index-cache.bin");
        _ntfsJournalCachePath = Path.Combine(cacheFolder, "file-index-ntfs-journal.mpack");
    }

    public bool IsIndexing => _isIndexing || _isLoadingCache;
    public int IndexedCount => Volatile.Read(ref _indexedCount);
    public bool IsCompletingCacheLoad => Volatile.Read(ref _isCompletingCacheLoad);

    public Task<bool> LoadCacheAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => LoadCache(cancellationToken), cancellationToken);
    }

    public bool LoadCache()
    {
        return LoadCache(CancellationToken.None);
    }

    private bool LoadCache(CancellationToken cancellationToken)
    {
        if (!File.Exists(_appMemoryPackCachePath)
            && !File.Exists(_fastMemoryPackCachePath)
            && !File.Exists(_memoryPackCachePath)
            && !File.Exists(_compactMemoryPackCachePath)
            && !File.Exists(_legacyMemoryPackCachePath)
            && !File.Exists(_binaryCachePath)
            && !File.Exists(_jsonCachePath))
        {
            AppDiagnostics.LogInfo("local index cache file not found", "index startup");
            return false;
        }

        try
        {
            _isLoadingCache = true;
            _currentRoot = "本地索引缓存";
            if (File.Exists(_appMemoryPackCachePath) && TryLoadAppMemoryPackCache(cancellationToken))
            {
                LoadNtfsJournalCache();
                if (File.Exists(_memoryPackCachePath))
                {
                    QueueFullMemoryPackCacheLoadFromFile(_memoryPackCachePath, migrateAfterLoad: false);
                    AppDiagnostics.LogInfo($"loaded app MemoryPack index cache; apps={IndexedCount:N0}; full cache continues in background", "index startup");
                }
                else
                {
                    AppDiagnostics.LogInfo($"loaded app MemoryPack index cache; apps={IndexedCount:N0}; full cache missing", "index startup");
                }

                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_memoryPackCachePath) && TryLoadMemoryPackCache(cancellationToken))
            {
                LoadNtfsJournalCache();
                AppDiagnostics.LogInfo($"loaded MemoryPack index cache; count={IndexedCount:N0}", "index startup");
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_fastMemoryPackCachePath) && TryLoadFastMemoryPackCache(cancellationToken))
            {
                LoadNtfsJournalCache();
                AppDiagnostics.LogInfo($"loaded legacy fast full-path cache; count={IndexedCount:N0}; migrate=true", "index startup");
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_compactMemoryPackCachePath) && TryLoadCompactMemoryPackCache(cancellationToken))
            {
                LoadNtfsJournalCache();
                AppDiagnostics.LogInfo($"loaded legacy compact index cache; count={IndexedCount:N0}; migrate=true", "index startup");
                QueueCacheSave();
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_legacyMemoryPackCachePath) && TryLoadLegacyMemoryPackCache(cancellationToken))
            {
                LoadNtfsJournalCache();
                AppDiagnostics.LogInfo($"loaded legacy MemoryPack index cache; count={IndexedCount:N0}; migrate=true", "index startup");
                QueueCacheSave();
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_binaryCachePath) && TryLoadLegacyBinaryCache(cancellationToken))
            {
                LoadNtfsJournalCache();
                AppDiagnostics.LogInfo($"loaded legacy binary index cache; count={IndexedCount:N0}; migrate=true", "index startup");
                QueueCacheSave();
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_jsonCachePath) && TryLoadJsonCache(cancellationToken))
            {
                LoadNtfsJournalCache();
                AppDiagnostics.LogInfo($"loaded legacy JSON index cache; count={IndexedCount:N0}; migrate=true", "index startup");
                QueueCacheSave();
                return true;
            }

            AppDiagnostics.LogInfo("no usable local index cache", "index startup");
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "load file index cache");
            ClearIndex();
            return false;
        }
        finally
        {
            if (!Volatile.Read(ref _isCompletingCacheLoad))
            {
                _currentRoot = string.Empty;
            }

            _isLoadingCache = false;
        }
    }

    private void PrepareCacheProgress(string cachePath)
    {
        Interlocked.Exchange(ref _cacheBytesRead, 0);
        Interlocked.Exchange(ref _cacheBytesTotal, File.Exists(cachePath) ? new FileInfo(cachePath).Length : 0);
        Interlocked.Exchange(ref _indexedCount, 0);
    }

    private bool TryLoadAppMemoryPackCache(CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var version = Volatile.Read(ref _cacheOperationVersion);
            AppDiagnostics.LogInfo("app MemoryPack cache deserialize start", "index startup");
            PrepareCacheProgress(_appMemoryPackCachePath);
            using var stream = new FileStream(
                _appMemoryPackCachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CacheFileBufferSize,
                FileOptions.SequentialScan);
            using var progressStream = new ProgressReadStream(stream, bytesRead => Interlocked.Add(ref _cacheBytesRead, bytesRead));
            var cache = MemoryPackSerializer
                .DeserializeAsync<AppMemoryPackedFileIndexCache>(progressStream, cancellationToken: cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            AppDiagnostics.LogInfo(
                $"app MemoryPack cache deserialize done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; apps={cache?.Items?.Length ?? 0:N0}",
                "index startup");
            if (cache?.Version != AppMemoryPackCacheVersion || cache.Items is not { Length: > 0 })
            {
                return false;
            }

            var items = new List<IndexedFileItem>(cache.Items.Length);
            foreach (var item in cache.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(item.Path))
                {
                    continue;
                }

                items.Add(CreateIndexedFileItem(item.Path, item.IsDirectory));
                if ((items.Count & 0x3FF) == 0)
                {
                    Interlocked.Exchange(ref _indexedCount, items.Count);
                }
            }

            if (version != Volatile.Read(ref _cacheOperationVersion))
            {
                return false;
            }

            ReplaceCachedIndex(items);
            AppDiagnostics.LogInfo(
                $"app MemoryPack cache load done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; indexed={IndexedCount:N0}",
                "index startup");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, $"load file index cache {Path.GetFileName(_appMemoryPackCachePath)}");
            ClearIndex();
            return false;
        }
    }

    private bool TryLoadFastMemoryPackCache(CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var version = Volatile.Read(ref _cacheOperationVersion);
            AppDiagnostics.LogInfo("legacy fast full-path cache deserialize start", "index startup");
            PrepareCacheProgress(_fastMemoryPackCachePath);
            using var stream = new FileStream(
                _fastMemoryPackCachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CacheFileBufferSize,
                FileOptions.SequentialScan);
            using var progressStream = new ProgressReadStream(stream, bytesRead => Interlocked.Add(ref _cacheBytesRead, bytesRead));
            var cache = MemoryPackSerializer
                .DeserializeAsync<FastMemoryPackedFileIndexCache>(progressStream, cancellationToken: cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            AppDiagnostics.LogInfo(
                $"legacy fast full-path cache deserialize done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; rawItems={cache?.Items?.Length ?? 0:N0}",
                "index startup");
            if (cache?.Version != FastMemoryPackCacheVersion || cache.Items is not { Length: > 0 })
            {
                return false;
            }

            var appItems = MaterializeFastCacheItems(cache.Items, appsOnly: true, reportProgress: true, cancellationToken);
            if (version != Volatile.Read(ref _cacheOperationVersion))
            {
                return false;
            }

            AppDiagnostics.LogInfo(
                $"legacy fast full-path cache materialize app items done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; apps={appItems.Count:N0}; rawItems={cache.Items.Length:N0}",
                "index startup");
            ReplaceCachedIndex(appItems);
            AppDiagnostics.LogInfo(
                $"legacy fast full-path app cache load done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; indexed={IndexedCount:N0}; fullCacheQueued=true",
                "index startup");
            QueueFullFastMemoryPackCacheLoad(cache, migrateAfterLoad: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, $"load file index cache {Path.GetFileName(_fastMemoryPackCachePath)}");
            ClearIndex();
            return false;
        }
    }

    private List<IndexedFileItem> MaterializeFastCacheItems(
        IReadOnlyList<FastMemoryPackedFileIndexItem> rawItems,
        bool appsOnly,
        bool reportProgress,
        CancellationToken cancellationToken)
    {
        var items = new List<IndexedFileItem>(appsOnly ? Math.Min(rawItems.Count, 8192) : rawItems.Count);
        foreach (var item in rawItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.Path)
                || (appsOnly && !IsLaunchablePath(item.Path, item.IsDirectory)))
            {
                continue;
            }

            items.Add(CreateIndexedFileItem(item.Path, item.IsDirectory));
            if (reportProgress && (items.Count & 0x3FF) == 0)
            {
                Interlocked.Exchange(ref _indexedCount, items.Count);
            }
        }

        return items;
    }

    private void QueueFullFastMemoryPackCacheLoad(FastMemoryPackedFileIndexCache cache, bool migrateAfterLoad)
    {
        var version = Volatile.Read(ref _cacheOperationVersion);
        var cancellationToken = _indexCancellation.Token;
        Volatile.Write(ref _isCompletingCacheLoad, true);
        _currentRoot = "后台加载文件/文件夹缓存";
        _ = Task.Run(() =>
        {
            var originalPriority = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                AppDiagnostics.LogInfo(
                    $"background legacy fast full-path cache materialize start; rawItems={cache.Items.Length:N0}",
                    "index startup");
                var items = MaterializeFastCacheItems(cache.Items, appsOnly: false, reportProgress: false, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (version != Volatile.Read(ref _cacheOperationVersion))
                {
                    AppDiagnostics.LogInfo("background legacy fast full-path cache discarded; version changed", "index startup");
                    return;
                }

                AppDiagnostics.LogInfo(
                    $"background legacy fast full-path cache materialize done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; items={items.Count:N0}",
                    "index startup");
                ReplaceCachedIndex(items);
                AppDiagnostics.LogInfo(
                    $"background legacy fast full-path cache load done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; indexed={IndexedCount:N0}",
                    "index startup");
                if (migrateAfterLoad)
                {
                    QueueCacheSave();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogException(ex, "background legacy fast full-path cache load");
            }
            finally
            {
                Thread.CurrentThread.Priority = originalPriority;
                if (version == Volatile.Read(ref _cacheOperationVersion))
                {
                    _currentRoot = string.Empty;
                    Volatile.Write(ref _isCompletingCacheLoad, false);
                }
            }
        });
    }

    private bool TryLoadMemoryPackCache(CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var version = Volatile.Read(ref _cacheOperationVersion);
            AppDiagnostics.LogInfo("MemoryPack cache deserialize start", "index startup");
            PrepareCacheProgress(_memoryPackCachePath);
            using var stream = new FileStream(
                _memoryPackCachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CacheFileBufferSize,
                FileOptions.SequentialScan);
            using var progressStream = new ProgressReadStream(stream, bytesRead => Interlocked.Add(ref _cacheBytesRead, bytesRead));
            var cache = MemoryPackSerializer
                .DeserializeAsync<MemoryPackedFileIndexCache>(progressStream, cancellationToken: cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            AppDiagnostics.LogInfo(
                $"MemoryPack cache deserialize done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; rawItems={cache?.Items?.Length ?? 0:N0}",
                "index startup");
            if (cache?.Version != MemoryPackCacheVersion
                || cache.Directories is not { Length: > 0 }
                || cache.Items is not { Length: > 0 })
            {
                return false;
            }

            AppDiagnostics.LogInfo(
                $"MemoryPack cache materialize app items start; rawItems={cache.Items.Length:N0}; directories={cache.Directories.Length:N0}",
                "index startup");
            var appItems = MaterializeMemoryPackCacheItems(cache, appsOnly: true, reportProgress: true, cancellationToken);
            if (version != Volatile.Read(ref _cacheOperationVersion))
            {
                return false;
            }

            AppDiagnostics.LogInfo(
                $"MemoryPack cache materialize app items done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; apps={appItems.Count:N0}",
                "index startup");
            ReplaceCachedIndex(appItems);
            AppDiagnostics.LogInfo(
                $"MemoryPack app cache load done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; indexed={IndexedCount:N0}; fullCacheQueued=true",
                "index startup");
            QueueFullMemoryPackCacheLoad(cache, migrateAfterLoad: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, $"load file index cache {Path.GetFileName(_memoryPackCachePath)}");
            ClearIndex();
            return false;
        }
    }

    private List<IndexedFileItem> MaterializeMemoryPackCacheItems(
        MemoryPackedFileIndexCache cache,
        bool appsOnly,
        bool reportProgress,
        CancellationToken cancellationToken)
    {
        var directories = cache.Directories;
        var rawItems = cache.Items;
        var items = new List<IndexedFileItem>(appsOnly ? Math.Min(rawItems.Length, 8192) : rawItems.Length);
        foreach (var item in rawItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)item.DirectoryIndex >= (uint)directories.Length
                || string.IsNullOrWhiteSpace(item.Name)
                || (appsOnly && !IsLaunchableName(item.Name, item.IsDirectory)))
            {
                continue;
            }

            items.Add(CreateIndexedFileItem(CombineCachedPath(directories[item.DirectoryIndex], item.Name), item.IsDirectory));
            if (reportProgress && (items.Count & 0x3FF) == 0)
            {
                Interlocked.Exchange(ref _indexedCount, items.Count);
            }
        }

        return items;
    }

    private void QueueFullMemoryPackCacheLoad(MemoryPackedFileIndexCache cache, bool migrateAfterLoad)
    {
        var version = Volatile.Read(ref _cacheOperationVersion);
        var cancellationToken = _indexCancellation.Token;
        Volatile.Write(ref _isCompletingCacheLoad, true);
        _currentRoot = "后台加载文件/文件夹缓存";
        _ = Task.Run(() =>
        {
            var originalPriority = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                AppDiagnostics.LogInfo(
                    $"background MemoryPack cache materialize start; rawItems={cache.Items.Length:N0}; directories={cache.Directories.Length:N0}",
                    "index startup");
                var items = MaterializeMemoryPackCacheItems(cache, appsOnly: false, reportProgress: false, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (version != Volatile.Read(ref _cacheOperationVersion))
                {
                    AppDiagnostics.LogInfo("background MemoryPack cache discarded; version changed", "index startup");
                    return;
                }

                AppDiagnostics.LogInfo(
                    $"background MemoryPack cache materialize done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; items={items.Count:N0}",
                    "index startup");
                ReplaceCachedIndex(items);
                AppDiagnostics.LogInfo(
                    $"background MemoryPack cache load done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; indexed={IndexedCount:N0}",
                    "index startup");
                if (migrateAfterLoad)
                {
                    QueueCacheSave();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogException(ex, "background MemoryPack cache load");
            }
            finally
            {
                Thread.CurrentThread.Priority = originalPriority;
                if (version == Volatile.Read(ref _cacheOperationVersion))
                {
                    _currentRoot = string.Empty;
                    Volatile.Write(ref _isCompletingCacheLoad, false);
                }
            }
        });
    }

    private void QueueFullMemoryPackCacheLoadFromFile(string cachePath, bool migrateAfterLoad)
    {
        var version = Volatile.Read(ref _cacheOperationVersion);
        var cancellationToken = _indexCancellation.Token;
        Volatile.Write(ref _isCompletingCacheLoad, true);
        _currentRoot = "后台读取文件/文件夹缓存";
        _ = Task.Run(() =>
        {
            var originalPriority = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                AppDiagnostics.LogInfo($"background MemoryPack cache deserialize start; file={Path.GetFileName(cachePath)}", "index startup");
                PrepareCacheProgress(cachePath);
                using var stream = new FileStream(
                    cachePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CacheFileBufferSize,
                    FileOptions.SequentialScan);
                using var progressStream = new ProgressReadStream(stream, bytesRead => Interlocked.Add(ref _cacheBytesRead, bytesRead));
                var cache = MemoryPackSerializer
                    .DeserializeAsync<MemoryPackedFileIndexCache>(progressStream, cancellationToken: cancellationToken)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                cancellationToken.ThrowIfCancellationRequested();
                if (cache?.Version != MemoryPackCacheVersion
                    || cache.Directories is not { Length: > 0 }
                    || cache.Items is not { Length: > 0 })
                {
                    AppDiagnostics.LogInfo("background MemoryPack cache invalid; skip full cache load", "index startup");
                    return;
                }

                AppDiagnostics.LogInfo(
                    $"background MemoryPack cache deserialize done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; rawItems={cache.Items.Length:N0}; directories={cache.Directories.Length:N0}",
                    "index startup");
                var items = MaterializeMemoryPackCacheItems(cache, appsOnly: false, reportProgress: false, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (version != Volatile.Read(ref _cacheOperationVersion))
                {
                    AppDiagnostics.LogInfo("background MemoryPack cache discarded; version changed", "index startup");
                    return;
                }

                AppDiagnostics.LogInfo(
                    $"background MemoryPack cache materialize done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; items={items.Count:N0}",
                    "index startup");
                ReplaceCachedIndex(items);
                AppDiagnostics.LogInfo(
                    $"background MemoryPack cache load done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; indexed={IndexedCount:N0}",
                    "index startup");
                if (migrateAfterLoad)
                {
                    QueueCacheSave();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogException(ex, "background MemoryPack cache load from file");
            }
            finally
            {
                Thread.CurrentThread.Priority = originalPriority;
                if (version == Volatile.Read(ref _cacheOperationVersion))
                {
                    _currentRoot = string.Empty;
                    Volatile.Write(ref _isCompletingCacheLoad, false);
                }
            }
        });
    }

    private bool TryLoadCompactMemoryPackCache(CancellationToken cancellationToken)
    {
        try
        {
            PrepareCacheProgress(_compactMemoryPackCachePath);
            using var stream = new FileStream(
                _compactMemoryPackCachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CacheFileBufferSize,
                FileOptions.SequentialScan);
            using var progressStream = new ProgressReadStream(stream, bytesRead => Interlocked.Add(ref _cacheBytesRead, bytesRead));
            var cache = MemoryPackSerializer
                .DeserializeAsync<CompactMemoryPackedFileIndexCache>(progressStream, cancellationToken: cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (cache?.Version != CompactMemoryPackCacheVersion || cache.Items is not { Length: > 0 })
            {
                return false;
            }

            var items = new List<IndexedFileItem>(cache.Items.Length);
            foreach (var item in cache.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(item.Path))
                {
                    items.Add(CreateIndexedFileItem(item.Path, item.IsDirectory));
                    if ((items.Count & 0x3FF) == 0)
                    {
                        Interlocked.Exchange(ref _indexedCount, items.Count);
                    }
                }
            }

            ReplaceCachedIndex(items);
            return IndexedCount > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, $"load file index cache {Path.GetFileName(_compactMemoryPackCachePath)}");
            ClearIndex();
            return false;
        }
    }

    private bool TryLoadLegacyMemoryPackCache(CancellationToken cancellationToken)
    {
        try
        {
            PrepareCacheProgress(_legacyMemoryPackCachePath);
            using var stream = new FileStream(
                _legacyMemoryPackCachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CacheFileBufferSize,
                FileOptions.SequentialScan);
            using var progressStream = new ProgressReadStream(stream, bytesRead => Interlocked.Add(ref _cacheBytesRead, bytesRead));
            var cache = MemoryPackSerializer
                .DeserializeAsync<LegacyMemoryPackedFileIndexCache>(progressStream, cancellationToken: cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (cache?.Version != LegacyMemoryPackCacheVersion || cache.Items is not { Length: > 0 })
            {
                return false;
            }

            var items = new List<IndexedFileItem>(cache.Items.Length);
            foreach (var item in cache.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(item.Path))
                {
                    items.Add(item.ToIndexedFileItem());
                    if ((items.Count & 0x3FF) == 0)
                    {
                        Interlocked.Exchange(ref _indexedCount, items.Count);
                    }
                }
            }

            ReplaceCachedIndex(items);
            return IndexedCount > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, $"load file index cache {Path.GetFileName(_legacyMemoryPackCachePath)}");
            ClearIndex();
            return false;
        }
    }

    private bool TryLoadLegacyBinaryCache(CancellationToken cancellationToken)
    {
        try
        {
            PrepareCacheProgress(_binaryCachePath);
            using var stream = new FileStream(
                _binaryCachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CacheFileBufferSize,
                FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadString() != BinaryCacheMagic || reader.ReadInt32() != BinaryCacheVersion)
            {
                return false;
            }

            _ = reader.ReadInt64();
            var itemCount = reader.ReadInt32();
            if (itemCount <= 0)
            {
                return false;
            }

            var items = new IndexedFileItem[itemCount];
            for (var index = 0; index < itemCount; index++)
            {
                if ((index & 0x3FF) == 0)
                {
                    Interlocked.Exchange(ref _cacheBytesRead, stream.Position);
                    Interlocked.Exchange(ref _indexedCount, index);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                items[index] = new IndexedFileItem(
                    reader.ReadString(),
                    reader.ReadString(),
                    reader.ReadString(),
                    reader.ReadString(),
                    reader.ReadString(),
                    reader.ReadBoolean(),
                    reader.ReadBoolean(),
                    reader.ReadString(),
                    reader.ReadString(),
                    reader.ReadString(),
                    reader.ReadString());
            }

            Interlocked.Exchange(ref _cacheBytesRead, stream.Position);
            ReplaceCachedIndex(items);
            return IndexedCount > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, $"load file index cache {Path.GetFileName(_binaryCachePath)}");
            ClearIndex();
            return false;
        }
    }

    private bool TryLoadJsonCache(CancellationToken cancellationToken)
    {
        try
        {
            PrepareCacheProgress(_jsonCachePath);
            using var stream = new FileStream(
                _jsonCachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CacheFileBufferSize,
                FileOptions.SequentialScan);

            var buffer = ArrayPool<byte>.Shared.Rent(CacheFileBufferSize);
            var bytesInBuffer = 0;
            var isFinalBlock = false;
            var readerState = new JsonReaderState();
            var currentProperty = JsonCacheProperty.None;
            var version = 0;
            var hasVersion = false;
            var inItems = false;
            var inItem = false;
            var itemPath = string.Empty;
            var itemIsDirectory = false;
            var items = new List<IndexedFileItem>();

            try
            {
                while (!isFinalBlock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytesRead = stream.Read(buffer.AsSpan(bytesInBuffer));
                    isFinalBlock = bytesRead == 0;
                    Interlocked.Exchange(ref _cacheBytesRead, stream.Position);

                    var reader = new Utf8JsonReader(buffer.AsSpan(0, bytesInBuffer + bytesRead), isFinalBlock, readerState);
                    while (reader.Read())
                    {
                        switch (reader.TokenType)
                        {
                            case JsonTokenType.PropertyName:
                                currentProperty = ReadJsonCacheProperty(ref reader);
                                break;

                            case JsonTokenType.Number when currentProperty == JsonCacheProperty.Version:
                                hasVersion = reader.TryGetInt32(out version);
                                if (hasVersion && version != JsonCacheVersion)
                                {
                                    ClearIndex();
                                    return false;
                                }

                                break;

                            case JsonTokenType.StartArray when currentProperty == JsonCacheProperty.Items:
                                if (hasVersion && version != JsonCacheVersion)
                                {
                                    ClearIndex();
                                    return false;
                                }

                                ClearIndex();
                                items.Clear();
                                inItems = true;
                                break;

                            case JsonTokenType.EndArray when inItems:
                                inItems = false;
                                break;

                            case JsonTokenType.StartObject when inItems:
                                inItem = true;
                                itemPath = string.Empty;
                                itemIsDirectory = false;
                                break;

                            case JsonTokenType.EndObject when inItem:
                                if (!string.IsNullOrWhiteSpace(itemPath))
                                {
                                    items.Add(CreateIndexedFileItem(itemPath, itemIsDirectory));
                                }

                                inItem = false;
                                if ((items.Count & 0x3FF) == 0)
                                {
                                    Interlocked.Exchange(ref _indexedCount, items.Count);
                                    cancellationToken.ThrowIfCancellationRequested();
                                }

                                break;

                            case JsonTokenType.String when inItem && currentProperty == JsonCacheProperty.Path:
                                itemPath = reader.GetString() ?? string.Empty;
                                break;

                            case JsonTokenType.True or JsonTokenType.False when inItem && currentProperty == JsonCacheProperty.IsDirectory:
                                itemIsDirectory = reader.GetBoolean();
                                break;
                        }
                    }

                    readerState = reader.CurrentState;
                    var totalBytes = bytesInBuffer + bytesRead;
                    var consumedBytes = (int)reader.BytesConsumed;
                    bytesInBuffer = totalBytes - consumedBytes;
                    if (bytesInBuffer > 0)
                    {
                        Buffer.BlockCopy(buffer, consumedBytes, buffer, 0, bytesInBuffer);
                    }

                    if (bytesInBuffer == buffer.Length)
                    {
                        throw new JsonException("JSON token exceeds cache buffer size.");
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var loaded = hasVersion && version == JsonCacheVersion && items.Count > 0;
            if (!loaded)
            {
                ClearIndex();
                return false;
            }

            ReplaceCachedIndex(items);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, $"load file index cache {Path.GetFileName(_jsonCachePath)}");
            ClearIndex();
            return false;
        }
    }

    private static JsonCacheProperty ReadJsonCacheProperty(ref Utf8JsonReader reader)
    {
        if (reader.ValueTextEquals(JsonVersionProperty))
        {
            return JsonCacheProperty.Version;
        }

        if (reader.ValueTextEquals(JsonItemsProperty))
        {
            return JsonCacheProperty.Items;
        }

        if (reader.ValueTextEquals(JsonPathProperty))
        {
            return JsonCacheProperty.Path;
        }

        return reader.ValueTextEquals(JsonIsDirectoryProperty)
            ? JsonCacheProperty.IsDirectory
            : JsonCacheProperty.None;
    }

    public LocalFileIndexProgress GetProgress()
    {
        return new LocalFileIndexProgress(
            IsIndexing,
            IndexedCount,
            Volatile.Read(ref _rootsTotal),
            Volatile.Read(ref _rootsCompleted),
            Volatile.Read(ref _directoriesScanned),
            Volatile.Read(ref _directoriesQueued),
            Volatile.Read(ref _skippedCount),
            _currentRoot,
            _isLoadingCache,
            Volatile.Read(ref _isCompletingCacheLoad),
            Volatile.Read(ref _cacheBytesRead),
            Volatile.Read(ref _cacheBytesTotal));
    }

    public void StartIndexing()
    {
        if (_indexTask is { IsCompleted: false })
        {
            return;
        }

        lock (_indexStartLock)
        {
            if (_indexTask is { IsCompleted: false })
            {
                return;
            }

            StopIncrementalIndexing();
            _isIndexing = true;
            _indexTask = Task.Run(() => BuildIndex(_indexCancellation.Token));
        }
    }

    public void RebuildIndex()
    {
        lock (_indexStartLock)
        {
            AppDiagnostics.LogInfo("rebuild file index", "index startup");
            StopIncrementalIndexing();
            if (_indexTask is { IsCompleted: false })
            {
                _indexCancellation.Cancel();
            }

            _indexCancellation.Dispose();
            _indexCancellation = new CancellationTokenSource();
            ClearIndex();
            Interlocked.Exchange(ref _rootsTotal, 0);
            Interlocked.Exchange(ref _rootsCompleted, 0);
            Interlocked.Exchange(ref _directoriesScanned, 0);
            Interlocked.Exchange(ref _directoriesQueued, 0);
            Interlocked.Exchange(ref _skippedCount, 0);
            _currentRoot = string.Empty;
            _isIndexing = true;
            _indexTask = Task.Run(() => BuildIndex(_indexCancellation.Token));
        }
    }

    public void StartIncrementalIndexing()
    {
        if (_disposed)
        {
            return;
        }

        AppDiagnostics.LogInfo("start FileSystemWatcher; queue NTFS USN delta", "index startup");
        lock (_watcherLock)
        {
            StopIncrementalIndexingCore();
            foreach (var root in SearchRoots())
            {
                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName
                            | NotifyFilters.DirectoryName
                            | NotifyFilters.Attributes
                            | NotifyFilters.Size
                            | NotifyFilters.LastWrite
                            | NotifyFilters.CreationTime
                    };

                    watcher.Created += OnFileCreatedOrChanged;
                    watcher.Changed += OnFileCreatedOrChanged;
                    watcher.Deleted += OnFileDeleted;
                    watcher.Renamed += OnFileRenamed;
                    watcher.Error += OnWatcherError;
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    AppDiagnostics.LogException(ex, $"watch file index root {root}");
                }
            }
        }

        QueueNtfsJournalUpdate();
    }

    public void StopIncrementalIndexing()
    {
        lock (_watcherLock)
        {
            StopIncrementalIndexingCore();
        }
    }

    public Task<int> ClearCacheDataAsync()
    {
        return Task.Run(ClearCacheData);
    }

    public int ClearCacheData()
    {
        AppDiagnostics.LogInfo("clear navigation cache data start", "settings");
        lock (_indexStartLock)
        {
            Interlocked.Increment(ref _cacheOperationVersion);
            Interlocked.Exchange(ref _cacheSaveQueued, 0);
            StopIncrementalIndexing();
            if (_indexTask is { IsCompleted: false })
            {
                _indexCancellation.Cancel();
            }

            _indexCancellation.Dispose();
            _indexCancellation = new CancellationTokenSource();
            _isIndexing = false;
            _isLoadingCache = false;
            Volatile.Write(ref _isCompletingCacheLoad, false);
            _currentRoot = string.Empty;
            Interlocked.Exchange(ref _rootsTotal, 0);
            Interlocked.Exchange(ref _rootsCompleted, 0);
            Interlocked.Exchange(ref _directoriesScanned, 0);
            Interlocked.Exchange(ref _directoriesQueued, 0);
            Interlocked.Exchange(ref _skippedCount, 0);
            Interlocked.Exchange(ref _cacheBytesRead, 0);
            Interlocked.Exchange(ref _cacheBytesTotal, 0);
            ClearIndex();
        }

        int deleted;
        lock (_cacheSaveLock)
        {
            deleted = DeleteCacheFiles();
        }

        AppDiagnostics.LogInfo($"clear navigation cache data done; deletedFiles={deleted:N0}", "settings");
        return deleted;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopIncrementalIndexing();
        _indexCancellation.Cancel();
        _indexCancellation.Dispose();
    }

    public async Task<LocalFileSearchResponse> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var trimmedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return new LocalFileSearchResponse([], IsIndexing, IndexedCount);
        }

        var expression = EverythingSearchParser.Parse(trimmedQuery);
        await _searchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return SearchIndex(expression, trimmedQuery, maxResults, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);

            return new LocalFileSearchResponse(results, IsIndexing, IndexedCount);
        }
        finally
        {
            _searchGate.Release();
        }
    }

    private void StopIncrementalIndexingCore()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnFileCreatedOrChanged;
                watcher.Changed -= OnFileCreatedOrChanged;
                watcher.Deleted -= OnFileDeleted;
                watcher.Renamed -= OnFileRenamed;
                watcher.Error -= OnWatcherError;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogException(ex, "dispose file index watcher");
            }
        }

        _watchers = [];
    }

    private void OnFileCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        QueuePathRefresh(e.FullPath, e.ChangeType == WatcherChangeTypes.Created);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        QueuePathRemoval(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        QueuePathRemoval(e.OldFullPath);
        QueuePathRefresh(e.FullPath, true);
    }

    private static void OnWatcherError(object sender, ErrorEventArgs e)
    {
        AppDiagnostics.LogException(e.GetException(), "file index watcher");
    }

    private void QueuePathRefresh(string path, bool scanChildren)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _ = Task.Run(() => RefreshPath(path, scanChildren));
    }

    private void QueuePathRemoval(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            RemoveFromIndex(path, true);
            QueueCacheSave();
        });
    }

    private void RefreshPath(string path, bool scanChildren)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            if (isDirectory && ShouldSkipDirectory(path, attributes))
            {
                RemoveFromIndex(path, true);
                QueueCacheSave();
                return;
            }

            AddOrReplaceIndexedPath(path, isDirectory);
            if (isDirectory && scanChildren && ShouldDescendDirectory(attributes))
            {
                IndexRoot(path, CancellationToken.None);
            }

            QueueCacheSave();
        }
        catch
        {
            RemoveFromIndex(path, true);
            QueueCacheSave();
        }
    }

    private LauncherSearchResult[] SearchIndex(ISearchNode expression, string query, int maxResults, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var capacity = Math.Max(maxResults * 10, maxResults);
        var fastCandidates = TryGetFastCandidates(query, maxResults);
        var usedFastCandidates = fastCandidates is not null;
        var items = fastCandidates ?? GetSearchItems();
        var candidates = new List<ScoredFileItem>(capacity);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lowestScore = 0;
        var scanned = 0;

        foreach (var item in items)
        {
            if ((++scanned & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!_index.TryGetValue(item.Path, out var activeItem) || !seenPaths.Add(activeItem.Path))
            {
                continue;
            }

            var score = expression.Score(activeItem);
            if (score <= 0 || (candidates.Count >= capacity && score < lowestScore))
            {
                continue;
            }

            candidates.Add(new ScoredFileItem(activeItem, score));
            if (candidates.Count < capacity)
            {
                if (lowestScore == 0 || score < lowestScore)
                {
                    lowestScore = score;
                }

                continue;
            }

            candidates.Sort(CompareScoredItems);
            if (candidates.Count > maxResults)
            {
                candidates.RemoveRange(maxResults, candidates.Count - maxResults);
            }

            lowestScore = candidates.Count == 0 ? 0 : candidates[^1].Score;
        }

        cancellationToken.ThrowIfCancellationRequested();
        candidates.Sort(CompareScoredItems);
        var results = candidates
            .Take(maxResults)
            .Select(result => new LauncherSearchResult
            {
                Name = result.Item.Name,
                Path = result.Item.Path,
                Kind = result.Item.Kind,
                SearchScore = result.Score,
                SearchSortName = result.Item.SearchNameWithoutExtension
            })
            .ToArray();
        var elapsedMs = stopwatch.ElapsedMilliseconds;
        if (elapsedMs >= 300)
        {
            AppDiagnostics.LogInfo(
                $"search slow; elapsedMs={elapsedMs:N0}; scanned={scanned:N0}; returned={results.Length:N0}; mode={(usedFastCandidates ? "fast" : "full")}; queryLength={query.Length:N0}; cachedRootKeys={_cachedPathRootIndex.Count:N0}; cachedStartKeys={_cachedNameStartCharacterIndex.Count:N0}; cachedStartBigramKeys={_cachedNameStartBigramIndex.Count:N0}; cachedCharKeys={_cachedNameCharacterIndex.Count:N0}; cachedBigramKeys={_cachedNameBigramIndex.Count:N0}; cachedGramKeys={_cachedNameTrigramIndex.Count:N0}; liveRootKeys={_pathRootIndex.Count:N0}; liveStartKeys={_nameStartCharacterIndex.Count:N0}; liveStartBigramKeys={_nameStartBigramIndex.Count:N0}; liveCharKeys={_nameCharacterIndex.Count:N0}; liveBigramKeys={_nameBigramIndex.Count:N0}; liveGramKeys={_nameTrigramIndex.Count:N0}",
                "navigation search");
        }

        return results;
    }

    private IEnumerable<IndexedFileItem> GetSearchItems()
    {
        var snapshot = _itemsSnapshot;
        return snapshot.Count > 0 ? snapshot : _index.Values;
    }

    private static int CompareScoredItems(ScoredFileItem left, ScoredFileItem right)
    {
        var compare = right.Score.CompareTo(left.Score);
        if (compare != 0) return compare;

        compare = KindRank(left.Item).CompareTo(KindRank(right.Item));
        if (compare != 0) return compare;

        compare = StringComparer.CurrentCultureIgnoreCase.Compare(left.Item.SearchNameWithoutExtension, right.Item.SearchNameWithoutExtension);
        if (compare != 0) return compare;

        compare = StringComparer.CurrentCultureIgnoreCase.Compare(left.Item.Name, right.Item.Name);
        if (compare != 0) return compare;

        return StringComparer.OrdinalIgnoreCase.Compare(left.Item.Path, right.Item.Path);
    }

    private void BuildIndex(CancellationToken cancellationToken)
    {
        var roots = SearchRoots().ToArray();
        Interlocked.Exchange(ref _rootsTotal, roots.Length);
        Interlocked.Exchange(ref _rootsCompleted, 0);
        Interlocked.Exchange(ref _directoriesScanned, 0);
        Interlocked.Exchange(ref _directoriesQueued, 0);
        Interlocked.Exchange(ref _skippedCount, 0);

        try
        {
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _currentRoot = root;
                AppDiagnostics.LogInfo($"root={root}; try NTFS MFT enumeration", "index startup");
                if (!TryIndexNtfsRoot(root, cancellationToken))
                {
                    AppDiagnostics.LogInfo($"root={root}; fallback to directory scan", "index startup");
                    IndexRoot(root, cancellationToken);
                }
                Interlocked.Increment(ref _rootsCompleted);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "build file index");
        }
        finally
        {
            _currentRoot = string.Empty;
            _isIndexing = false;
            if (!cancellationToken.IsCancellationRequested)
            {
                SaveCache();
                StartIncrementalIndexing();
            }
        }
    }

    private void SaveCache()
    {
        lock (_cacheSaveLock)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                AppDiagnostics.LogInfo("save MemoryPack cache start", "index startup");
                Directory.CreateDirectory(Path.GetDirectoryName(_memoryPackCachePath)!);
                var tempPath = $"{_memoryPackCachePath}.tmp";
                var appTempPath = $"{_appMemoryPackCachePath}.tmp";
                TryDeleteFile(tempPath);
                TryDeleteFile(appTempPath);
                var cache = CreateMemoryPackCache();
                var appCache = CreateAppMemoryPackCache();

                using (var stream = new FileStream(
                           tempPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           CacheFileBufferSize,
                           FileOptions.SequentialScan))
                {
                    MemoryPackSerializer
                        .SerializeAsync(stream, cache)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                }

                using (var stream = new FileStream(
                           appTempPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           CacheFileBufferSize,
                           FileOptions.SequentialScan))
                {
                    MemoryPackSerializer
                        .SerializeAsync(stream, appCache)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                }

                if (File.Exists(_memoryPackCachePath))
                {
                    File.Delete(_memoryPackCachePath);
                }

                if (File.Exists(_appMemoryPackCachePath))
                {
                    File.Delete(_appMemoryPackCachePath);
                }

                File.Move(tempPath, _memoryPackCachePath);
                File.Move(appTempPath, _appMemoryPackCachePath);
                TryDeleteFile(_fastMemoryPackCachePath);
                TryDeleteFile(_compactMemoryPackCachePath);
                TryDeleteFile(_legacyMemoryPackCachePath);
                TryDeleteFile(_binaryCachePath);
                TryDeleteFile(_jsonCachePath);
                SaveNtfsJournalCache();
                AppDiagnostics.LogInfo($"save MemoryPack cache done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}", "index startup");
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogException(ex, "save file index cache");
            }
        }
    }

    private void QueueCacheSave()
    {
        var version = Volatile.Read(ref _cacheOperationVersion);
        if (Interlocked.Exchange(ref _cacheSaveQueued, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Interlocked.Exchange(ref _cacheSaveQueued, 0);
            if (_disposed || version != Volatile.Read(ref _cacheOperationVersion))
            {
                return;
            }

            if (Volatile.Read(ref _isCompletingCacheLoad) || _isIndexing)
            {
                QueueCacheSave();
                return;
            }

            SaveCache();
        });
    }

    private MemoryPackedFileIndexCache CreateMemoryPackCache()
    {
        var directoryIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var directories = new List<string>();
        var items = new List<MemoryPackedFileIndexItem>();

        foreach (var item in GetSearchItems())
        {
            var name = DisplayName(item.Path);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(item.Path) ?? string.Empty;
            if (!directoryIds.TryGetValue(directory, out var directoryIndex))
            {
                directoryIndex = directories.Count;
                directories.Add(directory);
                directoryIds.Add(directory, directoryIndex);
            }

            items.Add(new MemoryPackedFileIndexItem
            {
                DirectoryIndex = directoryIndex,
                Name = name,
                IsDirectory = item.IsDirectory
            });
        }

        return new MemoryPackedFileIndexCache
        {
            Version = MemoryPackCacheVersion,
            UpdatedAtUnixMilliseconds = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            Directories = directories.ToArray(),
            Items = items.ToArray()
        };
    }

    private AppMemoryPackedFileIndexCache CreateAppMemoryPackCache()
    {
        var items = new List<AppMemoryPackedFileIndexItem>();
        foreach (var item in GetSearchItems())
        {
            if (!item.IsApp || string.IsNullOrWhiteSpace(item.Path))
            {
                continue;
            }

            items.Add(new AppMemoryPackedFileIndexItem
            {
                Path = item.Path,
                IsDirectory = item.IsDirectory
            });
        }

        return new AppMemoryPackedFileIndexCache
        {
            Version = AppMemoryPackCacheVersion,
            UpdatedAtUnixMilliseconds = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            Items = items.ToArray()
        };
    }

    private FastMemoryPackedFileIndexCache CreateFastMemoryPackCache()
    {
        var items = new List<FastMemoryPackedFileIndexItem>();
        foreach (var item in GetSearchItems())
        {
            if (string.IsNullOrWhiteSpace(item.Path))
            {
                continue;
            }

            items.Add(new FastMemoryPackedFileIndexItem
            {
                Path = item.Path,
                IsDirectory = item.IsDirectory
            });
        }

        return new FastMemoryPackedFileIndexCache
        {
            Version = FastMemoryPackCacheVersion,
            UpdatedAtUnixMilliseconds = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            Items = items.ToArray()
        };
    }

    private void IndexRoot(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        Interlocked.Exchange(ref _directoriesQueued, pending.Count);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            Interlocked.Exchange(ref _directoriesQueued, pending.Count);
            Interlocked.Increment(ref _directoriesScanned);

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current, "*", IndexEnumerationOptions).ToArray();
            }
            catch
            {
                Interlocked.Increment(ref _skippedCount);
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch
                {
                    Interlocked.Increment(ref _skippedCount);
                    continue;
                }

                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                if (isDirectory && ShouldSkipDirectory(entry, attributes))
                {
                    Interlocked.Increment(ref _skippedCount);
                    continue;
                }

                AddToIndex(entry, isDirectory);
                if (isDirectory && ShouldDescendDirectory(attributes))
                {
                    pending.Push(entry);
                    Interlocked.Exchange(ref _directoriesQueued, pending.Count);
                }
                else if (isDirectory)
                {
                    Interlocked.Increment(ref _skippedCount);
                }
            }
        }
    }

    private void AddToIndex(string path, bool isDirectory)
    {
        AddCachedItem(CreateIndexedFileItem(path, isDirectory));
    }

    private static IndexedFileItem CreateIndexedFileItem(string path, bool isDirectory)
    {
        var kind = ItemKind(path, isDirectory);
        var fileName = DisplayName(path);
        var name = kind == AppKind ? Path.GetFileNameWithoutExtension(path) : fileName;
        var nameWithoutExtension = isDirectory ? name : Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(nameWithoutExtension))
        {
            nameWithoutExtension = name;
        }

        return new IndexedFileItem(
            name,
            fileName,
            path,
            NormalizePath(path),
            kind,
            isDirectory,
            kind == AppKind,
            Path.GetExtension(path).ToLowerInvariant(),
            name.ToLowerInvariant(),
            fileName.ToLowerInvariant(),
            nameWithoutExtension.ToLowerInvariant());
    }

    private void AddCachedItem(IndexedFileItem item)
    {
        if (_index.TryAdd(item.Path, item))
        {
            AddToPathRootIndex(item);
            AddToNameAccelerationIndexes(item);
            Interlocked.Increment(ref _indexedCount);
        }
    }

    private void AddOrReplaceIndexedPath(string path, bool isDirectory)
    {
        RemoveFromIndex(path, false);
        AddCachedItem(CreateIndexedFileItem(path, isDirectory));
    }

    private void RemoveFromIndex(string path, bool includeChildren)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var childPrefix = $"{trimmed}{Path.DirectorySeparatorChar}";

        foreach (var key in _index.Keys)
        {
            if (!key.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                && (!includeChildren || !key.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (_index.TryRemove(key, out _))
            {
                Interlocked.Decrement(ref _indexedCount);
            }
        }
    }

    private void ReplaceCachedIndex(IReadOnlyCollection<IndexedFileItem> items)
    {
        var stopwatch = Stopwatch.StartNew();
        AppDiagnostics.LogInfo($"build path lookup start; items={items.Count:N0}", "index startup");
        if (items.Count == 0)
        {
            ClearIndex();
            return;
        }

        Interlocked.Increment(ref _nameIndexBuildVersion);
        var activeItems = items as IReadOnlyList<IndexedFileItem> ?? items.ToArray();
        var index = new ConcurrentDictionary<string, IndexedFileItem>(StringComparer.OrdinalIgnoreCase);
        var pathRootIndex = new Dictionary<string, List<IndexedFileItem>>(StringComparer.OrdinalIgnoreCase);
        var nameStartCharacterIndex = new Dictionary<char, List<IndexedFileItem>>();
        var nameStartBigramIndex = new Dictionary<NameBigram, List<IndexedFileItem>>();
        var indexed = 0;
        foreach (var item in items)
        {
            if (!index.TryAdd(item.Path, item))
            {
                continue;
            }

            AddToPathRootLookupBuilders(item, pathRootIndex);
            AddToNameStartLookupBuilders(item, nameStartCharacterIndex, nameStartBigramIndex);
            indexed++;
        }

        _index = index;
        _pathRootIndex = new ConcurrentDictionary<string, ConcurrentBag<IndexedFileItem>>(StringComparer.OrdinalIgnoreCase);
        _nameStartCharacterIndex = new ConcurrentDictionary<char, ConcurrentBag<IndexedFileItem>>();
        _nameStartBigramIndex = new ConcurrentDictionary<NameBigram, ConcurrentBag<IndexedFileItem>>();
        _nameCharacterIndex = new ConcurrentDictionary<char, ConcurrentBag<IndexedFileItem>>();
        _nameBigramIndex = new ConcurrentDictionary<NameBigram, ConcurrentBag<IndexedFileItem>>();
        _nameTrigramIndex = new ConcurrentDictionary<NameGram, ConcurrentBag<IndexedFileItem>>();
        _cachedPathRootIndex = pathRootIndex.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
        _cachedNameStartCharacterIndex = nameStartCharacterIndex.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray());
        _cachedNameStartBigramIndex = nameStartBigramIndex.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray());
        _itemsSnapshot = activeItems;
        Interlocked.Exchange(ref _indexedCount, indexed);
        AppDiagnostics.LogInfo(
            $"build path lookup done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; indexed={IndexedCount:N0}; startCharKeys={_cachedNameStartCharacterIndex.Count:N0}; startBigramKeys={_cachedNameStartBigramIndex.Count:N0}",
            "index startup");
        QueueCachedNameIndexBuild(activeItems);
    }

    private void QueueCachedNameIndexBuild(IReadOnlyList<IndexedFileItem> items)
    {
        var version = Volatile.Read(ref _nameIndexBuildVersion);
        AppDiagnostics.LogInfo($"build name acceleration index queued; items={items.Count:N0}", "index startup");
        _ = Task.Run(() =>
        {
            var originalPriority = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var bigramIndex = new Dictionary<NameBigram, List<IndexedFileItem>>();
                foreach (var item in items)
                {
                    AddToNameBigramLookupBuilders(item, bigramIndex);
                }

                if (version != Volatile.Read(ref _nameIndexBuildVersion))
                {
                    AppDiagnostics.LogInfo("build bigram acceleration index discarded; version changed", "index startup");
                    return;
                }

                _cachedNameBigramIndex = bigramIndex.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray());
                AppDiagnostics.LogInfo(
                    $"build bigram acceleration index done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; startKeys={_cachedNameStartCharacterIndex.Count:N0}; startBigramKeys={_cachedNameStartBigramIndex.Count:N0}; bigramKeys={_cachedNameBigramIndex.Count:N0}",
                    "index startup");
                bigramIndex.Clear();

                var characterIndex = new Dictionary<char, List<IndexedFileItem>>();
                foreach (var item in items)
                {
                    AddToNameCharacterLookupBuilders(item, characterIndex);
                }

                if (version != Volatile.Read(ref _nameIndexBuildVersion))
                {
                    AppDiagnostics.LogInfo("build character acceleration index discarded; version changed", "index startup");
                    return;
                }

                _cachedNameCharacterIndex = characterIndex.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray());
                AppDiagnostics.LogInfo(
                    $"build character acceleration index done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; nameKeys={_cachedNameCharacterIndex.Count:N0}",
                    "index startup");
                characterIndex.Clear();

                var trigramIndex = new Dictionary<NameGram, List<IndexedFileItem>>();
                foreach (var item in items)
                {
                    AddToNameTrigramLookupBuilders(item, trigramIndex);
                }

                if (version != Volatile.Read(ref _nameIndexBuildVersion))
                {
                    AppDiagnostics.LogInfo("build trigram acceleration index discarded; version changed", "index startup");
                    return;
                }

                _cachedNameTrigramIndex = trigramIndex.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray());
                AppDiagnostics.LogInfo(
                    $"build trigram acceleration index done; elapsedMs={stopwatch.ElapsedMilliseconds:N0}; gramKeys={_cachedNameTrigramIndex.Count:N0}",
                    "index startup");
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogException(ex, "build name acceleration index");
            }
            finally
            {
                Thread.CurrentThread.Priority = originalPriority;
            }
        });
    }

    private void ClearIndex()
    {
        Interlocked.Increment(ref _nameIndexBuildVersion);
        _index = new ConcurrentDictionary<string, IndexedFileItem>(StringComparer.OrdinalIgnoreCase);
        _pathRootIndex = new ConcurrentDictionary<string, ConcurrentBag<IndexedFileItem>>(StringComparer.OrdinalIgnoreCase);
        _nameStartCharacterIndex = new ConcurrentDictionary<char, ConcurrentBag<IndexedFileItem>>();
        _nameStartBigramIndex = new ConcurrentDictionary<NameBigram, ConcurrentBag<IndexedFileItem>>();
        _nameCharacterIndex = new ConcurrentDictionary<char, ConcurrentBag<IndexedFileItem>>();
        _nameBigramIndex = new ConcurrentDictionary<NameBigram, ConcurrentBag<IndexedFileItem>>();
        _nameTrigramIndex = new ConcurrentDictionary<NameGram, ConcurrentBag<IndexedFileItem>>();
        _ntfsVolumeIndexes.Clear();
        _itemsSnapshot = [];
        _cachedPathRootIndex = new Dictionary<string, IndexedFileItem[]>(StringComparer.OrdinalIgnoreCase);
        _cachedNameStartCharacterIndex = new Dictionary<char, IndexedFileItem[]>();
        _cachedNameStartBigramIndex = new Dictionary<NameBigram, IndexedFileItem[]>();
        _cachedNameCharacterIndex = new Dictionary<char, IndexedFileItem[]>();
        _cachedNameBigramIndex = new Dictionary<NameBigram, IndexedFileItem[]>();
        _cachedNameTrigramIndex = new Dictionary<NameGram, IndexedFileItem[]>();
        Interlocked.Exchange(ref _indexedCount, 0);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private int DeleteCacheFiles()
    {
        var deleted = 0;
        foreach (var path in CacheDataPaths())
        {
            if (TryDeleteCacheFile(path))
            {
                deleted++;
            }

            if (TryDeleteCacheFile($"{path}.tmp"))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private IEnumerable<string> CacheDataPaths()
    {
        yield return _appMemoryPackCachePath;
        yield return _memoryPackCachePath;
        yield return _fastMemoryPackCachePath;
        yield return _compactMemoryPackCachePath;
        yield return _legacyMemoryPackCachePath;
        yield return _binaryCachePath;
        yield return _jsonCachePath;
        yield return _ntfsJournalCachePath;
    }

    private static bool TryDeleteCacheFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, $"delete cache file {Path.GetFileName(path)}");
            return false;
        }
    }

    private IEnumerable<IndexedFileItem>? TryGetFastCandidates(string query, int maxResults)
    {
        return TryGetFastPathCandidates(query, maxResults) ?? TryGetFastNameCandidates(query, maxResults);
    }

    private IEnumerable<IndexedFileItem>? TryGetFastPathCandidates(string query, int maxResults)
    {
        if (!TryGetPathRootSearch(query, out var rootKey, out var isRootOnly))
        {
            return null;
        }

        _cachedPathRootIndex.TryGetValue(rootKey, out var cachedCandidates);
        _pathRootIndex.TryGetValue(rootKey, out var liveCandidates);
        IEnumerable<IndexedFileItem>? candidates = (cachedCandidates, liveCandidates) switch
        {
            (null, null) => Array.Empty<IndexedFileItem>(),
            (not null, null) => cachedCandidates,
            (null, not null) => liveCandidates,
            _ => cachedCandidates.Concat(liveCandidates)
        };

        return isRootOnly
            ? candidates.Take(Math.Max(maxResults * 10, maxResults))
            : candidates;
    }

    private IEnumerable<IndexedFileItem>? TryGetFastNameCandidates(string query, int maxResults)
    {
        if (!TryGetSimpleNameTerms(query, out var terms) && !TryGetLooseNameTerms(query, out terms))
        {
            return null;
        }

        return TryGetFastNameCandidates(terms, maxResults);
    }

    private IEnumerable<IndexedFileItem>? TryGetFastNameCandidates(string[] terms, int maxResults)
    {
        var cachedStartCharacterIndex = _cachedNameStartCharacterIndex;
        var cachedStartBigramIndex = _cachedNameStartBigramIndex;
        var cachedCharacterIndex = _cachedNameCharacterIndex;
        var cachedBigramIndex = _cachedNameBigramIndex;
        var cachedTrigramIndex = _cachedNameTrigramIndex;
        IEnumerable<IndexedFileItem>? cachedCandidates = null;
        if (cachedStartCharacterIndex.Count > 0 || cachedStartBigramIndex.Count > 0 || cachedCharacterIndex.Count > 0 || cachedBigramIndex.Count > 0 || cachedTrigramIndex.Count > 0)
        {
            cachedCandidates = TryGetFastNameCandidates(
                terms,
                cachedStartCharacterIndex,
                cachedStartBigramIndex,
                cachedCharacterIndex,
                cachedBigramIndex,
                cachedTrigramIndex,
                maxResults);
        }

        IEnumerable<IndexedFileItem>? liveCandidates = null;
        if (!_nameStartCharacterIndex.IsEmpty || !_nameStartBigramIndex.IsEmpty || !_nameCharacterIndex.IsEmpty || !_nameBigramIndex.IsEmpty || !_nameTrigramIndex.IsEmpty)
        {
            liveCandidates = TryGetFastNameCandidates(
                terms,
                _nameStartCharacterIndex,
                _nameStartBigramIndex,
                _nameCharacterIndex,
                _nameBigramIndex,
                _nameTrigramIndex,
                maxResults);
        }

        return (cachedCandidates, liveCandidates) switch
        {
            (null, null) => null,
            (not null, null) => cachedCandidates,
            (null, not null) => liveCandidates,
            _ => cachedCandidates.Concat(liveCandidates)
        };
    }

    private static IEnumerable<IndexedFileItem>? TryGetFastNameCandidates<TStartCharacterCandidates, TCharacterCandidates, TBigramCandidates, TTrigramCandidates>(
        string[] terms,
        IReadOnlyDictionary<char, TStartCharacterCandidates> startCharacterIndex,
        IReadOnlyDictionary<NameBigram, TBigramCandidates> startBigramIndex,
        IReadOnlyDictionary<char, TCharacterCandidates> characterIndex,
        IReadOnlyDictionary<NameBigram, TBigramCandidates> bigramIndex,
        IReadOnlyDictionary<NameGram, TTrigramCandidates> trigramIndex,
        int maxResults)
        where TStartCharacterCandidates : IReadOnlyCollection<IndexedFileItem>
        where TCharacterCandidates : IReadOnlyCollection<IndexedFileItem>
        where TBigramCandidates : IReadOnlyCollection<IndexedFileItem>
        where TTrigramCandidates : IReadOnlyCollection<IndexedFileItem>
    {
        var seenBigrams = new HashSet<NameBigram>();
        var seenTrigrams = new HashSet<NameGram>();
        var seenCharacters = new HashSet<char>();
        var useBigramIndex = bigramIndex.Count > 0;
        var useTrigramIndex = trigramIndex.Count > 0;
        IReadOnlyCollection<IndexedFileItem>? bestCandidates = null;
        var bestCount = int.MaxValue;

        foreach (var term in terms)
        {
            var normalized = term.ToLowerInvariant();
            if (terms.Length == 1 && normalized.Length == 1 && startCharacterIndex.Count > 0)
            {
                if (!startCharacterIndex.TryGetValue(normalized[0], out var startCandidates))
                {
                    return Array.Empty<IndexedFileItem>();
                }

                return LimitCandidates(startCandidates, maxResults, SingleCharacterCandidateMultiplier);
            }

            if (terms.Length == 1 && normalized.Length >= 2 && startBigramIndex.Count > 0)
            {
                var startGram = new NameBigram(normalized[0], normalized[1]);
                if (!startBigramIndex.TryGetValue(startGram, out var startBigramCandidates))
                {
                    if (bigramIndex.Count == 0 && characterIndex.Count == 0)
                    {
                        return Array.Empty<IndexedFileItem>();
                    }
                }
                else if (bigramIndex.Count == 0
                         || (normalized.Length == 2 && startBigramCandidates.Count >= maxResults)
                         || (normalized.Length >= 3 && trigramIndex.Count == 0))
                {
                    return LimitCandidates(startBigramCandidates, maxResults, StartPrefixCandidateMultiplier);
                }
            }

            if (characterIndex.Count > 0)
            {
                foreach (var ch in normalized)
                {
                    if (!seenCharacters.Add(ch))
                    {
                        continue;
                    }

                    if (!characterIndex.TryGetValue(ch, out var characterCandidates))
                    {
                        return Array.Empty<IndexedFileItem>();
                    }

                    UpdateBestCandidates(characterCandidates, ref bestCandidates, ref bestCount);
                }
            }

            if (useBigramIndex && normalized.Length >= 2)
            {
                for (var index = 0; index <= normalized.Length - 2; index++)
                {
                    var gram = new NameBigram(normalized[index], normalized[index + 1]);
                    if (!seenBigrams.Add(gram))
                    {
                        continue;
                    }

                    if (!bigramIndex.TryGetValue(gram, out var candidates))
                    {
                        return Array.Empty<IndexedFileItem>();
                    }

                    UpdateBestCandidates(candidates, ref bestCandidates, ref bestCount);
                }
            }

            if (!useTrigramIndex || normalized.Length < 3)
            {
                continue;
            }

            for (var index = 0; index <= normalized.Length - 3; index++)
            {
                var gram = new NameGram(normalized[index], normalized[index + 1], normalized[index + 2]);
                if (!seenTrigrams.Add(gram))
                {
                    continue;
                }

                if (!trigramIndex.TryGetValue(gram, out var candidates))
                {
                    return Array.Empty<IndexedFileItem>();
                }

                UpdateBestCandidates(candidates, ref bestCandidates, ref bestCount);
            }
        }

        return bestCandidates;
    }

    private static void UpdateBestCandidates(
        IReadOnlyCollection<IndexedFileItem> candidates,
        ref IReadOnlyCollection<IndexedFileItem>? bestCandidates,
        ref int bestCount)
    {
        var count = candidates.Count;
        if (count < bestCount)
        {
            bestCandidates = candidates;
            bestCount = count;
        }
    }

    private static IEnumerable<IndexedFileItem> LimitCandidates(
        IReadOnlyCollection<IndexedFileItem> candidates,
        int maxResults,
        int multiplier)
    {
        var limit = Math.Max(maxResults * multiplier, maxResults);
        return candidates.Count > limit ? candidates.Take(limit) : candidates;
    }

    private static bool TryGetSimpleNameTerms(string query, out string[] terms)
    {
        terms = [];
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        foreach (var ch in query)
        {
            if (ch is '|' or '!' or '<' or '>' or ':' or '\\' or '/' or '*' or '?' or '"')
            {
                return false;
            }
        }

        terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length > 0;
    }

    private static bool TryGetLooseNameTerms(string query, out string[] terms)
    {
        terms = [];
        if (query.IndexOfAny(['|', '!', '<', '>', '\\', '/', '*', '?', '"']) >= 0)
        {
            return false;
        }

        var values = new List<string>();
        foreach (var rawTerm in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var term = StripSearchPrefixesForLooseName(rawTerm);
            if (string.IsNullOrWhiteSpace(term))
            {
                continue;
            }

            AddLooseNameTerms(term, values);
        }

        terms = values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(term => term.Length)
            .ToArray();
        return terms.Length > 0;
    }

    private static string StripSearchPrefixesForLooseName(string term)
    {
        var text = term;
        while (true)
        {
            var lower = text.ToLowerInvariant();
            if (lower.StartsWith("case:", StringComparison.Ordinal))
            {
                text = text[5..];
                continue;
            }

            if (lower.StartsWith("nocase:", StringComparison.Ordinal))
            {
                text = text[7..];
                continue;
            }

            if (lower.StartsWith("file:", StringComparison.Ordinal) && text.Length > 5)
            {
                text = text[5..];
                continue;
            }

            if (lower.StartsWith("folder:", StringComparison.Ordinal) && text.Length > 7)
            {
                text = text[7..];
                continue;
            }

            if (lower.StartsWith("folders:", StringComparison.Ordinal) && text.Length > 8)
            {
                text = text[8..];
                continue;
            }

            if (lower.StartsWith("path:", StringComparison.Ordinal)
                || lower.StartsWith("parent:", StringComparison.Ordinal)
                || lower.StartsWith("regex:", StringComparison.Ordinal)
                || lower.StartsWith("ext:", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            foreach (var pair in MacroExtensions)
            {
                if (lower == $"{pair.Key}:")
                {
                    return string.Empty;
                }
            }

            return text;
        }
    }

    private static void AddLooseNameTerms(string text, List<string> terms)
    {
        var start = -1;
        for (var index = 0; index <= text.Length; index++)
        {
            var isTermCharacter = index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_');
            if (isTermCharacter)
            {
                if (start < 0)
                {
                    start = index;
                }

                continue;
            }

            if (start >= 0 && index - start >= 2)
            {
                terms.Add(text[start..index]);
            }

            start = -1;
        }
    }

    private static bool TryGetPathRootSearch(string query, out string rootKey, out bool isRootOnly)
    {
        rootKey = string.Empty;
        isRootOnly = false;
        var text = query.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch is '|' or '!' or '<' or '>' or '"')
            {
                return false;
            }
        }

        if (!TryStripPathCandidatePrefixes(text, out var pathText) || !LooksLikePathTerm(pathText))
        {
            return false;
        }

        if (!TryGetPathRootKey(pathText, out rootKey))
        {
            return false;
        }

        isRootOnly = IsRootOnlyPathTerm(pathText);
        return true;
    }

    private static bool TryStripPathCandidatePrefixes(string text, out string pathText)
    {
        pathText = text;
        while (true)
        {
            var lower = pathText.ToLowerInvariant();
            if (lower.StartsWith("case:", StringComparison.Ordinal))
            {
                pathText = pathText[5..];
                continue;
            }

            if (lower.StartsWith("nocase:", StringComparison.Ordinal))
            {
                pathText = pathText[7..];
                continue;
            }

            if (lower.StartsWith("path:", StringComparison.Ordinal))
            {
                pathText = pathText[5..];
                continue;
            }

            if (lower.StartsWith("nopath:", StringComparison.Ordinal)
                || lower.StartsWith("regex:", StringComparison.Ordinal)
                || lower.StartsWith("noregex:", StringComparison.Ordinal))
            {
                return false;
            }

            return pathText.Length > 0;
        }
    }

    private static bool TryGetPathRootKey(string path, out string rootKey)
    {
        rootKey = string.Empty;
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            rootKey = $"{char.ToLowerInvariant(path[0])}:{Path.DirectorySeparatorChar}";
            return true;
        }

        try
        {
            var root = Path.GetPathRoot(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            rootKey = NormalizeRootKey(root);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeRootKey(string root)
    {
        var normalized = NormalizePath(root);
        if (normalized.Length == 2 && normalized[1] == ':')
        {
            return $"{normalized}{Path.DirectorySeparatorChar}";
        }

        return normalized.EndsWith(Path.DirectorySeparatorChar)
            ? normalized
            : $"{normalized}{Path.DirectorySeparatorChar}";
    }

    private static bool IsRootOnlyPathTerm(string path)
    {
        var normalized = NormalizePath(path.Trim());
        return (normalized.Length == 2 && IsDriveTerm(normalized))
            || (normalized.Length == 3 && IsDriveTerm(normalized) && normalized[2] == Path.DirectorySeparatorChar);
    }

    private static void AddToPathRootLookupBuilders(
        IndexedFileItem item,
        Dictionary<string, List<IndexedFileItem>> pathRootIndex)
    {
        if (TryGetPathRootKey(item.Path, out var rootKey))
        {
            AddToListIndex(pathRootIndex, rootKey, item);
        }
    }

    private static void AddToNameLookupBuilders(
        IndexedFileItem item,
        Dictionary<char, List<IndexedFileItem>> startCharacterIndex,
        Dictionary<NameBigram, List<IndexedFileItem>> startBigramIndex,
        Dictionary<char, List<IndexedFileItem>> characterIndex,
        Dictionary<NameBigram, List<IndexedFileItem>> bigramIndex,
        Dictionary<NameGram, List<IndexedFileItem>> trigramIndex)
    {
        AddToNameStartLookupBuilders(item, startCharacterIndex, startBigramIndex);
        AddToNameCharacterLookupBuilders(item, characterIndex);
        AddToNameBigramLookupBuilders(item, bigramIndex);
        AddToNameTrigramLookupBuilders(item, trigramIndex);
    }

    private static void AddToNameStartLookupBuilders(
        IndexedFileItem item,
        Dictionary<char, List<IndexedFileItem>> startCharacterIndex,
        Dictionary<NameBigram, List<IndexedFileItem>> startBigramIndex)
    {
        HashSet<char>? characters = null;
        HashSet<NameBigram>? bigrams = null;
        AddNameStartKeys(item, ref characters, ref bigrams);
        if (characters is not null)
        {
            foreach (var character in characters)
            {
                AddToListIndex(startCharacterIndex, character, item);
            }
        }

        if (bigrams is not null)
        {
            foreach (var bigram in bigrams)
            {
                AddToListIndex(startBigramIndex, bigram, item);
            }
        }
    }

    private static void AddToNameCharacterLookupBuilders(
        IndexedFileItem item,
        Dictionary<char, List<IndexedFileItem>> characterIndex)
    {
        HashSet<char>? characters = null;
        AddNameCharacterKeys(item, ref characters);
        if (characters is not null)
        {
            foreach (var character in characters)
            {
                AddToListIndex(characterIndex, character, item);
            }
        }
    }

    private static void AddToNameBigramLookupBuilders(
        IndexedFileItem item,
        Dictionary<NameBigram, List<IndexedFileItem>> bigramIndex)
    {
        HashSet<NameBigram>? keys = null;
        AddNameBigramKeys(item, ref keys);
        if (keys is null)
        {
            return;
        }

        foreach (var key in keys)
        {
            AddToListIndex(bigramIndex, key, item);
        }
    }

    private static void AddToNameTrigramLookupBuilders(
        IndexedFileItem item,
        Dictionary<NameGram, List<IndexedFileItem>> trigramIndex)
    {
        HashSet<NameGram>? keys = null;
        AddNameTrigramKeys(item, ref keys);
        if (keys is null)
        {
            return;
        }

        foreach (var key in keys)
        {
            AddToListIndex(trigramIndex, key, item);
        }
    }

    private static void AddToListIndex<TKey>(
        Dictionary<TKey, List<IndexedFileItem>> index,
        TKey key,
        IndexedFileItem item)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var items))
        {
            items = [];
            index.Add(key, items);
        }

        items.Add(item);
    }

    private void AddToNameAccelerationIndexes(IndexedFileItem item)
    {
        HashSet<char>? startCharacters = null;
        HashSet<NameBigram>? startBigrams = null;
        AddNameStartKeys(item, ref startCharacters, ref startBigrams);
        if (startCharacters is not null)
        {
            foreach (var character in startCharacters)
            {
                _nameStartCharacterIndex.GetOrAdd(character, static _ => new ConcurrentBag<IndexedFileItem>()).Add(item);
            }
        }

        if (startBigrams is not null)
        {
            foreach (var key in startBigrams)
            {
                _nameStartBigramIndex.GetOrAdd(key, static _ => new ConcurrentBag<IndexedFileItem>()).Add(item);
            }
        }

        HashSet<char>? characters = null;
        AddNameCharacterKeys(item, ref characters);
        if (characters is not null)
        {
            foreach (var character in characters)
            {
                _nameCharacterIndex.GetOrAdd(character, static _ => new ConcurrentBag<IndexedFileItem>()).Add(item);
            }
        }

        HashSet<NameBigram>? bigramKeys = null;
        AddNameBigramKeys(item, ref bigramKeys);
        if (bigramKeys is not null)
        {
            foreach (var key in bigramKeys)
            {
                _nameBigramIndex.GetOrAdd(key, static _ => new ConcurrentBag<IndexedFileItem>()).Add(item);
            }
        }

        HashSet<NameGram>? keys = null;
        AddNameTrigramKeys(item, ref keys);
        if (keys is null)
        {
            return;
        }

        foreach (var key in keys)
        {
            _nameTrigramIndex.GetOrAdd(key, static _ => new ConcurrentBag<IndexedFileItem>()).Add(item);
        }
    }

    private static void AddNameStartKeys(
        IndexedFileItem item,
        ref HashSet<char>? characters,
        ref HashSet<NameBigram>? bigrams)
    {
        AddNameStartKeys(item.SearchName, ref characters, ref bigrams);
        if (!item.SearchFileName.Equals(item.SearchName, StringComparison.Ordinal))
        {
            AddNameStartKeys(item.SearchFileName, ref characters, ref bigrams);
        }

        if (!item.SearchNameWithoutExtension.Equals(item.SearchName, StringComparison.Ordinal)
            && !item.SearchNameWithoutExtension.Equals(item.SearchFileName, StringComparison.Ordinal))
        {
            AddNameStartKeys(item.SearchNameWithoutExtension, ref characters, ref bigrams);
        }
    }

    private static void AddNameStartKeys(
        string text,
        ref HashSet<char>? characters,
        ref HashSet<NameBigram>? bigrams)
    {
        if (text.Length == 0)
        {
            return;
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (index > 0 && !IsWordSeparator(text[index - 1]))
            {
                continue;
            }

            characters ??= [];
            characters.Add(text[index]);
            if (index < text.Length - 1)
            {
                bigrams ??= [];
                bigrams.Add(new NameBigram(text[index], text[index + 1]));
            }
        }
    }

    private static void AddNameCharacterKeys(IndexedFileItem item, ref HashSet<char>? keys)
    {
        AddNameCharacterKeys(item.SearchName, ref keys);
        if (!item.SearchFileName.Equals(item.SearchName, StringComparison.Ordinal))
        {
            AddNameCharacterKeys(item.SearchFileName, ref keys);
        }

        if (!item.SearchNameWithoutExtension.Equals(item.SearchName, StringComparison.Ordinal)
            && !item.SearchNameWithoutExtension.Equals(item.SearchFileName, StringComparison.Ordinal))
        {
            AddNameCharacterKeys(item.SearchNameWithoutExtension, ref keys);
        }
    }

    private static void AddNameCharacterKeys(string text, ref HashSet<char>? keys)
    {
        if (text.Length == 0)
        {
            return;
        }

        keys ??= [];
        foreach (var ch in text)
        {
            keys.Add(ch);
        }
    }

    private static void AddNameBigramKeys(IndexedFileItem item, ref HashSet<NameBigram>? keys)
    {
        AddNameBigramKeys(item.SearchName, ref keys);
        if (!item.SearchFileName.Equals(item.SearchName, StringComparison.Ordinal))
        {
            AddNameBigramKeys(item.SearchFileName, ref keys);
        }

        if (!item.SearchNameWithoutExtension.Equals(item.SearchName, StringComparison.Ordinal)
            && !item.SearchNameWithoutExtension.Equals(item.SearchFileName, StringComparison.Ordinal))
        {
            AddNameBigramKeys(item.SearchNameWithoutExtension, ref keys);
        }
    }

    private static void AddNameBigramKeys(string text, ref HashSet<NameBigram>? keys)
    {
        if (text.Length < 2)
        {
            return;
        }

        keys ??= [];
        for (var index = 0; index <= text.Length - 2; index++)
        {
            keys.Add(new NameBigram(text[index], text[index + 1]));
        }
    }

    private static void AddNameTrigramKeys(IndexedFileItem item, ref HashSet<NameGram>? keys)
    {
        AddNameTrigramKeys(item.SearchName, ref keys);
        if (!item.SearchFileName.Equals(item.SearchName, StringComparison.Ordinal))
        {
            AddNameTrigramKeys(item.SearchFileName, ref keys);
        }

        if (!item.SearchNameWithoutExtension.Equals(item.SearchName, StringComparison.Ordinal)
            && !item.SearchNameWithoutExtension.Equals(item.SearchFileName, StringComparison.Ordinal))
        {
            AddNameTrigramKeys(item.SearchNameWithoutExtension, ref keys);
        }
    }

    private static void AddNameTrigramKeys(string text, ref HashSet<NameGram>? keys)
    {
        if (text.Length < 3)
        {
            return;
        }

        keys ??= [];
        for (var index = 0; index <= text.Length - 3; index++)
        {
            keys.Add(new NameGram(text[index], text[index + 1], text[index + 2]));
        }
    }

    private void AddToPathRootIndex(IndexedFileItem item)
    {
        if (TryGetPathRootKey(item.Path, out var rootKey))
        {
            _pathRootIndex.GetOrAdd(rootKey, static _ => new ConcurrentBag<IndexedFileItem>()).Add(item);
        }
    }

    private static IEnumerable<string> SearchRoots()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "get search roots");
            yield break;
        }

        foreach (var drive in drives)
        {
            string root;
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network or DriveType.Ram))
                {
                    continue;
                }

                root = drive.RootDirectory.FullName;
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogException(ex, $"read drive {drive.Name}");
                continue;
            }

            yield return root;
        }
    }

    private static bool ShouldSkipDirectory(string path, FileAttributes attributes)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return SkippedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool ShouldDescendDirectory(FileAttributes attributes)
    {
        return !attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static int KindRank(IndexedFileItem item)
    {
        return item.Kind switch
        {
            AppKind => 0,
            FileKind => 1,
            FolderKind => 2,
            _ => 3
        };
    }

    private static string ItemKind(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return FolderKind;
        }

        return IsLaunchablePath(path, isDirectory)
            ? AppKind
            : FileKind;
    }

    private static bool IsLaunchablePath(string path, bool isDirectory)
    {
        return IsLaunchableName(path, isDirectory);
    }

    private static bool IsLaunchableName(string name, bool isDirectory)
    {
        return !isDirectory && LaunchableExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    private static string DisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).ToLowerInvariant();
    }

    private static string CombineCachedPath(string directory, string name)
    {
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    private static int ScoreText(string text, string term)
    {
        if (string.Equals(text, term, StringComparison.Ordinal)) return 20000;
        if (text.StartsWith(term, StringComparison.Ordinal)) return 12000;

        var wordIndex = IndexOfWordStart(text, term);
        if (wordIndex >= 0) return 9000 - Math.Min(wordIndex, 100);

        var containsIndex = text.IndexOf(term, StringComparison.Ordinal);
        if (containsIndex >= 0) return 6500 - Math.Min(containsIndex, 100);

        return 0;
    }

    private static int IndexOfWordStart(string text, string term)
    {
        var index = text.IndexOf(term, StringComparison.Ordinal);
        while (index >= 0)
        {
            if (index == 0 || IsWordSeparator(text[index - 1]))
            {
                return index;
            }

            index = text.IndexOf(term, index + 1, StringComparison.Ordinal);
        }

        return -1;
    }

    private static bool IsWordSeparator(char ch)
    {
        return char.IsWhiteSpace(ch) || ch is '-' or '_' or '.' or '(' or ')' or '[' or ']' or '{' or '}';
    }

    private static Regex WildcardRegex(string pattern, bool ignoreCase)
    {
        var builder = new StringBuilder("^");
        foreach (var ch in pattern)
        {
            builder.Append(ch switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(ch.ToString())
            });
        }

        builder.Append('$');
        return new Regex(builder.ToString(), ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
    }

    private sealed class ProgressReadStream(Stream inner, Action<int> onRead) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
            inner.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = inner.Read(buffer, offset, count);
            Report(bytesRead);
            return bytesRead;
        }

        public override int Read(Span<byte> buffer)
        {
            var bytesRead = inner.Read(buffer);
            Report(bytesRead);
            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytesRead = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Report(bytesRead);
            return bytesRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var bytesRead = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            Report(bytesRead);
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return inner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Report(int bytesRead)
        {
            if (bytesRead > 0)
            {
                onRead(bytesRead);
            }
        }
    }

    [MemoryPackable]
    private sealed partial class AppMemoryPackedFileIndexCache
    {
        public int Version { get; set; }
        public long UpdatedAtUnixMilliseconds { get; set; }
        public AppMemoryPackedFileIndexItem[] Items { get; set; } = [];
    }

    [MemoryPackable]
    private sealed partial class AppMemoryPackedFileIndexItem
    {
        public string Path { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
    }

    [MemoryPackable]
    private sealed partial class MemoryPackedFileIndexCache
    {
        public int Version { get; set; }
        public long UpdatedAtUnixMilliseconds { get; set; }
        public string[] Directories { get; set; } = [];
        public MemoryPackedFileIndexItem[] Items { get; set; } = [];
    }

    [MemoryPackable]
    private sealed partial class MemoryPackedFileIndexItem
    {
        public int DirectoryIndex { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
    }

    [MemoryPackable]
    private sealed partial class FastMemoryPackedFileIndexCache
    {
        public int Version { get; set; }
        public long UpdatedAtUnixMilliseconds { get; set; }
        public FastMemoryPackedFileIndexItem[] Items { get; set; } = [];
    }

    [MemoryPackable]
    private sealed partial class FastMemoryPackedFileIndexItem
    {
        public string Path { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
    }

    [MemoryPackable]
    private sealed partial class CompactMemoryPackedFileIndexCache
    {
        public int Version { get; set; }
        public long UpdatedAtUnixMilliseconds { get; set; }
        public CompactMemoryPackedFileIndexItem[] Items { get; set; } = [];
    }

    [MemoryPackable]
    private sealed partial class CompactMemoryPackedFileIndexItem
    {
        public string Path { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
    }

    [MemoryPackable]
    private sealed partial class LegacyMemoryPackedFileIndexCache
    {
        public int Version { get; set; }
        public long UpdatedAtUnixMilliseconds { get; set; }
        public LegacyMemoryPackedFileIndexItem[] Items { get; set; } = [];
    }

    [MemoryPackable]
    private sealed partial class LegacyMemoryPackedFileIndexItem
    {
        public string Name { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string SearchPath { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public bool IsApp { get; set; }
        public string Extension { get; set; } = string.Empty;
        public string SearchName { get; set; } = string.Empty;
        public string SearchFileName { get; set; } = string.Empty;
        public string SearchNameWithoutExtension { get; set; } = string.Empty;

        public IndexedFileItem ToIndexedFileItem()
        {
            return new IndexedFileItem(
                Name,
                FileName,
                Path,
                SearchPath,
                Kind,
                IsDirectory,
                IsApp,
                Extension,
                SearchName,
                SearchFileName,
                SearchNameWithoutExtension);
        }
    }

    private sealed record IndexedFileItem(
        string Name,
        string FileName,
        string Path,
        string SearchPath,
        string Kind,
        bool IsDirectory,
        bool IsApp,
        string Extension,
        string SearchName,
        string SearchFileName,
        string SearchNameWithoutExtension);

    private readonly record struct NameBigram(char First, char Second);

    private readonly record struct NameGram(char First, char Second, char Third);

    private readonly record struct ScoredFileItem(IndexedFileItem Item, int Score);

    private interface ISearchNode
    {
        int Score(IndexedFileItem item);
    }

    private sealed class AndNode(IReadOnlyList<ISearchNode> children) : ISearchNode
    {
        public int Score(IndexedFileItem item)
        {
            var score = 0;
            foreach (var child in children)
            {
                var childScore = child.Score(item);
                if (childScore <= 0)
                {
                    return 0;
                }

                score += childScore;
            }

            return Math.Max(score, 1);
        }
    }

    private sealed class OrNode(ISearchNode left, ISearchNode right) : ISearchNode
    {
        public int Score(IndexedFileItem item)
        {
            return Math.Max(left.Score(item), right.Score(item));
        }
    }

    private sealed class NotNode(ISearchNode child) : ISearchNode
    {
        public int Score(IndexedFileItem item)
        {
            return child.Score(item) > 0 ? 0 : 1;
        }
    }

    private sealed class TermNode(string rawText, TermOptions options) : ISearchNode
    {
        private readonly string _text = options.CaseSensitive ? rawText : rawText.ToLowerInvariant();
        private readonly Regex? _regex = options.Regex && !string.IsNullOrWhiteSpace(rawText)
            ? TryCreateRegex(rawText, options.CaseSensitive)
            : null;
        private readonly Regex? _wildcard = !options.Regex && HasWildcard(rawText)
            ? WildcardRegex(options.CaseSensitive ? rawText : rawText.ToLowerInvariant(), !options.CaseSensitive)
            : null;

        public int Score(IndexedFileItem item)
        {
            if (string.IsNullOrWhiteSpace(_text))
            {
                return 1;
            }

            return options.MatchPath || LooksLikePathTerm(rawText)
                ? ScorePath(item)
                : ScoreName(item);
        }

        private int ScoreName(IndexedFileItem item)
        {
            if (_regex is not null)
            {
                return RegexMatchesName(item) ? 6500 : 0;
            }

            if (_wildcard is not null)
            {
                return WildcardMatchesName(item) ? 6500 : 0;
            }

            var primary = options.CaseSensitive ? item.Name : item.SearchName;
            var fileName = options.CaseSensitive ? item.FileName : item.SearchFileName;
            var baseName = options.CaseSensitive ? Path.GetFileNameWithoutExtension(item.FileName) : item.SearchNameWithoutExtension;
            var score = Math.Max(ScoreText(primary, _text), Math.Max(ScoreText(fileName, _text), ScoreText(baseName, _text)));
            if (score <= 0)
            {
                return 0;
            }

            var shortest = Math.Min(primary.Length, Math.Min(fileName.Length, baseName.Length));
            return Math.Max(score - Math.Min(shortest, 200), 1);
        }

        private bool RegexMatchesName(IndexedFileItem item)
        {
            return _regex is not null
                && (_regex.IsMatch(options.CaseSensitive ? item.Name : item.SearchName)
                    || _regex.IsMatch(options.CaseSensitive ? item.FileName : item.SearchFileName)
                    || _regex.IsMatch(options.CaseSensitive ? Path.GetFileNameWithoutExtension(item.FileName) : item.SearchNameWithoutExtension));
        }

        private bool WildcardMatchesName(IndexedFileItem item)
        {
            return _wildcard is not null
                && (_wildcard.IsMatch(options.CaseSensitive ? item.Name : item.SearchName)
                    || _wildcard.IsMatch(options.CaseSensitive ? item.FileName : item.SearchFileName)
                    || _wildcard.IsMatch(options.CaseSensitive ? Path.GetFileNameWithoutExtension(item.FileName) : item.SearchNameWithoutExtension));
        }

        private int ScorePath(IndexedFileItem item)
        {
            var path = options.CaseSensitive ? item.Path : item.SearchPath;
            var term = options.CaseSensitive ? rawText : NormalizePath(rawText);

            if (_regex is not null)
            {
                return _regex.IsMatch(path) ? 3500 : 0;
            }

            if (_wildcard is not null)
            {
                return _wildcard.IsMatch(path) ? 3500 : 0;
            }

            if (IsDriveTerm(term))
            {
                return path.StartsWith(term, StringComparison.Ordinal) ? 3200 : 0;
            }

            if (path.Contains(term, StringComparison.Ordinal))
            {
                return 3000 + Math.Min(term.Length, 500);
            }

            return 0;
        }

        private static Regex? TryCreateRegex(string pattern, bool caseSensitive)
        {
            try
            {
                return new Regex(pattern, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            }
            catch
            {
                return null;
            }
        }
    }

    private sealed class KindNode(string kind) : ISearchNode
    {
        public int Score(IndexedFileItem item)
        {
            return kind switch
            {
                AppKind => item.IsApp ? 1000 : 0,
                FileKind => item.IsDirectory ? 0 : 1000,
                FolderKind => item.IsDirectory ? 1000 : 0,
                _ => 0
            };
        }
    }

    private sealed class ExtensionNode(IReadOnlySet<string> extensions) : ISearchNode
    {
        public int Score(IndexedFileItem item)
        {
            return !item.IsDirectory && extensions.Contains(item.Extension) ? 1000 : 0;
        }
    }

    private sealed class ParentNode(string rawPath, bool caseSensitive) : ISearchNode
    {
        private readonly string _path = caseSensitive ? rawPath.TrimEnd('\\', '/') : NormalizePath(rawPath).TrimEnd('\\', '/');

        public int Score(IndexedFileItem item)
        {
            var directory = Path.GetDirectoryName(item.Path) ?? string.Empty;
            var itemParent = caseSensitive ? directory.TrimEnd('\\', '/') : NormalizePath(directory).TrimEnd('\\', '/');
            return itemParent.Equals(_path, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) ? 3500 : 0;
        }
    }

    private sealed record TermOptions(bool CaseSensitive = false, bool MatchPath = false, bool Regex = false);

    private static bool HasWildcard(string text)
    {
        return text.Contains('*') || text.Contains('?');
    }

    private static bool LooksLikePathTerm(string text)
    {
        return text.Contains('\\') || text.Contains('/') || IsDriveTerm(text);
    }

    private static bool IsDriveTerm(string text)
    {
        return text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':';
    }

    private enum JsonCacheProperty
    {
        None,
        Version,
        Items,
        Path,
        IsDirectory
    }

    private static ISearchNode BuildTermNode(string rawText)
    {
        var text = rawText;
        var options = new TermOptions();

        while (true)
        {
            var lower = text.ToLowerInvariant();
            if (lower.StartsWith("case:", StringComparison.Ordinal))
            {
                options = options with { CaseSensitive = true };
                text = text[5..];
                continue;
            }

            if (lower.StartsWith("nocase:", StringComparison.Ordinal))
            {
                options = options with { CaseSensitive = false };
                text = text[7..];
                continue;
            }

            if (lower.StartsWith("path:", StringComparison.Ordinal))
            {
                options = options with { MatchPath = true };
                text = text[5..];
                continue;
            }

            if (lower.StartsWith("nopath:", StringComparison.Ordinal))
            {
                options = options with { MatchPath = false };
                text = text[7..];
                continue;
            }

            if (lower.StartsWith("regex:", StringComparison.Ordinal))
            {
                options = options with { Regex = true };
                text = text[6..];
                continue;
            }

            if (lower.StartsWith("noregex:", StringComparison.Ordinal))
            {
                options = options with { Regex = false };
                text = text[8..];
                continue;
            }

            break;
        }

        var normalized = text.ToLowerInvariant();
        if (normalized is "file:" or "files:")
        {
            return new KindNode(FileKind);
        }

        if (normalized is "folder:" or "folders:")
        {
            return new KindNode(FolderKind);
        }

        if (normalized.StartsWith("file:", StringComparison.Ordinal) && normalized.Length > 5)
        {
            return new AndNode([new KindNode(FileKind), new TermNode(text[5..], options)]);
        }

        if (normalized.StartsWith("folder:", StringComparison.Ordinal) && normalized.Length > 7)
        {
            return new AndNode([new KindNode(FolderKind), new TermNode(text[7..], options)]);
        }

        if (normalized.StartsWith("folders:", StringComparison.Ordinal) && normalized.Length > 8)
        {
            return new AndNode([new KindNode(FolderKind), new TermNode(text[8..], options)]);
        }

        if (normalized.StartsWith("ext:", StringComparison.Ordinal))
        {
            return new ExtensionNode(ParseExtensions(text[4..]));
        }

        if (normalized.StartsWith("parent:", StringComparison.Ordinal))
        {
            return new ParentNode(text[7..].Trim('"'), options.CaseSensitive);
        }

        foreach (var pair in MacroExtensions)
        {
            if (normalized == $"{pair.Key}:")
            {
                return pair.Key.Equals("exe", StringComparison.OrdinalIgnoreCase)
                    ? new OrNode(new KindNode(AppKind), new ExtensionNode(pair.Value.ToHashSet(StringComparer.OrdinalIgnoreCase)))
                    : new ExtensionNode(pair.Value.ToHashSet(StringComparer.OrdinalIgnoreCase));
            }
        }

        return new TermNode(text, options);
    }

    private static IReadOnlySet<string> ParseExtensions(string text)
    {
        var values = text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.StartsWith('.') ? value.ToLowerInvariant() : $".{value.ToLowerInvariant()}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return values.Count == 0 ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : values;
    }

    private enum TokenKind
    {
        Term,
        Or,
        Not,
        GroupStart,
        GroupEnd
    }

    private sealed record SearchToken(TokenKind Kind, string Text);

    private static class EverythingSearchParser
    {
        public static ISearchNode Parse(string query)
        {
            var tokens = Tokenize(query);
            var parser = new Parser(tokens);
            return parser.ParseExpression();
        }

        private static IReadOnlyList<SearchToken> Tokenize(string query)
        {
            var tokens = new List<SearchToken>();
            var index = 0;

            while (index < query.Length)
            {
                var ch = query[index];
                if (char.IsWhiteSpace(ch))
                {
                    index++;
                    continue;
                }

                if (ch == '|')
                {
                    tokens.Add(new SearchToken(TokenKind.Or, "|"));
                    index++;
                    continue;
                }

                if (ch == '!')
                {
                    tokens.Add(new SearchToken(TokenKind.Not, "!"));
                    index++;
                    continue;
                }

                if (ch == '<')
                {
                    tokens.Add(new SearchToken(TokenKind.GroupStart, "<"));
                    index++;
                    continue;
                }

                if (ch == '>')
                {
                    tokens.Add(new SearchToken(TokenKind.GroupEnd, ">"));
                    index++;
                    continue;
                }

                if (ch == '"')
                {
                    tokens.Add(new SearchToken(TokenKind.Term, ReadQuoted(query, ref index)));
                    continue;
                }

                tokens.Add(new SearchToken(TokenKind.Term, ReadTerm(query, ref index)));
            }

            return tokens;
        }

        private static string ReadQuoted(string query, ref int index)
        {
            index++;
            var builder = new StringBuilder();
            while (index < query.Length)
            {
                var ch = query[index++];
                if (ch == '"')
                {
                    break;
                }

                builder.Append(ch);
            }

            return builder.ToString();
        }

        private static string ReadTerm(string query, ref int index)
        {
            var builder = new StringBuilder();
            while (index < query.Length)
            {
                var ch = query[index];
                if (char.IsWhiteSpace(ch) || ch is '|' or '!' or '<' or '>')
                {
                    break;
                }

                builder.Append(ch);
                index++;
            }

            return builder.ToString();
        }

        private sealed class Parser(IReadOnlyList<SearchToken> tokens)
        {
            private int _index;

            public ISearchNode ParseExpression()
            {
                return ParseOr();
            }

            private ISearchNode ParseOr()
            {
                var left = ParseAnd();
                while (Match(TokenKind.Or))
                {
                    var right = ParseAnd();
                    left = new OrNode(left, right);
                }

                return left;
            }

            private ISearchNode ParseAnd()
            {
                var nodes = new List<ISearchNode>();
                while (!IsAtEnd && Peek().Kind is not (TokenKind.Or or TokenKind.GroupEnd))
                {
                    nodes.Add(ParseNot());
                }

                return nodes.Count == 1 ? nodes[0] : new AndNode(nodes);
            }

            private ISearchNode ParseNot()
            {
                if (Match(TokenKind.Not))
                {
                    return new NotNode(ParseNot());
                }

                return ParsePrimary();
            }

            private ISearchNode ParsePrimary()
            {
                if (Match(TokenKind.GroupStart))
                {
                    var node = ParseOr();
                    Match(TokenKind.GroupEnd);
                    return node;
                }

                if (Match(TokenKind.Term, out var token))
                {
                    return BuildTermNode(token.Text);
                }

                return new AndNode([]);
            }

            private bool IsAtEnd => _index >= tokens.Count;

            private SearchToken Peek()
            {
                return tokens[_index];
            }

            private bool Match(TokenKind kind)
            {
                if (IsAtEnd || Peek().Kind != kind)
                {
                    return false;
                }

                _index++;
                return true;
            }

            private bool Match(TokenKind kind, out SearchToken token)
            {
                if (!IsAtEnd && Peek().Kind == kind)
                {
                    token = Peek();
                    _index++;
                    return true;
                }

                token = new SearchToken(kind, string.Empty);
                return false;
            }
        }
    }
}

public sealed record LocalFileSearchResponse(
    IReadOnlyList<LauncherSearchResult> Results,
    bool IsIndexing,
    int IndexedCount);

public sealed record LocalFileIndexProgress(
    bool IsIndexing,
    int IndexedCount,
    int RootsTotal,
    int RootsCompleted,
    int DirectoriesScanned,
    int DirectoriesQueued,
    int SkippedCount,
    string CurrentRoot,
    bool IsLoadingCache,
    bool IsCompletingCacheLoad,
    long CacheBytesRead,
    long CacheBytesTotal)
{
    public double CompletionRatio
    {
        get
        {
            if (!IsIndexing)
            {
                return 1;
            }

            if (IsLoadingCache || IsCompletingCacheLoad)
            {
                if (IsCompletingCacheLoad)
                {
                    return IndexedCount > 0 ? 0.5 : 0.1;
                }

                if (CacheBytesTotal > 0)
                {
                    return Math.Clamp(CacheBytesRead / (double)CacheBytesTotal, 0.02, 0.98);
                }

                return IndexedCount > 0 ? 0.5 : 0.05;
            }

            var rootRatio = RootsTotal <= 0 ? 0 : RootsCompleted / (double)RootsTotal;
            var directoryTotal = DirectoriesScanned + DirectoriesQueued;
            var directoryRatio = directoryTotal <= 0 ? 0 : DirectoriesScanned / (double)directoryTotal;
            return Math.Clamp((rootRatio * 0.7) + (directoryRatio * 0.3), 0, 0.98);
        }
    }
}
