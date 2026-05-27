using System.IO;
using RaycastPM.Models;

namespace RaycastPM.Services;

public sealed class AppScanner
{
    private static readonly string[] LaunchableExtensions = [".exe", ".lnk", ".appref-ms"];

    public Task<IReadOnlyList<InstalledApp>> ScanAsync(IEnumerable<string> customDirectories)
    {
        return Task.Run(() => Scan(customDirectories));
    }

    private static IReadOnlyList<InstalledApp> Scan(IEnumerable<string> customDirectories)
    {
        var roots = DefaultRoots()
            .Concat(customDirectories.Select(Environment.ExpandEnvironmentVariables))
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
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
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> directories;
            IEnumerable<string> files;

            try
            {
                directories = Directory.EnumerateDirectories(current);
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories)
            {
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
}
