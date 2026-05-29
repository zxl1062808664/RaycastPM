using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MemoryPack;
using Microsoft.Win32.SafeHandles;

namespace RaycastPM.Services;

public sealed partial class LocalFileSearchService
{
    private const int NtfsJournalCacheVersion = 1;
    private const uint FsctlEnumUsnData = 0x000900B3;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int ErrorHandleEof = 38;
    private const int NtfsBufferSize = 1024 * 1024;
    private const uint UsnReasonFileCreate = 0x00000100;
    private const uint UsnReasonFileDelete = 0x00000200;
    private const uint UsnReasonRenameOldName = 0x00001000;
    private const uint UsnReasonRenameNewName = 0x00002000;
    private const uint FileAttributeDirectory = 0x00000010;

    private bool TryIndexNtfsRoot(string root, CancellationToken cancellationToken)
    {
        if (!IsNtfsRoot(root))
        {
            return false;
        }

        if (!TryReadNtfsSnapshot(root, cancellationToken, out var snapshot))
        {
            AppDiagnostics.LogInfo($"root={root}; NTFS MFT enumeration failed", "index startup");
            return false;
        }

        AddNtfsSnapshotItems(root, snapshot, removeExistingRoot: false, cancellationToken);
        AppDiagnostics.LogInfo($"root={root}; NTFS MFT indexed; mftRecords={snapshot.Records.Count:N0}", "index startup");
        return true;
    }

    private void QueueNtfsJournalUpdate()
    {
        if (Interlocked.Exchange(ref _ntfsJournalUpdateQueued, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                ApplyNtfsJournalUpdates(CancellationToken.None);
            }
            finally
            {
                Interlocked.Exchange(ref _ntfsJournalUpdateQueued, 0);
            }
        });
    }

    private void ApplyNtfsJournalUpdates(CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var root in SearchRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsNtfsRoot(root))
            {
                continue;
            }

            if (!_ntfsVolumeIndexes.TryGetValue(root, out var volume))
            {
                AppDiagnostics.LogInfo($"root={root}; missing NTFS FRN cache; rebuild MFT", "index startup");
                changed |= TryRebuildNtfsRoot(root, cancellationToken);
                continue;
            }

            var status = TryApplyNtfsJournalDelta(root, volume, cancellationToken);
            if (status == NtfsJournalUpdateStatus.Applied)
            {
                AppDiagnostics.LogInfo($"root={root}; applied NTFS USN delta", "index startup");
                changed = true;
                continue;
            }

            if (status == NtfsJournalUpdateStatus.Unavailable)
            {
                AppDiagnostics.LogInfo($"root={root}; USN unavailable or invalid; rebuild MFT", "index startup");
                changed |= TryRebuildNtfsRoot(root, cancellationToken);
            }
        }

        if (changed)
        {
            QueueCacheSave();
        }
    }

    private bool TryRebuildNtfsRoot(string root, CancellationToken cancellationToken)
    {
        if (!TryReadNtfsSnapshot(root, cancellationToken, out var snapshot))
        {
            AppDiagnostics.LogInfo($"root={root}; rebuild MFT failed; keep cache/watch fallback", "index startup");
            return false;
        }

        AddNtfsSnapshotItems(root, snapshot, removeExistingRoot: true, cancellationToken);
        AppDiagnostics.LogInfo($"root={root}; rebuilt NTFS MFT; mftRecords={snapshot.Records.Count:N0}", "index startup");
        return true;
    }

    private void AddNtfsSnapshotItems(
        string root,
        NtfsVolumeSnapshot snapshot,
        bool removeExistingRoot,
        CancellationToken cancellationToken)
    {
        if (removeExistingRoot)
        {
            RemoveFromIndex(root, includeChildren: true);
        }

        var pathCache = new Dictionary<ulong, string?>();
        var added = 0;
        foreach (var record in snapshot.Records.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryMaterializeNtfsRecord(root, snapshot.Records, pathCache, record, out var path, out var isDirectory))
            {
                continue;
            }

            AddToIndex(path, isDirectory);
            if ((++added & 0x3FF) == 0)
            {
                Interlocked.Add(ref _directoriesScanned, 1);
            }
        }

        _ntfsVolumeIndexes[root] = new NtfsVolumeIndex(
            root,
            snapshot.JournalId,
            snapshot.NextUsn,
            snapshot.Records.ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    private NtfsJournalUpdateStatus TryApplyNtfsJournalDelta(
        string root,
        NtfsVolumeIndex volume,
        CancellationToken cancellationToken)
    {
        if (!TryOpenNtfsVolume(root, out var handle))
        {
            return NtfsJournalUpdateStatus.Unavailable;
        }

        using (handle)
        {
            if (!TryQueryUsnJournal(handle, out var journal))
            {
                return NtfsJournalUpdateStatus.Unavailable;
            }

            if (journal.JournalId != volume.JournalId
                || volume.NextUsn < journal.FirstUsn
                || volume.NextUsn > journal.NextUsn)
            {
                AppDiagnostics.LogInfo(
                    $"root={root}; USN state mismatch; cached={volume.NextUsn}; first={journal.FirstUsn}; next={journal.NextUsn}",
                    "index startup");
                return NtfsJournalUpdateStatus.Unavailable;
            }

            if (volume.NextUsn == journal.NextUsn)
            {
                AppDiagnostics.LogInfo($"root={root}; USN no changes; next={journal.NextUsn}", "index startup");
                return NtfsJournalUpdateStatus.NoChanges;
            }

            var startUsn = volume.NextUsn;
            AppDiagnostics.LogInfo($"root={root}; read USN delta; from={startUsn}; to={journal.NextUsn}", "index startup");
            var output = ArrayPool<byte>.Shared.Rent(NtfsBufferSize);
            try
            {
                while (startUsn < journal.NextUsn)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var input = CreateReadUsnJournalInput(startUsn, journal.JournalId);
                    if (!DeviceIoControl(
                            handle,
                            FsctlReadUsnJournal,
                            input,
                            input.Length,
                            output,
                            output.Length,
                            out var bytesReturned,
                            IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == ErrorHandleEof)
                        {
                            break;
                        }

                        AppDiagnostics.LogException(new Win32Exception(error), $"read NTFS USN journal {root}");
                        return NtfsJournalUpdateStatus.Unavailable;
                    }

                    if (bytesReturned < sizeof(long))
                    {
                        break;
                    }

                    var nextUsn = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(0, sizeof(long)));
                    var offset = sizeof(long);
                    while (offset + sizeof(int) <= bytesReturned)
                    {
                        var recordLength = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(offset, sizeof(int)));
                        if (recordLength <= 0 || offset + recordLength > bytesReturned)
                        {
                            break;
                        }

                        if (TryParseUsnRecord(output.AsSpan(offset, recordLength), out var record))
                        {
                            ApplyNtfsUsnRecord(root, volume, record);
                        }

                        offset += recordLength;
                    }

                    if (nextUsn <= startUsn)
                    {
                        break;
                    }

                    startUsn = nextUsn;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(output);
            }

            volume.JournalId = journal.JournalId;
            volume.NextUsn = journal.NextUsn;
            return NtfsJournalUpdateStatus.Applied;
        }
    }

    private void ApplyNtfsUsnRecord(string root, NtfsVolumeIndex volume, NtfsUsnRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Name) || IsNtfsMetadataName(record.Name))
        {
            return;
        }

        volume.Records.TryGetValue(record.FileReferenceNumber, out var oldEntry);
        var oldPath = oldEntry is null
            ? null
            : ResolveNtfsPath(root, volume.Records, new Dictionary<ulong, string?>(), oldEntry.FileReferenceNumber);

        if ((record.Reason & UsnReasonFileDelete) != 0 || (record.Reason & UsnReasonRenameOldName) != 0)
        {
            if (!string.IsNullOrWhiteSpace(oldPath))
            {
                RemoveFromIndex(oldPath, oldEntry?.IsDirectory == true);
            }

            RemoveNtfsRecordTree(volume, record.FileReferenceNumber);
            return;
        }

        var entry = new NtfsRecordEntry(
            record.FileReferenceNumber,
            record.ParentFileReferenceNumber,
            record.Name,
            record.FileAttributes);
        volume.Records[record.FileReferenceNumber] = entry;

        var pathCache = new Dictionary<ulong, string?>();
        if (!TryMaterializeNtfsRecord(root, volume.Records, pathCache, entry, out var path, out var isDirectory))
        {
            if (!string.IsNullOrWhiteSpace(oldPath))
            {
                RemoveFromIndex(oldPath, oldEntry?.IsDirectory == true);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(oldPath)
            && !oldPath.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            RemoveFromIndex(oldPath, oldEntry?.IsDirectory == true);
        }

        if ((record.Reason & UsnReasonRenameNewName) != 0 && isDirectory)
        {
            AddNtfsRecordTree(root, volume, record.FileReferenceNumber);
            return;
        }

        AddOrReplaceIndexedPath(path, isDirectory);
    }

    private void AddNtfsRecordTree(string root, NtfsVolumeIndex volume, ulong startFileReferenceNumber)
    {
        var queue = new Queue<ulong>();
        var seen = new HashSet<ulong>();
        var pathCache = new Dictionary<ulong, string?>();
        queue.Enqueue(startFileReferenceNumber);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current) || !volume.Records.TryGetValue(current, out var record))
            {
                continue;
            }

            if (TryMaterializeNtfsRecord(root, volume.Records, pathCache, record, out var path, out var isDirectory))
            {
                AddOrReplaceIndexedPath(path, isDirectory);
            }

            foreach (var child in volume.Records.Values)
            {
                if (child.ParentFileReferenceNumber == current)
                {
                    queue.Enqueue(child.FileReferenceNumber);
                }
            }
        }
    }

    private static void RemoveNtfsRecordTree(NtfsVolumeIndex volume, ulong startFileReferenceNumber)
    {
        var queue = new Queue<ulong>();
        var seen = new HashSet<ulong>();
        queue.Enqueue(startFileReferenceNumber);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var child in volume.Records.Values)
            {
                if (child.ParentFileReferenceNumber == current)
                {
                    queue.Enqueue(child.FileReferenceNumber);
                }
            }

            volume.Records.TryRemove(current, out _);
        }
    }

    private bool TryReadNtfsSnapshot(
        string root,
        CancellationToken cancellationToken,
        out NtfsVolumeSnapshot snapshot)
    {
        snapshot = default;
        if (!TryOpenNtfsVolume(root, out var handle))
        {
            return false;
        }

        using (handle)
        {
            if (!TryQueryUsnJournal(handle, out var journal))
            {
                return false;
            }

            var records = new Dictionary<ulong, NtfsRecordEntry>();
            var startFileReferenceNumber = 0UL;
            var output = ArrayPool<byte>.Shared.Rent(NtfsBufferSize);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var input = CreateMftEnumInput(startFileReferenceNumber, journal.NextUsn);
                    if (!DeviceIoControl(
                            handle,
                            FsctlEnumUsnData,
                            input,
                            input.Length,
                            output,
                            output.Length,
                            out var bytesReturned,
                            IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == ErrorHandleEof)
                        {
                            break;
                        }

                        AppDiagnostics.LogException(new Win32Exception(error), $"enumerate NTFS MFT {root}");
                        return false;
                    }

                    if (bytesReturned < sizeof(ulong))
                    {
                        break;
                    }

                    startFileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(0, sizeof(ulong)));
                    if (bytesReturned == sizeof(ulong))
                    {
                        break;
                    }

                    var offset = sizeof(ulong);
                    while (offset + sizeof(int) <= bytesReturned)
                    {
                        var recordLength = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(offset, sizeof(int)));
                        if (recordLength <= 0 || offset + recordLength > bytesReturned)
                        {
                            break;
                        }

                        if (TryParseUsnRecord(output.AsSpan(offset, recordLength), out var record)
                            && !string.IsNullOrWhiteSpace(record.Name)
                            && !IsNtfsMetadataName(record.Name))
                        {
                            var entry = new NtfsRecordEntry(
                                record.FileReferenceNumber,
                                record.ParentFileReferenceNumber,
                                record.Name,
                                record.FileAttributes);
                            if (!records.TryGetValue(entry.FileReferenceNumber, out var existing)
                                || ShouldReplaceNtfsRecord(existing, entry))
                            {
                                records[entry.FileReferenceNumber] = entry;
                            }
                        }

                        offset += recordLength;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(output);
            }

            snapshot = new NtfsVolumeSnapshot(root, journal.JournalId, journal.NextUsn, records);
            return true;
        }
    }

    private void LoadNtfsJournalCache()
    {
        if (!File.Exists(_ntfsJournalCachePath))
        {
            AppDiagnostics.LogInfo("NTFS FRN cache not found", "index startup");
            return;
        }

        try
        {
            using var stream = new FileStream(
                _ntfsJournalCachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CacheFileBufferSize,
                FileOptions.SequentialScan);
            var cache = MemoryPackSerializer
                .DeserializeAsync<NtfsJournalIndexCache>(stream)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (cache?.Version != NtfsJournalCacheVersion)
            {
                AppDiagnostics.LogInfo("NTFS FRN cache version mismatch; rebuild required", "index startup");
                return;
            }

            _ntfsVolumeIndexes.Clear();
            foreach (var volume in cache.Volumes)
            {
                if (string.IsNullOrWhiteSpace(volume.Root))
                {
                    continue;
                }

                _ntfsVolumeIndexes[NormalizeRootPath(volume.Root)] = new NtfsVolumeIndex(
                    NormalizeRootPath(volume.Root),
                    volume.JournalId,
                    volume.NextUsn,
                    volume.Records
                        .Where(record => record.FileReferenceNumber != 0 && !string.IsNullOrWhiteSpace(record.Name))
                        .ToDictionary(
                            record => record.FileReferenceNumber,
                            record => new NtfsRecordEntry(
                                record.FileReferenceNumber,
                                record.ParentFileReferenceNumber,
                                record.Name,
                                record.FileAttributes)));
            }

            AppDiagnostics.LogInfo($"loaded NTFS FRN cache; volumes={_ntfsVolumeIndexes.Count:N0}", "index startup");
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "load NTFS journal cache");
            _ntfsVolumeIndexes.Clear();
        }
    }

    private void SaveNtfsJournalCache()
    {
        try
        {
            var cache = new NtfsJournalIndexCache
            {
                Version = NtfsJournalCacheVersion,
                Volumes = _ntfsVolumeIndexes.Values
                    .Select(volume => new NtfsJournalVolumeCache
                    {
                        Root = volume.Root,
                        JournalId = volume.JournalId,
                        NextUsn = volume.NextUsn,
                        Records = volume.Records.Values
                            .Select(record => new NtfsJournalRecordCache
                            {
                                FileReferenceNumber = record.FileReferenceNumber,
                                ParentFileReferenceNumber = record.ParentFileReferenceNumber,
                                Name = record.Name,
                                FileAttributes = record.FileAttributes
                            })
                            .ToArray()
                    })
                    .ToArray()
            };

            var tempPath = $"{_ntfsJournalCachePath}.tmp";
            TryDeleteFile(tempPath);
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

            if (File.Exists(_ntfsJournalCachePath))
            {
                File.Delete(_ntfsJournalCachePath);
            }

            File.Move(tempPath, _ntfsJournalCachePath);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "save NTFS journal cache");
        }
    }

    private static bool TryMaterializeNtfsRecord(
        string root,
        IReadOnlyDictionary<ulong, NtfsRecordEntry> records,
        Dictionary<ulong, string?> pathCache,
        NtfsRecordEntry record,
        out string path,
        out bool isDirectory)
    {
        path = string.Empty;
        isDirectory = record.IsDirectory;
        if (string.IsNullOrWhiteSpace(record.Name))
        {
            return false;
        }

        var resolvedPath = ResolveNtfsPath(root, records, pathCache, record.FileReferenceNumber);
        if (string.IsNullOrWhiteSpace(resolvedPath)
            || resolvedPath.Equals(NormalizeRootPath(root), StringComparison.OrdinalIgnoreCase)
            || ContainsSkippedDirectory(root, resolvedPath))
        {
            return false;
        }

        if (record.IsDirectory && ShouldSkipDirectory(resolvedPath, (FileAttributes)record.FileAttributes))
        {
            return false;
        }

        path = resolvedPath;
        return true;
    }

    private static string? ResolveNtfsPath(
        string root,
        IReadOnlyDictionary<ulong, NtfsRecordEntry> records,
        Dictionary<ulong, string?> pathCache,
        ulong fileReferenceNumber,
        int depth = 0)
    {
        if (pathCache.TryGetValue(fileReferenceNumber, out var cachedPath))
        {
            return cachedPath;
        }

        if (depth > 256 || !records.TryGetValue(fileReferenceNumber, out var record))
        {
            return null;
        }

        var normalizedRoot = NormalizeRootPath(root);
        string? parentPath;
        if (record.ParentFileReferenceNumber == 0
            || record.ParentFileReferenceNumber == record.FileReferenceNumber
            || !records.ContainsKey(record.ParentFileReferenceNumber))
        {
            parentPath = normalizedRoot;
        }
        else
        {
            parentPath = ResolveNtfsPath(root, records, pathCache, record.ParentFileReferenceNumber, depth + 1);
        }

        if (string.IsNullOrWhiteSpace(parentPath))
        {
            pathCache[fileReferenceNumber] = null;
            return null;
        }

        var path = Path.Combine(parentPath, record.Name);
        pathCache[fileReferenceNumber] = path;
        return path;
    }

    private static bool ContainsSkippedDirectory(string root, string path)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(NormalizeRootPath(root), path);
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(relative) || relative == ".")
        {
            return false;
        }

        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (SkippedDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldReplaceNtfsRecord(NtfsRecordEntry existing, NtfsRecordEntry candidate)
    {
        if (existing.Name.Contains('~') && !candidate.Name.Contains('~'))
        {
            return true;
        }

        return candidate.Name.Length > existing.Name.Length;
    }

    private static bool IsNtfsRoot(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady
                && drive.DriveType is DriveType.Fixed or DriveType.Removable
                && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
                && TryGetVolumeDevicePath(root, out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryOpenNtfsVolume(string root, out SafeFileHandle handle)
    {
        handle = new SafeFileHandle(IntPtr.Zero, ownsHandle: true);
        if (!TryGetVolumeDevicePath(root, out var devicePath))
        {
            return false;
        }

        handle = CreateFile(
            devicePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        AppDiagnostics.LogException(new Win32Exception(error), $"open NTFS volume {root}");
        return false;
    }

    private static bool TryQueryUsnJournal(SafeFileHandle handle, out NtfsJournalState state)
    {
        state = default;
        Span<byte> output = stackalloc byte[64];
        var outputArray = output.ToArray();
        if (!DeviceIoControl(
                handle,
                FsctlQueryUsnJournal,
                null,
                0,
                outputArray,
                outputArray.Length,
                out var bytesReturned,
                IntPtr.Zero)
            || bytesReturned < 24)
        {
            return false;
        }

        state = new NtfsJournalState(
            BinaryPrimitives.ReadUInt64LittleEndian(outputArray.AsSpan(0, sizeof(ulong))),
            BinaryPrimitives.ReadInt64LittleEndian(outputArray.AsSpan(8, sizeof(long))),
            BinaryPrimitives.ReadInt64LittleEndian(outputArray.AsSpan(16, sizeof(long))));
        return true;
    }

    private static byte[] CreateMftEnumInput(ulong startFileReferenceNumber, long highUsn)
    {
        var input = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(0, sizeof(ulong)), startFileReferenceNumber);
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(8, sizeof(long)), 0);
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16, sizeof(long)), highUsn);
        return input;
    }

    private static byte[] CreateReadUsnJournalInput(long startUsn, ulong journalId)
    {
        var input = new byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(0, sizeof(long)), startUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8, sizeof(uint)), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(12, sizeof(uint)), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(16, sizeof(ulong)), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(24, sizeof(ulong)), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32, sizeof(ulong)), journalId);
        return input;
    }

    private static bool TryParseUsnRecord(ReadOnlySpan<byte> buffer, out NtfsUsnRecord record)
    {
        record = default;
        if (buffer.Length < 60)
        {
            return false;
        }

        var recordLength = BinaryPrimitives.ReadInt32LittleEndian(buffer[..sizeof(int)]);
        if (recordLength < 60 || recordLength > buffer.Length)
        {
            return false;
        }

        var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(4, sizeof(ushort)));
        if (majorVersion != 2)
        {
            return false;
        }

        var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(56, sizeof(ushort)));
        var fileNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(58, sizeof(ushort)));
        if (fileNameOffset + fileNameLength > recordLength)
        {
            return false;
        }

        var name = Encoding.Unicode.GetString(buffer.Slice(fileNameOffset, fileNameLength));
        record = new NtfsUsnRecord(
            BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(8, sizeof(ulong))),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(16, sizeof(ulong))),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(24, sizeof(long))),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(40, sizeof(uint))),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(52, sizeof(uint))),
            name);
        return true;
    }

    private static bool TryGetVolumeDevicePath(string root, out string devicePath)
    {
        devicePath = string.Empty;
        var driveRoot = NormalizeRootPath(root);
        if (driveRoot.Length < 2 || driveRoot[1] != ':' || !char.IsAsciiLetter(driveRoot[0]))
        {
            return false;
        }

        devicePath = "\\\\.\\" + driveRoot[..2];
        return true;
    }

    private static string NormalizeRootPath(string root)
    {
        var pathRoot = Path.GetPathRoot(root);
        if (string.IsNullOrWhiteSpace(pathRoot))
        {
            return root;
        }

        return pathRoot.EndsWith(Path.DirectorySeparatorChar)
            ? pathRoot
            : $"{pathRoot}{Path.DirectorySeparatorChar}";
    }

    private static bool IsNtfsMetadataName(string name)
    {
        return name is "$MFT"
            or "$MFTMirr"
            or "$LogFile"
            or "$Volume"
            or "$AttrDef"
            or "$Bitmap"
            or "$Boot"
            or "$BadClus"
            or "$Secure"
            or "$UpCase"
            or "$Extend"
            or "$ObjId"
            or "$Quota"
            or "$Reparse"
            or "$RmMetadata";
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    private sealed class NtfsVolumeIndex(
        string root,
        ulong journalId,
        long nextUsn,
        Dictionary<ulong, NtfsRecordEntry> records)
    {
        public string Root { get; } = NormalizeRootPath(root);
        public ulong JournalId { get; set; } = journalId;
        public long NextUsn { get; set; } = nextUsn;
        public ConcurrentDictionary<ulong, NtfsRecordEntry> Records { get; } = new(records);
    }

    private readonly record struct NtfsVolumeSnapshot(
        string Root,
        ulong JournalId,
        long NextUsn,
        Dictionary<ulong, NtfsRecordEntry> Records);

    private sealed record NtfsRecordEntry(
        ulong FileReferenceNumber,
        ulong ParentFileReferenceNumber,
        string Name,
        uint FileAttributes)
    {
        public bool IsDirectory => (FileAttributes & FileAttributeDirectory) != 0;
    }

    private readonly record struct NtfsJournalState(ulong JournalId, long FirstUsn, long NextUsn);

    private readonly record struct NtfsUsnRecord(
        ulong FileReferenceNumber,
        ulong ParentFileReferenceNumber,
        long Usn,
        uint Reason,
        uint FileAttributes,
        string Name);

    private enum NtfsJournalUpdateStatus
    {
        Unavailable,
        NoChanges,
        Applied
    }

    [MemoryPackable]
    private sealed partial class NtfsJournalIndexCache
    {
        public int Version { get; set; }
        public NtfsJournalVolumeCache[] Volumes { get; set; } = [];
    }

    [MemoryPackable]
    private sealed partial class NtfsJournalVolumeCache
    {
        public string Root { get; set; } = string.Empty;
        public ulong JournalId { get; set; }
        public long NextUsn { get; set; }
        public NtfsJournalRecordCache[] Records { get; set; } = [];
    }

    [MemoryPackable]
    private sealed partial class NtfsJournalRecordCache
    {
        public ulong FileReferenceNumber { get; set; }
        public ulong ParentFileReferenceNumber { get; set; }
        public string Name { get; set; } = string.Empty;
        public uint FileAttributes { get; set; }
    }
}
