using System.Text.Json;
using System.Text.Json.Nodes;

namespace FireDeptAttendance.Core;

/// <summary>One formatted run of text, or the signature image marker.</summary>
public sealed class RichRun
{
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    /// <summary>True when this run is the inline signature image (<c>{"img":"signature"}</c>).</summary>
    public bool IsImage { get; set; }

    public RichRun Clone() => (RichRun)MemberwiseClone();
}

/// <summary>
/// The "rich-v1" JSON format shared with the Python version:
/// <c>{"format":"rich-v1","paras":[[{"t":"text","b":0,"i":0,"u":0},{"img":"signature"}],...]}</c>.
/// Plain strings (legacy data) are split on newlines.
/// </summary>
public static class RichTextModel
{
    public const string Format = "rich-v1";

    public static List<List<RichRun>> Parse(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return new() { new() };
        var s = stored.Trim();
        if (s.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(stored) is JsonObject o && (string?)o["format"] == Format)
                {
                    var result = new List<List<RichRun>>();
                    if (o["paras"] is JsonArray paras)
                        foreach (var p in paras)
                        {
                            var para = new List<RichRun>();
                            if (p is JsonArray runs)
                                foreach (var r in runs.OfType<JsonObject>())
                                    para.Add(ReadRun(r));
                            result.Add(para);
                        }
                    return result.Count > 0 ? result : new() { new() };
                }
            }
            catch (JsonException) { /* fall through to plain text */ }
        }
        return stored.Split('\n')
            .Select(line => line.Length > 0 ? new List<RichRun> { new() { Text = line } } : new List<RichRun>())
            .ToList();
    }

    private static bool Flag(JsonNode? n)
    {
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<double>(out var d)) return d != 0;
        if (v.TryGetValue<string>(out var s)) return s.Length > 0;
        return false;
    }

    private static RichRun ReadRun(JsonObject r)
    {
        if (r["img"] is JsonNode img && Flag(img)) return new RichRun { IsImage = true };
        return new RichRun
        {
            Text = r["t"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : "",
            Bold = Flag(r["b"]),
            Italic = Flag(r["i"]),
            Underline = Flag(r["u"]),
        };
    }

    public static string Encode(List<List<RichRun>>? paras)
    {
        paras ??= new() { new() };
        if (paras.Count == 0) paras.Add(new());
        var arr = new JsonArray();
        foreach (var p in paras)
        {
            var pa = new JsonArray();
            foreach (var r in p)
            {
                if (r.IsImage) pa.Add(new JsonObject { ["img"] = "signature" });
                else pa.Add(new JsonObject
                {
                    ["t"] = r.Text, ["b"] = r.Bold ? 1 : 0, ["i"] = r.Italic ? 1 : 0, ["u"] = r.Underline ? 1 : 0,
                });
            }
            arr.Add(pa);
        }
        // Match Python json.dumps separators ("format": ..., ) closely; content is what matters.
        return new JsonObject { ["format"] = Format, ["paras"] = arr }.ToJsonString();
    }

    /// <summary>True if there is no visible text (signature image ignored).</summary>
    public static bool IsEmpty(string? stored) =>
        !Parse(stored).Any(p => p.Any(r => !r.IsImage && r.Text.Trim().Length > 0));

    public static bool HasSignature(string? stored) => Parse(stored).Any(p => p.Any(r => r.IsImage));

    public static List<List<RichRun>> Substitute(List<List<RichRun>> paras, IDictionary<string, string> map)
    {
        var outp = new List<List<RichRun>>();
        foreach (var p in paras.Count > 0 ? paras : new() { new() })
        {
            var np = new List<RichRun>();
            foreach (var r in p)
            {
                var c = r.Clone();
                if (!c.IsImage)
                    foreach (var (k, v) in map) c.Text = c.Text.Replace(k, v);
                np.Add(c);
            }
            outp.Add(np);
        }
        return outp;
    }

    public static string ToPlain(List<List<RichRun>> paras) =>
        string.Join("\n", (paras.Count > 0 ? paras : new() { new() })
            .Select(p => string.Concat(p.Where(r => !r.IsImage).Select(r => r.Text))));

    public static Dictionary<string, string> Placeholders(long attendees, string dateIso, string location) => new()
    {
        ["{attendees}"] = attendees.ToString(),
        ["{date}"] = string.IsNullOrEmpty(dateIso) ? "" : DateUtil.IsoToUs(dateIso),
        ["{location}"] = location ?? "",
    };
}
