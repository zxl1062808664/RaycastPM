using System.IO;
using System.Text;

namespace RaycastPM.Services;

public static class AppDiagnostics
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RaycastPM",
        "logs");

    public static void LogException(Exception exception, string context)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var builder = new StringBuilder();
            builder.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
            if (!string.IsNullOrWhiteSpace(context))
            {
                builder.Append(context).Append(' ');
            }

            builder.Append(exception.GetType().FullName)
                   .Append(": ")
                   .AppendLine(exception.Message);
            builder.AppendLine(exception.StackTrace);
            builder.AppendLine();

            lock (Sync)
            {
                File.AppendAllText(CurrentLogPath(), builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private static string CurrentLogPath()
    {
        return Path.Combine(LogDirectory, $"crash-{DateTime.Now:yyyyMMdd}.log");
    }
}
