using System.IO;
using System.Text;
using System.Text.Json;
using RaycastPM.Models;

namespace RaycastPM.Services;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string FolderPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RaycastPM");

    private string StatePath => Path.Combine(FolderPath, "state.json");
    private string StateBackupPath => Path.Combine(FolderPath, "state.bak");

    public AppState Load()
    {
        Directory.CreateDirectory(FolderPath);
        var state = TryLoad(StatePath)
            ?? TryLoad(StateBackupPath)
            ?? new AppState();
        return NormalizeState(state);
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(FolderPath);
        var tempPath = $"{StatePath}.tmp";

        try
        {
            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(tempPath, json, Encoding.UTF8);

            if (File.Exists(StatePath))
            {
                File.Copy(StatePath, StateBackupPath, true);
            }

            File.Move(tempPath, StatePath, true);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, "state save");
            TryDelete(tempPath);
        }
    }

    private static AppState? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppState>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogException(ex, $"state load {Path.GetFileName(path)}");
            return null;
        }
    }

    private static AppState NormalizeState(AppState? state)
    {
        state ??= new AppState();
        state.Settings ??= new AppSettings();
        state.Settings.CustomAppDirectories ??= [];
        state.Settings.LogDirectory = AppDiagnostics.ResolveLogDirectory(state.Settings.LogDirectory);
        state.Settings.ExchangeRatesToCny = NormalizeRates(state.Settings.ExchangeRatesToCny);
        if (!double.IsFinite(state.Settings.NoteFontSize) || state.Settings.NoteFontSize <= 0)
        {
            state.Settings.NoteFontSize = 18;
        }

        state.AppLaunchCounts = state.AppLaunchCounts is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(
                state.AppLaunchCounts.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value > 0),
                StringComparer.OrdinalIgnoreCase);
        state.ClipboardItems = state.ClipboardItems?
            .Where(item => item is not null)
            .Select(NormalizeClipboardEntry)
            .Take(120)
            .ToList() ?? [];
        state.Notes = state.Notes?
            .Where(item => item is not null)
            .Select(NormalizeNote)
            .ToList() ?? [];
        state.PlanItems = state.PlanItems?
            .Where(item => item is not null)
            .Select(NormalizePlanItem)
            .ToList() ?? [];
        return state;
    }

    private static NoteItem NormalizeNote(NoteItem note)
    {
        note.Images ??= [];
        for (var index = note.Images.Count - 1; index >= 0; index--)
        {
            if (note.Images[index].ImageBytes.Length == 0)
            {
                note.Images.RemoveAt(index);
            }
        }

        return note;
    }

    private static ClipboardEntry NormalizeClipboardEntry(ClipboardEntry item)
    {
        if (item.Kind == ClipboardItemKind.Text && !string.IsNullOrWhiteSpace(item.Content))
        {
            item.Kind = ClipboardClassifier.ClassifyText(item.Content);
        }

        if (item.Kind != ClipboardItemKind.Image)
        {
            item.ImageBytes = null;
        }

        return item;
    }

    private static PlanItem NormalizePlanItem(PlanItem item)
    {
        item.Title = string.IsNullOrWhiteSpace(item.Title) ? "新计划" : item.Title.Trim();
        item.Description ??= string.Empty;
        item.Images ??= [];
        for (var index = item.Images.Count - 1; index >= 0; index--)
        {
            if (item.Images[index].ImageBytes.Length == 0)
            {
                item.Images.RemoveAt(index);
            }
        }

        if (item.StartAt == default)
        {
            item.StartAt = item.CreatedAt.LocalDateTime;
        }

        return item;
    }

    private static Dictionary<string, double> NormalizeRates(Dictionary<string, double>? rates)
    {
        var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["CNY"] = 1
        };

        if (rates is null)
        {
            normalized["USD"] = 6.8;
            return normalized;
        }

        foreach (var pair in rates)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && double.IsFinite(pair.Value) && pair.Value > 0)
            {
                normalized[pair.Key.ToUpperInvariant()] = pair.Value;
            }
        }

        normalized["CNY"] = 1;
        if (normalized.Count == 1)
        {
            normalized["USD"] = 6.8;
        }

        return normalized;
    }

    private static void TryDelete(string path)
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
}
