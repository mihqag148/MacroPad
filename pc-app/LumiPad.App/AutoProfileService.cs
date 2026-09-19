using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LumiPad.App;

public sealed class AutoProfileMapping
{
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public int ProfileIndex { get; set; }
}

// Kept only so settings written by the short-lived preset UI can be migrated.
public sealed class AutoProfilePreset
{
    public string Name { get; set; } = "Profile";
    public int DefaultProfile { get; set; }
    public List<AutoProfileMapping> Mappings { get; set; } = [];
}

public sealed class AutoProfileSettings
{
    public bool Enabled { get; set; }
    public int DefaultProfile { get; set; }
    public List<AutoProfileMapping> Mappings { get; set; } = [];

    [JsonPropertyName("ActivePresetIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LegacyActivePresetIndex { get; set; }

    [JsonPropertyName("Presets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AutoProfilePreset>? LegacyPresets { get; set; }

    public void EnsureNormalized()
    {
        Mappings ??= [];

        // Migrate the previous "preset of mappings" format into the UI the
        // product actually wants: one Default row + up to ten app mappings.
        if (LegacyPresets is { Count: > 0 })
        {
            int index = Math.Clamp(
                LegacyActivePresetIndex,
                0,
                LegacyPresets.Count - 1);
            AutoProfilePreset selected = LegacyPresets[index];

            if (Mappings.Count == 0 && selected.Mappings is { Count: > 0 })
            {
                Mappings = selected.Mappings
                    .Select(CloneMapping)
                    .ToList();
            }

            if (DefaultProfile == 0 && selected.DefaultProfile != 0)
                DefaultProfile = selected.DefaultProfile;

            LegacyPresets = null;
            LegacyActivePresetIndex = 0;
        }

        DefaultProfile = Math.Clamp(DefaultProfile, 0, 4);

        var deduped = new List<AutoProfileMapping>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (AutoProfileMapping mapping in Mappings)
        {
            if (mapping is null ||
                string.IsNullOrWhiteSpace(mapping.ExecutablePath))
            {
                continue;
            }

            string key = mapping.ExecutablePath.Trim();
            if (!seen.Add(key))
                continue;

            mapping.Name =
                string.IsNullOrWhiteSpace(mapping.Name)
                    ? Path.GetFileNameWithoutExtension(key)
                    : mapping.Name.Trim();
            mapping.ExecutablePath = key;
            mapping.ProfileIndex =
                Math.Clamp(mapping.ProfileIndex, 0, 4);

            deduped.Add(mapping);
            if (deduped.Count >= 10)
                break;
        }

        Mappings = deduped;
    }

    private static AutoProfileMapping CloneMapping(
        AutoProfileMapping mapping) =>
        new()
        {
            Name = mapping.Name,
            ExecutablePath = mapping.ExecutablePath,
            ProfileIndex = mapping.ProfileIndex
        };
}

public sealed record RunningAppInfo(
    string Name,
    string ExecutablePath,
    int ProcessId,
    string WindowTitle);

public static class AutoProfileService
{
    private static string SettingsFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "auto_profiles.json");

    public static AutoProfileSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
                return new AutoProfileSettings();

            var settings = JsonSerializer.Deserialize<AutoProfileSettings>(
                File.ReadAllText(SettingsFilePath));

            if (settings is null)
                return new AutoProfileSettings();

            settings.EnsureNormalized();
            return settings;
        }
        catch
        {
            return new AutoProfileSettings();
        }
    }

    public static void Save(AutoProfileSettings settings)
    {
        try
        {
            settings.EnsureNormalized();

            string? folder = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllText(
                SettingsFilePath,
                JsonSerializer.Serialize(
                    settings,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    public static RunningAppInfo? GetForegroundApplication()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null;

        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
            return null;

        try
        {
            using Process process = Process.GetProcessById((int)pid);
            string? path = TryGetProcessPath(process);
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string name = FriendlyName(path, process.ProcessName);
            return new RunningAppInfo(
                name,
                path,
                process.Id,
                process.MainWindowTitle ?? "");
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<RunningAppInfo> ScanRunningApplications()
    {
        int self = Environment.ProcessId;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RunningAppInfo>();

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == self ||
                        process.MainWindowHandle == IntPtr.Zero ||
                        string.IsNullOrWhiteSpace(process.MainWindowTitle))
                    {
                        continue;
                    }

                    string? path = TryGetProcessPath(process);
                    if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
                        continue;

                    result.Add(new RunningAppInfo(
                        FriendlyName(path, process.ProcessName),
                        path,
                        process.Id,
                        process.MainWindowTitle));
                }
                catch
                {
                }
            }
        }

        return result
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(40)
            .ToArray();
    }

    public static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(a),
                Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    private static string FriendlyName(string path, string fallback)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
                return info.FileDescription.Trim();
        }
        catch
        {
        }

        string fileName = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(fileName) ? fallback : fileName;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint lpdwProcessId);
}
