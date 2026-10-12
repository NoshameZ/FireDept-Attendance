using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FireDeptAttendance.Core;

/// <summary>
/// User preferences persisted to <c>settings.json</c> (same file format as the
/// Python version, so an existing file is read as-is). Unknown keys are kept.
/// </summary>
public sealed class AppSettings
{
    public const string FileName = "settings.json";
    public static readonly string[] FontSizes = { "Small", "Medium", "Large", "Extra Large" };
    public static readonly string[] AccentPresets =
        { "#B22222", "#1d4ed8", "#0f9d58", "#7c3aed", "#ea580c", "#0891b2", "#db2777", "#475569" };

    public const string DefaultHeader =
        "MEETING MINUTES\nDate: {date}\nLocation: {location}\nMembers in attendance: {attendees}\n" +
        "----------------------------------------\n";
    public const string DefaultFooter =
        "----------------------------------------\nRespectfully submitted,\nSecretary";

    public string Theme { get; set; } = "light";
    public string Accent { get; set; } = "#B22222";
    public string FontSize { get; set; } = "Medium";
    public string FontFamily { get; set; } = "Segoe UI";
    public double RateOutOfTown { get; set; }
    public double RateInTown { get; set; }
    public double RateMeetings { get; set; }
    public string DefaultMeetingLocation { get; set; } = "";
    public Dictionary<string, string> CallLocations { get; set; } = new();
    public string MinutesHeader { get; set; } = DefaultHeader;
    public string MinutesFooter { get; set; } = DefaultFooter;
    public string Signature { get; set; } = "";
    public List<string> UserWords { get; set; } = new();

    // Folder preferences (stored under "paths"; kept by ResetToDefaults and by imports).
    public string LastImportFile { get; set; } = "";
    public string ExportExcelDir { get; set; } = "";
    public string ExportMinutesDir { get; set; } = "";
    public string ExportSingleMinutesDir { get; set; } = "";
    /// <summary>User chose "Start empty" at the first-run import prompt; don't ask again.</summary>
    public bool SkipImportPrompt { get; set; }

    public string LastImportDir =>
        string.IsNullOrEmpty(LastImportFile) ? "" : Path.GetDirectoryName(LastImportFile) ?? "";

    public static string DocumentsDir => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>A configured folder if it still exists, else Documents.</summary>
    public static string ResolveDir(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured!;
        var docs = DocumentsDir;
        return string.IsNullOrEmpty(docs) ? Environment.CurrentDirectory : docs;
    }

    /// <summary>Top-level keys we do not manage, preserved on save.</summary>
    private JsonObject _extra = new();

    private string? _directory;

    public static double ScalingFor(string size) => size switch
    {
        "Small" => 1.0,
        "Large" => 1.4,
        "Extra Large" => 1.65,
        _ => 1.18,
    };

    public static bool IsHex(string? v)
    {
        if (v is null) return false;
        var h = v.TrimStart('#');
        return (h.Length == 3 || h.Length == 6) && h.All(Uri.IsHexDigit);
    }

    public static AppSettings Load(string directory)
    {
        var s = new AppSettings { _directory = directory };
        JsonObject? root = null;
        try
        {
            var path = Path.Combine(directory, FileName);
            if (File.Exists(path)) root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception) { root = null; }
        root ??= new JsonObject();

        // Python treated falsy stored values as "use default".
        static string? GetStr(JsonObject o, string k) =>
            o[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

        s.Theme = GetStr(root, "theme") ?? "light";
        if (s.Theme != "light" && s.Theme != "dark") s.Theme = "light";
        s.FontSize = GetStr(root, "font_size") ?? "Medium";
        if (!FontSizes.Contains(s.FontSize)) s.FontSize = "Medium";
        s.Accent = GetStr(root, "accent") ?? "#B22222";
        if (!IsHex(s.Accent)) s.Accent = "#B22222";
        s.FontFamily = GetStr(root, "font_family") ?? "Segoe UI";

        if (root["rates"] is JsonObject rates)
        {
            s.RateOutOfTown = ReadRate(rates, "out_of_town");
            s.RateInTown = ReadRate(rates, "in_town");
            s.RateMeetings = ReadRate(rates, "meetings");
        }

        if (root["defaults"] is JsonObject d)
        {
            s.DefaultMeetingLocation = (GetStr(d, "meeting_location") ?? "").Trim();
            if (d["call_locations"] is JsonObject cl)
                foreach (var (k, v) in cl)
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var vs) && k.Trim().Length > 0 &&
                        vs.Trim().Length > 0)
                        s.CallLocations[k.Trim()] = vs.Trim();
        }

        if (root["minutes"] is JsonObject m)
        {
            if (m["header"] is JsonValue hv && hv.TryGetValue<string>(out var h)) s.MinutesHeader = h;
            if (m["footer"] is JsonValue fv && fv.TryGetValue<string>(out var f)) s.MinutesFooter = f;
            if (m["signature"] is JsonValue sv && sv.TryGetValue<string>(out var sig)) s.Signature = sig;
            if (m["user_words"] is JsonArray uw)
                foreach (var w in uw)
                    if (w is JsonValue wv && wv.TryGetValue<string>(out var ws)) s.UserWords.Add(ws);
        }

        if (root["paths"] is JsonObject pa)
        {
            s.LastImportFile = GetStr(pa, "last_import_file") ?? "";
            s.ExportExcelDir = GetStr(pa, "export_excel_dir") ?? "";
            s.ExportMinutesDir = GetStr(pa, "export_minutes_dir") ?? "";
            s.ExportSingleMinutesDir = GetStr(pa, "export_single_minutes_dir") ?? "";
            s.SkipImportPrompt = pa["skip_import_prompt"] is JsonValue sk && sk.TryGetValue<bool>(out var b) && b;
        }

        foreach (var (k, v) in root)
            if (!Known.Contains(k)) s._extra[k] = v?.DeepClone();
        return s;
    }

    private static readonly HashSet<string> Known =
        new() { "theme", "accent", "font_size", "font_family", "rates", "defaults", "minutes", "paths" };

    private static double ReadRate(JsonObject o, string key)
    {
        try
        {
            var n = o[key];
            if (n is null) return 0;
            double v = n.GetValueKind() == JsonValueKind.String
                ? double.Parse(n.GetValue<string>(), CultureInfo.InvariantCulture)
                : n.GetValue<double>();
            return double.IsFinite(v) ? Math.Max(0, v) : 0;
        }
        catch (Exception) { return 0; }
    }

    public JsonObject ToJson()
    {
        var root = new JsonObject
        {
            ["theme"] = Theme,
            ["accent"] = Accent,
            ["font_size"] = FontSize,
            ["font_family"] = FontFamily,
            ["rates"] = new JsonObject
            {
                ["out_of_town"] = RateOutOfTown, ["in_town"] = RateInTown, ["meetings"] = RateMeetings,
            },
            ["defaults"] = new JsonObject
            {
                ["meeting_location"] = DefaultMeetingLocation,
                ["call_locations"] = new JsonObject(CallLocations.Select(kv =>
                    new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
            },
            ["minutes"] = new JsonObject
            {
                ["header"] = MinutesHeader,
                ["footer"] = MinutesFooter,
                ["signature"] = Signature,
                ["user_words"] = new JsonArray(UserWords.Select(w => (JsonNode?)w).ToArray()),
            },
            ["paths"] = new JsonObject
            {
                ["last_import_file"] = LastImportFile,
                ["export_excel_dir"] = ExportExcelDir,
                ["export_minutes_dir"] = ExportMinutesDir,
                ["export_single_minutes_dir"] = ExportSingleMinutesDir,
                ["skip_import_prompt"] = SkipImportPrompt,
            },
        };
        foreach (var (k, v) in _extra) root[k] = v?.DeepClone();
        return root;
    }

    public bool Save()
    {
        if (_directory is null) return false;
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, FileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson().ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Reset appearance + everything else to factory defaults (keeps directory and folder preferences).</summary>
    public void ResetToDefaults()
    {
        var dir = _directory;
        var fresh = new AppSettings();
        Theme = fresh.Theme; Accent = fresh.Accent; FontSize = fresh.FontSize; FontFamily = fresh.FontFamily;
        RateOutOfTown = RateInTown = RateMeetings = 0;
        DefaultMeetingLocation = ""; CallLocations = new();
        MinutesHeader = DefaultHeader; MinutesFooter = DefaultFooter;
        Signature = ""; UserWords = new();
        _directory = dir;
    }

    public string? Directory_ => _directory;

    /// <summary>Absolute path of the signature image if configured and present, else "".</summary>
    public string SignaturePath()
    {
        if (string.IsNullOrEmpty(Signature) || _directory is null) return "";
        var p = Path.IsPathRooted(Signature) ? Signature : Path.Combine(_directory, Signature);
        return File.Exists(p) ? p : "";
    }

    /// <summary>Copy the chosen image into the data folder as signature.ext. Returns stored path or "".</summary>
    public string SetSignature(string srcPath)
    {
        if (_directory is null || string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath)) return "";
        var ext = Path.GetExtension(srcPath).ToLowerInvariant();
        if (ext.Length == 0) ext = ".png";
        var destName = "signature" + ext;
        var dest = Path.Combine(_directory, destName);
        try
        {
            if (!string.Equals(Path.GetFullPath(srcPath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(srcPath, dest, overwrite: true);
        }
        catch (Exception) { return ""; }
        var old = Signature;
        if (!string.IsNullOrEmpty(old) && old != destName)
        {
            var oldPath = Path.IsPathRooted(old) ? old : Path.Combine(_directory, old);
            try
            {
                if (File.Exists(oldPath) && !string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(dest),
                        StringComparison.OrdinalIgnoreCase))
                    File.Delete(oldPath);
            }
            catch (Exception) { /* ignore */ }
        }
        Signature = destName;
        Save();
        return dest;
    }

    public void ClearSignature()
    {
        if (!string.IsNullOrEmpty(Signature) && _directory is not null)
        {
            var p = Path.IsPathRooted(Signature) ? Signature : Path.Combine(_directory, Signature);
            try { if (File.Exists(p)) File.Delete(p); } catch (Exception) { /* ignore */ }
        }
        Signature = "";
        Save();
    }

    public void AddUserWord(string word)
    {
        word = (word ?? "").Trim();
        if (word.Length == 0) return;
        if (!UserWords.Contains(word)) UserWords.Add(word);
        Save();
    }
}
