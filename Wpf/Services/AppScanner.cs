using System.IO;
using RaycastPM.Models;

namespace RaycastPM.Services;

public sealed class AppScanner
{
    private static readonly string[] LaunchableExtensions = [".exe", ".lnk", ".appref-ms"];
    private static readonly EnumerationOptions ScanEnumerationOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    public Task<IReadOnlyList<InstalledApp>> ScanAsync(IEnumerable<string>? customDirectories)
    {
        return Task.Run(() => Scan(customDirectories));
    }

    private static IReadOnlyList<InstalledApp> Scan(IEnumerable<string>? customDirectories)
    {
        var roots = DefaultRoots()
            .Concat((customDirectories ?? Enumerable.Empty<string>()).Select(Environment.ExpandEnvironmentVariables))
            .Where(path => !string.IsNullOrWhiteSpace(path) && SafeDirectoryExists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<InstalledApp>();

        foreach (var root in roots)
        {
            foreach (var file in EnumerateLaunchables(root))
            {
                if (!seen.Add(file))
                {
                    continue;
                }

                apps.Add(new InstalledApp
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Path = file
                });
            }
        }

        return apps
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> DefaultRoots()
    {
        var paths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };

        return paths.Where(path => !string.IsNullOrWhiteSpace(path));
    }

    private static IEnumerable<string> EnumerateLaunchables(string root)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(NormalizeDirectory(current)))
            {
                continue;
            }

            string[] directories;
            string[] files;

            try
            {
                directories = Directory.EnumerateDirectories(current, "*", ScanEnumerationOptions).ToArray();
                files = Directory.EnumerateFiles(current, "*", ScanEnumerationOptions).ToArray();
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogException(ex, $"scan directory {current}");
                continue;
            }

            foreach (var directory in directories)
            {
                if (!ShouldDescendDirectory(directory))
                {
                    continue;
                }

                pending.Push(directory);
            }

            foreach (var file in files)
            {
                if (LaunchableExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, $"check directory {path}");
            return false;
        }
    }

    private static bool ShouldDescendDirectory(string path)
    {
        try
        {
            return !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, $"read directory attributes {path}");
            return false;
        }
    }

    private static string NormalizeDirectory(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }
}
