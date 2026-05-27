using System.IO;
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

    public AppState Load()
    {
        Directory.CreateDirectory(FolderPath);
        if (!File.Exists(StatePath))
        {
            return new AppState();
        }

        try
        {
            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<AppState>(json, JsonOptions) ?? new AppState();
        }
        catch
        {
            return new AppState();
        }
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(FolderPath);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(StatePath, json);
    }
}
