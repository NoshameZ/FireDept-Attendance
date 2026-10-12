using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace FireDeptAttendance.Core;

public sealed class QuarterMinutesSummary
{
    public string QuarterLabel { get; set; } = "";
    public string Folder { get; set; } = "";
    public int TotalMeetings { get; set; }
    public int Exported { get; set; }
    public int Blank { get; set; }
    public int Skipped { get; set; }
    public List<string> Files { get; } = new();
}

/// <summary>Meeting-minutes export to Word (.docx) — port of <c>docx_export.py</c>.</summary>
public static class DocxExporter
{
    private const double SigWidthInches = 2.0;
    private const long EmuPerInch = 914400;

    public static void ExportMinutes(string path, List<List<RichRun>> header, List<List<RichRun>> body,
                                     List<List<RichRun>> footer, string? signaturePath = null, int basePt = 11)
    {
        var sig = !string.IsNullOrEmpty(signaturePath) && File.Exists(signaturePath) ? signaturePath : null;
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        AddStyles(main);
        var bodyEl = new Body();
        main.Document = new Document(bodyEl);
        var ctx = new Ctx(main, sig);

        bool placed = false;
        placed |= AddBlock(ctx, bodyEl, header, basePt);
        placed |= AddBlock(ctx, bodyEl, body, basePt);
        placed |= AddBlock(ctx, bodyEl, footer, basePt);
        if (sig != null && !placed)
        {
            var drawing = ctx.Picture();
            if (drawing != null) bodyEl.Append(new Paragraph(new Run(drawing)));
        }
        bodyEl.Append(new SectionProperties(
            new PageSize { Width = 12240, Height = 15840 },
            new PageMargin { Top = 1440, Right = 1440, Bottom = 1440, Left = 1440, Header = 720, Footer = 720, Gutter = 0 }));
        main.Document.Save();
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var sp = main.AddNewPart<StyleDefinitionsPart>();
        sp.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri", EastAsia = "Calibri" },
                    new FontSize { Val = "22" }, new FontSizeComplexScript { Val = "22" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle())
            { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });
        sp.Styles.Save();
    }

    private sealed class Ctx
    {
        private readonly MainDocumentPart _main;
        private readonly string? _sig;
        private string? _relId;
        private long _cx, _cy;
        private uint _id = 1;

        public Ctx(MainDocumentPart main, string? sig) { _main = main; _sig = sig; }

        public Drawing? Picture()
        {
            if (_sig is null) return null;
            try
            {
                if (_relId is null)
                {
                    var (w, h) = ImageInfo.GetSize(_sig);
                    var type = ImageInfo.PartType(_sig);
                    var part = _main.AddImagePart(type);
                    using (var fs = File.OpenRead(_sig)) part.FeedData(fs);
                    _relId = _main.GetIdOfPart(part);
                    _cx = (long)(SigWidthInches * EmuPerInch);
                    _cy = w > 0 && h > 0 ? (long)(_cx * (double)h / w) : _cx / 2;
                }
                var id = _id++;
                return new Drawing(new DW.Inline(
                    new DW.Extent { Cx = _cx, Cy = _cy },
                    new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                    new DW.DocProperties { Id = id, Name = "Signature " + id },
                    new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                    new A.Graphic(new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = id, Name = Path.GetFileName(_sig) },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(new A.Blip { Embed = _relId }, new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = _cx, Cy = _cy }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
                { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 });
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>A run whose tab characters become &lt;w:tab/&gt; elements (Word ignores raw tabs inside &lt;w:t&gt;).</summary>
    internal static Run MakeRun(RunProperties rp, string text)
    {
        var r = new Run(rp);
        var parts = text.Replace("\r", "").Split('\t');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) r.Append(new TabChar());
            if (parts[i].Length > 0) r.Append(new Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve });
        }
        return r;
    }

    private static bool AddBlock(Ctx ctx, Body body, List<List<RichRun>> paras, int basePt)
    {
        bool placed = false;
        foreach (var para in paras)
        {
            var p = new Paragraph();
            foreach (var run in para)
            {
                if (run.IsImage)
                {
                    var d = ctx.Picture();
                    if (d != null) { p.Append(new Run(d)); placed = true; }
                    continue;
                }
                if (run.Text.Length == 0) continue;
                var rp = new RunProperties();
                if (run.Bold) rp.Append(new Bold());
                if (run.Italic) rp.Append(new Italic());
                if (run.Underline) rp.Append(new Underline { Val = UnderlineValues.Single });
                rp.Append(new FontSize { Val = (basePt * 2).ToString() });
                p.Append(MakeRun(rp, run.Text));
            }
            body.Append(p);
        }
        return placed;
    }

    // ------------------------------------------------------------------ //
    // Batch export
    // ------------------------------------------------------------------ //
    private const string IllegalChars = "<>:\"/\\|?*";

    public static string SafeFileName(string name)
    {
        var chars = name.Select(ch => IllegalChars.Contains(ch) || ch < 32 ? '-' : ch).ToArray();
        var outp = new string(chars).Trim().TrimEnd('.', ' ');
        return outp.Length > 0 ? outp : "Meeting Minutes";
    }

    public static string UniquePath(string dir, string baseName, string ext, HashSet<string> used)
    {
        var candidate = baseName + ext;
        int n = 2;
        while (used.Contains(candidate.ToLowerInvariant()) || File.Exists(Path.Combine(dir, candidate)))
            candidate = $"{baseName} ({n++}){ext}";
        used.Add(candidate.ToLowerInvariant());
        return Path.Combine(dir, candidate);
    }

    public static QuarterMinutesSummary ExportQuarterMinutes(Database db, long quarterId, string outDir,
        string headerTmpl, string footerTmpl, string? signaturePath, int basePt = 11, bool skipEmpty = false,
        bool makeSubfolder = true)
    {
        var q = db.GetQuarter(quarterId) ?? throw new ValidationException("Quarter not found.");
        var label = $"Q{q.Number} {q.Year}";
        var meetings = db.GetEvents(quarterId).Where(e => e.IsMeeting).ToList();
        var summary = new QuarterMinutesSummary { QuarterLabel = label, Folder = outDir, TotalMeetings = meetings.Count };
        if (meetings.Count == 0) return summary;

        var folder = makeSubfolder ? Path.Combine(outDir, SafeFileName($"Minutes {label}")) : outDir;
        Directory.CreateDirectory(folder);
        summary.Folder = folder;

        var headerParsed = RichTextModel.Parse(headerTmpl);
        var footerParsed = RichTextModel.Parse(footerTmpl);
        var used = new HashSet<string>();
        int idx = 0;
        foreach (var ev in meetings)
        {
            idx++;
            var attendees = db.CountEventAttendees(ev.Id);
            var stored = db.GetEventMinutes(ev.Id);
            var has = !RichTextModel.IsEmpty(stored);
            if (skipEmpty && !has) { summary.Skipped++; continue; }
            var map = RichTextModel.Placeholders(attendees, ev.EventDate, ev.Location);
            var hdr = RichTextModel.Substitute(headerParsed, map);
            var ftr = RichTextModel.Substitute(footerParsed, map);
            List<List<RichRun>> body;
            if (has) body = RichTextModel.Parse(stored);
            else
            {
                body = new() { new() { new RichRun { Text = "No minutes recorded.", Italic = true } } };
                summary.Blank++;
            }
            var baseName = SafeFileName(!string.IsNullOrEmpty(ev.EventDate)
                ? $"{ev.EventDate} Meeting Minutes" : $"{label} - Meeting {idx}");
            var path = UniquePath(folder, baseName, ".docx", used);
            ExportMinutes(path, hdr, body, ftr, signaturePath, basePt);
            summary.Exported++;
            summary.Files.Add(Path.GetFileName(path));
        }
        return summary;
    }
}

/// <summary>Reads pixel dimensions from PNG/JPEG/GIF/BMP headers (no System.Drawing).</summary>
public static class ImageInfo
{
    public static PartTypeInfo PartType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ImagePartType.Jpeg,
        ".gif" => ImagePartType.Gif,
        ".bmp" => ImagePartType.Bmp,
        _ => ImagePartType.Png,
    };

    public static (int W, int H) GetSize(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 'P')
            return (BE32(b, 16), BE32(b, 20));
        if (b.Length > 10 && b[0] == 'G' && b[1] == 'I')
            return (b[6] | b[7] << 8, b[8] | b[9] << 8);
        if (b.Length > 26 && b[0] == 'B' && b[1] == 'M')
            return (BitConverter.ToInt32(b, 18), Math.Abs(BitConverter.ToInt32(b, 22)));
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                int len = (b[i + 2] << 8) | b[i + 3];
                i += 2 + len;
            }
        }
        return (0, 0);
    }

    private static int BE32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
}
