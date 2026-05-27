using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RaycastPM.Models;

namespace RaycastPM.Services;

public sealed partial class LocalFileSearchService
{
    private const int CacheVersion = 1;
    private const string AppKind = "应用";
    private const string FileKind = "文件";
    private const string FolderKind = "文件夹";

    private static readonly string[] LaunchableExtensions = [".exe", ".lnk", ".appref-ms"];

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

    private readonly ConcurrentDictionary<string, IndexedFileItem> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _indexStartLock = new();
    private readonly string _cachePath;
    private CancellationTokenSource _indexCancellation = new();
    private Task? _indexTask;
    private int _indexedCount;
    private int _rootsTotal;
    private int _rootsCompleted;
    private int _directoriesScanned;
    private int _directoriesQueued;
    private int _skippedCount;
    private string _currentRoot = string.Empty;
    private bool _isIndexing;

    public LocalFileSearchService(string cacheFolder)
    {
        Directory.CreateDirectory(cacheFolder);
        _cachePath = Path.Combine(cacheFolder, "file-index-cache.json");
    }

    public bool IsIndexing => _isIndexing;
    public int IndexedCount => Volatile.Read(ref _indexedCount);

    public bool LoadCache()
    {
        if (!File.Exists(_cachePath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(_cachePath);
            var cache = JsonSerializer.Deserialize<FileIndexCache>(stream);
            if (cache?.Version != CacheVersion || cache.Items.Count == 0)
            {
                return false;
            }

            _index.Clear();
            foreach (var item in cache.Items)
            {
                AddToIndex(item.Path, item.IsDirectory);
            }

            Interlocked.Exchange(ref _indexedCount, _index.Count);
            return _index.Count > 0;
        }
        catch
        {
            return false;
        }
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
            _currentRoot);
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

            _isIndexing = true;
            _indexTask = Task.Run(() => BuildIndex(_indexCancellation.Token));
        }
    }

    public void RebuildIndex()
    {
        lock (_indexStartLock)
        {
            if (_indexTask is { IsCompleted: false })
            {
                _indexCancellation.Cancel();
            }

            _indexCancellation.Dispose();
            _indexCancellation = new CancellationTokenSource();
            _index.Clear();
            Interlocked.Exchange(ref _indexedCount, 0);
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

    public async Task<LocalFileSearchResponse> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        var trimmedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return new LocalFileSearchResponse([], IsIndexing, IndexedCount);
        }

        var expression = EverythingSearchParser.Parse(trimmedQuery);

        var results = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SearchIndex(expression, maxResults, cancellationToken);
        }, cancellationToken);

        return new LocalFileSearchResponse(results, IsIndexing, IndexedCount);
    }

    private LauncherSearchResult[] SearchIndex(ISearchNode expression, int maxResults, CancellationToken cancellationToken)
    {
        var capacity = Math.Max(maxResults * 3, maxResults);
        var candidates = new List<ScoredFileItem>(capacity);
        var lowestScore = 0;
        var scanned = 0;

        foreach (var item in _index.Values)
        {
            if ((++scanned & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var score = expression.Score(item);
            if (score <= 0 || (candidates.Count >= capacity && score < lowestScore))
            {
                continue;
            }

            candidates.Add(new ScoredFileItem(item, score));
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
        return candidates
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
                IndexRoot(root, cancellationToken);
                Interlocked.Increment(ref _rootsCompleted);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _currentRoot = string.Empty;
            _isIndexing = false;
            if (!cancellationToken.IsCancellationRequested)
            {
                SaveCache();
            }
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var cache = new FileIndexCache(
                CacheVersion,
                DateTimeOffset.Now,
                _index.Values
                    .Select(item => new CachedFileIndexItem(item.Path, item.IsDirectory))
                    .ToList());

            var tempPath = $"{_cachePath}.tmp";
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, cache);
            }

            if (File.Exists(_cachePath))
            {
                File.Delete(_cachePath);
            }

            File.Move(tempPath, _cachePath);
        }
        catch
        {
        }
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
        var kind = ItemKind(path, isDirectory);
        var fileName = DisplayName(path);
        var name = kind == AppKind ? Path.GetFileNameWithoutExtension(path) : fileName;
        var nameWithoutExtension = isDirectory ? name : Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(nameWithoutExtension))
        {
            nameWithoutExtension = name;
        }

        var item = new IndexedFileItem(
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

        if (_index.TryAdd(path, item))
        {
            Interlocked.Increment(ref _indexedCount);
        }
    }

    private static IEnumerable<string> SearchRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
            {
                continue;
            }

            if (drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network or DriveType.Ram)
            {
                yield return drive.RootDirectory.FullName;
            }
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

        return LaunchableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            ? AppKind
            : FileKind;
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

    private readonly record struct ScoredFileItem(IndexedFileItem Item, int Score);

    private sealed record FileIndexCache(
        int Version,
        DateTimeOffset UpdatedAt,
        List<CachedFileIndexItem> Items);

    private sealed record CachedFileIndexItem(
        string Path,
        bool IsDirectory);

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
    string CurrentRoot)
{
    public double CompletionRatio
    {
        get
        {
            if (!IsIndexing)
            {
                return 1;
            }

            var rootRatio = RootsTotal <= 0 ? 0 : RootsCompleted / (double)RootsTotal;
            var directoryTotal = DirectoriesScanned + DirectoriesQueued;
            var directoryRatio = directoryTotal <= 0 ? 0 : DirectoriesScanned / (double)directoryTotal;
            return Math.Clamp((rootRatio * 0.7) + (directoryRatio * 0.3), 0, 0.98);
        }
    }
}
