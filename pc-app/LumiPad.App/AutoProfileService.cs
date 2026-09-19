using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LumiPad.App;

public sealed class AutoProfileMapping
{
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public int ProfileIndex { get; set; }
}

public sealed class AutoProfileSettings
{
    public bool Enabled { get; set; }
    public int DefaultProfile { get; set; }
    public List<AutoProfileMapping> Mappings { get; set; } = [];
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

            settings.DefaultProfile = Math.Clamp(settings.DefaultProfile, 0, 4);
            settings.Mappings ??= [];

            foreach (var mapping in settings.Mappings)
                mapping.ProfileIndex = Math.Clamp(mapping.ProfileIndex, 0, 4);

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
