using System.IO;
using System.Text.RegularExpressions;
using RaycastPM.Models;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace RaycastPM.Services;

internal static class ClipboardClassifier
{
    private static readonly Regex DomainLikeLinkPattern = new(
        @"^(?:www\.)?[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+(?:/[^\s]*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HexColorPattern = new(
        @"^(?:#|0x)(?:[0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CssColorFunctionPattern = new(
        @"^(?:rgb|rgba|hsl|hsla)\(\s*[^()]+\s*\)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ClipboardItemKind ClassifyText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return ClipboardItemKind.Text;
        }

        if (ExtractExistingFilePaths(trimmed).Count > 0)
        {
            return ClipboardItemKind.File;
        }

        if (IsColor(trimmed))
        {
            return ClipboardItemKind.Color;
        }

        return IsLink(trimmed)
            ? ClipboardItemKind.Link
            : ClipboardItemKind.Text;
    }

    public static string NormalizeFileContent(IEnumerable<string> paths)
    {
        return string.Join(
            Environment.NewLine,
            paths.Select(NormalizePathText)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> ExtractExistingFilePaths(string content)
    {
        var paths = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePathText)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        if (paths.Length == 0)
        {
            return [];
        }

        return paths.All(path => File.Exists(path) || Directory.Exists(path))
            ? paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
    }

    private static string NormalizePathText(string path)
    {
        return path.Trim().Trim('"');
    }

    private static bool IsLink(string text)
    {
        if (text.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return uri.Scheme is "http" or "https" or "ftp" or "file" or "mailto";
        }

        return DomainLikeLinkPattern.IsMatch(text);
    }

    private static bool IsColor(string text)
    {
        if (text.Length is < 3 or > 64 || text.Contains('\r') || text.Contains('\n'))
        {
            return false;
        }

        if (HexColorPattern.IsMatch(text) || CssColorFunctionPattern.IsMatch(text))
        {
            return true;
        }

        try
        {
            return MediaColorConverter.ConvertFromString(text) is MediaColor;
        }
        catch
        {
            return false;
        }
    }
}
