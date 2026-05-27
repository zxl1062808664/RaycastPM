using System.IO;
using Microsoft.Win32;

namespace RaycastPM.Services;

public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "RaycastPM";

    public static bool IsEnabled()
    {
        var executablePath = GetExecutablePath();
        if (executablePath is null)
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        var value = key?.GetValue(EntryName) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(NormalizeRunValue(value), executablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(EntryName, false);
                return true;
            }

            var executablePath = GetExecutablePath();
            if (executablePath is null)
            {
                return false;
            }

            key.SetValue(EntryName, Quote(executablePath), RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (IsUsableExecutable(processPath))
        {
            return Path.GetFullPath(processPath!);
        }

        var appHostPath = Path.Combine(AppContext.BaseDirectory, $"{EntryName}.exe");
        return IsUsableExecutable(appHostPath)
            ? Path.GetFullPath(appHostPath)
            : null;
    }

    private static bool IsUsableExecutable(string? path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && File.Exists(path)
            && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRunValue(string value)
    {
        try
        {
            var trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"')
            {
                var closingQuote = trimmed.IndexOf('"', 1);
                if (closingQuote > 1)
                {
                    return Path.GetFullPath(trimmed[1..closingQuote]);
                }
            }

            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return value.Trim();
        }
    }

    private static string Quote(string path)
    {
        return $"\"{path}\"";
    }
}
