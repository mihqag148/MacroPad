using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LumiPad.App;

public sealed class ActionScriptStep
{
    public string Type { get; set; } = "Delay";
    public string Value { get; set; } = "";

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Value) ? Type : $"{Type} · {Value}";
}

public sealed class ActionScriptDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int ActionId { get; set; }
    public string Name { get; set; } = "New Script";
    public List<ActionScriptStep> Steps { get; set; } = [];

    public override string ToString() =>
        ActionId > 0 ? $"#{ActionId:00}  {Name}" : Name;
}

public static class ActionScriptStore
{
    private static string ScriptsFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "action_scripts.json");

    public static List<ActionScriptDefinition> Load()
    {
        try
        {
            if (!File.Exists(ScriptsFilePath))
                return [];

            var scripts =
                JsonSerializer.Deserialize<List<ActionScriptDefinition>>(
                    File.ReadAllText(ScriptsFilePath)) ?? [];

            NormalizeActionIds(scripts);
            return scripts;
        }
        catch
        {
            return [];
        }
    }

    public static void NormalizeActionIds(
        IList<ActionScriptDefinition> scripts)
    {
        var used = new HashSet<int>();

        foreach (var script in scripts)
        {
            script.Steps ??= [];

            if (script.ActionId is >= 1 and <= 32 &&
                used.Add(script.ActionId))
            {
                continue;
            }

            for (int id = 1; id <= 32; id++)
            {
                if (!used.Add(id))
                    continue;

                script.ActionId = id;
                break;
            }
        }
    }

    public static int NextAvailableActionId(
        IEnumerable<ActionScriptDefinition> scripts)
    {
        var used = scripts
            .Where(s => s.ActionId is >= 1 and <= 32)
            .Select(s => s.ActionId)
            .ToHashSet();

        for (int id = 1; id <= 32; id++)
        {
            if (!used.Contains(id))
                return id;
        }

        return 0;
    }

    public static void Save(IEnumerable<ActionScriptDefinition> scripts)
    {
        try
        {
            var list = scripts.ToList();
            NormalizeActionIds(list);

            string? folder = Path.GetDirectoryName(ScriptsFilePath);
            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllText(
                ScriptsFilePath,
                JsonSerializer.Serialize(
                    list,
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}

public static class ActionScriptEngine
{
    private const uint KeyeventfKeyup = 0x0002;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfUnicode = 0x0004;

    public static async Task ExecuteAsync(
        ActionScriptDefinition script,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        foreach (ActionScriptStep step in script.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string type = step.Type.Trim();
            string value = step.Value ?? "";

            progress?.Invoke(step.ToString());

            if (type.Equals("Run", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    Process.Start(new ProcessStartInfo(value)
                    {
                        UseShellExecute = true
                    });
                }
            }
            else if (type.Equals("Keys", StringComparison.OrdinalIgnoreCase))
            {
                SendChord(value);
            }
            else if (type.Equals("Text", StringComparison.OrdinalIgnoreCase))
            {
                SendUnicodeText(value);
            }
            else if (type.Equals("Delay", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, out int delayMs))
                    delayMs = 100;

                await Task.Delay(
                    Math.Clamp(delayMs, 0, 600_000),
                    cancellationToken);
            }
            else if (type.Equals("Media", StringComparison.OrdinalIgnoreCase))
            {
                SendMediaKey(value);
            }
        }
    }

    private static void SendChord(string chord)
    {
        string[] parts = chord.Split(
            '+',
            StringSplitOptions.TrimEntries |
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return;

        var modifiers = new List<byte>();
        byte key = 0;

        foreach (string part in parts)
        {
            if (TryModifier(part, out byte modifier))
            {
                modifiers.Add(modifier);
                continue;
            }

            if (TryVirtualKey(part, out byte parsed))
                key = parsed;
        }

        foreach (byte modifier in modifiers)
            KeyDown(modifier);

        if (key != 0)
        {
            KeyDown(key);
            KeyUp(key);
        }

        for (int i = modifiers.Count - 1; i >= 0; i--)
            KeyUp(modifiers[i]);
    }

    private static bool TryModifier(string token, out byte vk)
    {
        switch (token.Trim().ToUpperInvariant())
        {
            case "CTRL":
            case "CONTROL":
                vk = 0x11; return true;
            case "SHIFT":
                vk = 0x10; return true;
            case "ALT":
                vk = 0x12; return true;
            case "WIN":
            case "WINDOWS":
                vk = 0x5B; return true;
            default:
                vk = 0; return false;
        }
    }

    private static bool TryVirtualKey(string token, out byte vk)
    {
        string key = token.Trim().ToUpperInvariant();

        if (key.Length == 1)
        {
            char c = key[0];
            if (c is >= 'A' and <= 'Z')
            {
                vk = (byte)c;
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                vk = (byte)c;
                return true;
            }
        }

        if (key.StartsWith("F", StringComparison.Ordinal) &&
            int.TryParse(key[1..], out int f) &&
            f is >= 1 and <= 24)
        {
            vk = (byte)(0x70 + f - 1);
            return true;
        }

        var map = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        {
            ["ENTER"] = 0x0D,
            ["TAB"] = 0x09,
            ["ESC"] = 0x1B,
            ["ESCAPE"] = 0x1B,
            ["SPACE"] = 0x20,
            ["BACKSPACE"] = 0x08,
            ["DELETE"] = 0x2E,
            ["HOME"] = 0x24,
            ["END"] = 0x23,
            ["PGUP"] = 0x21,
            ["PAGEUP"] = 0x21,
            ["PGDN"] = 0x22,
            ["PAGEDOWN"] = 0x22,
            ["LEFT"] = 0x25,
            ["UP"] = 0x26,
            ["RIGHT"] = 0x27,
            ["DOWN"] = 0x28,
        };

        return map.TryGetValue(key, out vk);
    }

    private static void SendMediaKey(string value)
    {
        byte vk = value.Trim().ToUpperInvariant() switch
        {
            "PLAY" or "PLAYPAUSE" or "PLAY/PAUSE" => 0xB3,
            "NEXT" or "NEXTTRACK" => 0xB0,
            "PREV" or "PREVIOUS" or "PREVTRACK" => 0xB1,
            "STOP" => 0xB2,
            "MUTE" => 0xAD,
            "VOLUP" or "VOLUMEUP" => 0xAF,
            "VOLDOWN" or "VOLUMEDOWN" => 0xAE,
            _ => 0
        };

        if (vk == 0)
            return;

        KeyDown(vk);
        KeyUp(vk);
    }

    private static void SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var inputs = new INPUT[text.Length * 2];
        int index = 0;

        foreach (char ch in text)
        {
            inputs[index++] = new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KeyeventfUnicode
                    }
                }
            };

            inputs[index++] = new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KeyeventfUnicode | KeyeventfKeyup
                    }
                }
            };
        }

        _ = SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<INPUT>());
    }

    private static void KeyDown(byte vk) =>
        keybd_event(vk, 0, 0, UIntPtr.Zero);

    private static void KeyUp(byte vk) =>
        keybd_event(vk, 0, KeyeventfKeyup, UIntPtr.Zero);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint nInputs,
        INPUT[] pInputs,
        int cbSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte bVk,
        byte bScan,
        uint dwFlags,
        UIntPtr dwExtraInfo);
}
