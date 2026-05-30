using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace RaycastPM.Services;

public sealed class SystemMonitorService : IDisposable
{
    private readonly object _gpuSync = new();
    private List<PerformanceCounter>? _gpuCounters;
    private bool _gpuCountersInitialized;
    private ulong _previousIdleTime;
    private ulong _previousKernelTime;
    private ulong _previousUserTime;
    private long _previousNetworkTicks;
    private long _previousBytesReceived;
    private long _previousBytesSent;

    public SystemMonitorSnapshot GetSnapshot()
    {
        var (bytesReceived, bytesSent) = ReadNetworkBytes();
        var nowTicks = Stopwatch.GetTimestamp();
        var elapsedSeconds = _previousNetworkTicks == 0
            ? 0
            : (nowTicks - _previousNetworkTicks) / (double)Stopwatch.Frequency;

        var downloadBytesPerSecond = elapsedSeconds <= 0
            ? 0
            : Math.Max(0, (bytesReceived - _previousBytesReceived) / elapsedSeconds);
        var uploadBytesPerSecond = elapsedSeconds <= 0
            ? 0
            : Math.Max(0, (bytesSent - _previousBytesSent) / elapsedSeconds);

        _previousNetworkTicks = nowTicks;
        _previousBytesReceived = bytesReceived;
        _previousBytesSent = bytesSent;

        return new SystemMonitorSnapshot(
            downloadBytesPerSecond,
            uploadBytesPerSecond,
            ReadCpuUsagePercent(),
            ReadGpuUsagePercent(),
            ReadMemoryUsagePercent());
    }

    public void Dispose()
    {
        lock (_gpuSync)
        {
            if (_gpuCounters is null)
            {
                return;
            }

            foreach (var counter in _gpuCounters)
            {
                counter.Dispose();
            }

            _gpuCounters.Clear();
            _gpuCounters = null;
        }
    }

    private static (long BytesReceived, long BytesSent) ReadNetworkBytes()
    {
        long bytesReceived = 0;
        long bytesSent = 0;

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            try
            {
                var statistics = networkInterface.GetIPStatistics();
                bytesReceived += statistics.BytesReceived;
                bytesSent += statistics.BytesSent;
            }
            catch
            {
            }
        }

        return (bytesReceived, bytesSent);
    }

    private double ReadCpuUsagePercent()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return 0;
        }

        var idle = idleTime.ToUInt64();
        var kernel = kernelTime.ToUInt64();
        var user = userTime.ToUInt64();
        if (_previousKernelTime == 0 && _previousUserTime == 0)
        {
            _previousIdleTime = idle;
            _previousKernelTime = kernel;
            _previousUserTime = user;
            return 0;
        }

        var idleDelta = idle - _previousIdleTime;
        var kernelDelta = kernel - _previousKernelTime;
        var userDelta = user - _previousUserTime;
        var totalDelta = kernelDelta + userDelta;

        _previousIdleTime = idle;
        _previousKernelTime = kernel;
        _previousUserTime = user;

        if (totalDelta == 0)
        {
            return 0;
        }

        return Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
    }

    private static double ReadMemoryUsagePercent()
    {
        var status = new MemoryStatusEx();
        status.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        return GlobalMemoryStatusEx(ref status) ? Math.Clamp(status.MemoryLoad, 0, 100) : 0;
    }

    private double? ReadGpuUsagePercent()
    {
        EnsureGpuCounters();
        lock (_gpuSync)
        {
            if (_gpuCounters is not { Count: > 0 })
            {
                return null;
            }

            double usage = 0;
            for (var index = _gpuCounters.Count - 1; index >= 0; index--)
            {
                try
                {
                    usage += _gpuCounters[index].NextValue();
                }
                catch
                {
                    _gpuCounters[index].Dispose();
                    _gpuCounters.RemoveAt(index);
                }
            }

            return Math.Clamp(usage, 0, 100);
        }
    }

    private void EnsureGpuCounters()
    {
        lock (_gpuSync)
        {
            if (_gpuCountersInitialized)
            {
                return;
            }

            _gpuCountersInitialized = true;
            _gpuCounters = [];

            try
            {
                if (!PerformanceCounterCategory.Exists("GPU Engine"))
                {
                    return;
                }

                var category = new PerformanceCounterCategory("GPU Engine");
                foreach (var instance in category.GetInstanceNames())
                {
                    try
                    {
                        var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                        counter.NextValue();
                        _gpuCounters.Add(counter);
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogException(ex, "initialize GPU performance counters");
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint lowDateTime;
        private readonly uint highDateTime;

        public ulong ToUInt64()
        {
            return ((ulong)highDateTime << 32) | lowDateTime;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}

public sealed record SystemMonitorSnapshot(
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    double CpuUsagePercent,
    double? GpuUsagePercent,
    double MemoryUsagePercent);
