using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeGlide;

internal sealed class ConfigFile
{
    [JsonPropertyName("suspendWhenFullscreen")]
    public bool? SuspendWhenFullscreen { get; set; }

    [JsonPropertyName("blocklist")]
    public List<string?>? Blocklist { get; set; }
}

[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(ConfigFile))]
internal sealed partial class ConfigJson : JsonSerializerContext
{
}

/// <summary>%APPDATA%\EdgeGlide\config.json: created on first run, reloaded whenever it is saved.</summary>
internal sealed class ConfigStore : IDisposable
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EdgeGlide");

    public static string FilePath { get; } = Path.Combine(Folder, "config.json");

    const string Header = """
        // EdgeGlide settings. Easiest to change from the tray icon (Settings), or edit here:
        // changes apply as soon as you save.
        //
        // "suspendWhenFullscreen": true (the default) keeps EdgeGlide switched off while the app in
        // front is fullscreen or borderless-fullscreen. With false it keeps working over fullscreen
        // apps; it still switches off whenever an app hides or confines the cursor.
        //
        // "blocklist": programs (exe file names, not case-sensitive) that keep EdgeGlide
        // suspended while they are the foreground window. EdgeGlide already suspends itself
        // whenever an app hides or confines the cursor (and for fullscreen apps, see above),
        // so this is only for what those checks miss, e.g. a windowed game that leaves the
        // cursor visible. Find exact names in Task Manager > Details.

        """;

    const string DefaultText = Header + """
        {
          "suspendWhenFullscreen": true,
          "blocklist": [
            // "SomeGame.exe",
          ]
        }

        """;

    FileSystemWatcher? _watcher;

    public IReadOnlySet<string> Blocklist { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stay switched off while the app in front is fullscreen. On unless the config says otherwise.</summary>
    public bool SuspendWhenFullscreen { get; private set; } = true;

    /// <summary>Returns null on success, otherwise an error message (and keeps the previous settings).</summary>
    public string? Load()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (!File.Exists(FilePath)) File.WriteAllText(FilePath, DefaultText);
            var file = JsonSerializer.Deserialize(ReadShared(FilePath), ConfigJson.Default.ConfigFile) ?? new ConfigFile();
            Blocklist = Normalize(file.Blocklist);
            SuspendWhenFullscreen = file.SuspendWhenFullscreen ?? true;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ex.Message;
        }
    }

    // The settings window saves through these. Each returns null on success, otherwise an error message.
    public string? SaveBlocklist(IEnumerable<string> names) => Write(Normalize(names), SuspendWhenFullscreen);
    public string? SaveSuspendWhenFullscreen(bool suspend) => Write(Blocklist, suspend);

    string? Write(IEnumerable<string> names, bool suspendWhenFullscreen)
    {
        var list = names.Order(StringComparer.OrdinalIgnoreCase).ToList();
        var json = new StringBuilder(Header)
            .Append("{\n  \"suspendWhenFullscreen\": ").Append(suspendWhenFullscreen ? "true" : "false")
            .Append(",\n  \"blocklist\": [");
        for (int i = 0; i < list.Count; i++)
            json.Append(i == 0 ? "\n" : ",\n").Append("    \"").Append(JsonEncodedText.Encode(list[i])).Append('"');
        json.Append(list.Count > 0 ? "\n  ]\n}\n" : "]\n}\n");
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, json.ToString());
            Blocklist = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            SuspendWhenFullscreen = suspendWhenFullscreen;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>"C:\Games\Foo.exe", " foo " and "foo.exe" all become "foo.exe"; blank stays blank.</summary>
    public static string NormalizeName(string? raw)
    {
        string cleaned = new string((raw ?? "").Where(c => !char.IsControl(c)).ToArray());   // no forged log lines
        string name = Path.GetFileName(cleaned.Trim().Trim('"'));
        if (name.Length == 0 || name.Length > 255) return "";
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    public void Watch(Action changed)
    {
        Directory.CreateDirectory(Folder);
        _watcher = new FileSystemWatcher(Folder, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Changed += (_, _) => changed();
        _watcher.Created += (_, _) => changed();
        _watcher.Renamed += (_, _) => changed();   // editors that save via a temp file + rename
        _watcher.EnableRaisingEvents = true;
    }

    public void OpenInEditor()
    {
        if (!File.Exists(FilePath)) Load();
        // Full path: a bare "notepad.exe" would be searched for in EdgeGlide's own (user-writable) folder first.
        string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        using var _ = Process.Start(new ProcessStartInfo(notepad) { ArgumentList = { FilePath } });
    }

    public void Dispose() => _watcher?.Dispose();

    static HashSet<string> Normalize(IEnumerable<string?>? names)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names ?? [])
        {
            string name = NormalizeName(raw);
            if (name.Length > 0) set.Add(name);
        }
        return set;
    }

    // The editor may still have the file open; read with full sharing and retry briefly.
    static string ReadShared(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(50);
            }
        }
    }
}
