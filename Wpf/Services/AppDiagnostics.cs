using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RaycastPM.Services;

public static class AppDiagnostics
{
    private static readonly object Sync = new();
    private const int AttachParentProcess = -1;
    private static int _consoleAttachAttempted;
    private static bool _isEnabled = true;
    public static string DefaultLogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RaycastPM",
        "logs");
    private static string _logDirectory = DefaultLogDirectory;

    public static void Configure(bool enabled, string? logDirectory)
    {
        lock (Sync)
        {
            _isEnabled = enabled;
            _logDirectory = ResolveLogDirectory(logDirectory);
        }

        LogInfo($"configured; enabled={enabled}; directory={_logDirectory}", "diagnostics");
    }

    public static string ResolveLogDirectory(string? logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            return DefaultLogDirectory;
        }

        try
        {
            return Path.GetFullPath(logDirectory.Trim());
        }
        catch
        {
            return DefaultLogDirectory;
        }
    }

    public static void LogInfo(string message, string context = "")
    {
        var line = FormatLine(message, context);
        TryWriteConsole(line);
        System.Diagnostics.Debug.WriteLine(line);
        TryWriteLog($"{line}{Environment.NewLine}");
    }

    public static void LogException(Exception exception, string context)
    {
        var header = FormatLine($"{exception.GetType().FullName}: {exception.Message}", context);
        TryWriteConsole(header);
        System.Diagnostics.Debug.WriteLine(header);

        var builder = new StringBuilder();
        builder.AppendLine(header);
        builder.AppendLine(exception.StackTrace);
        builder.AppendLine();

        TryWriteLog(builder.ToString());
    }

    private static void TryWriteLog(string text)
    {
        try
        {
            string directory;
            lock (Sync)
            {
                if (!_isEnabled)
                {
                    return;
                }

                directory = _logDirectory;
            }

            Directory.CreateDirectory(directory);
            lock (Sync)
            {
                File.AppendAllText(CurrentLogPath(directory), text, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private static string FormatLine(string message, string context)
    {
        var builder = new StringBuilder();
        builder.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
        if (!string.IsNullOrWhiteSpace(context))
        {
            builder.Append(context).Append(' ');
        }

        builder.Append(message);
        return builder.ToString();
    }

    private static void TryWriteConsole(string line)
    {
        try
        {
            if (Interlocked.Exchange(ref _consoleAttachAttempted, 1) == 0)
            {
                AttachConsole(AttachParentProcess);
                Console.OutputEncoding = Encoding.UTF8;
            }

            Console.WriteLine(line);
        }
        catch
        {
        }
    }

    private static string CurrentLogPath(string directory)
    {
        return Path.Combine(directory, $"raycastpm-{DateTime.Now:yyyyMMdd}.log");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
}
