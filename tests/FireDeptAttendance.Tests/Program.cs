// Headless self-test of FireDeptAttendance.Core (runs on Linux/Windows; no UI).
// Usage: dotnet run --project tests/FireDeptAttendance.Tests [path-to-existing-fire_dept.db]
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using FireDeptAttendance.Core;

if (args.Length == 4 && args[0] == "--export")   // --export <db> <quarterId> <out.xlsx>
{
    using var edb = new Database(args[1]);
    ExcelExporter.ExportQuarterWorkbook(edb, args[3], new List<long> { long.Parse(args[2]) }, false, true);
    return 0;
}

int failures = 0, passed = 0;
void Check(bool cond, string name)
{
    if (cond) { passed++; Console.WriteLine($"  PASS  {name}"); }
    else { failures++; Console.WriteLine($"  FAIL  {name}"); }
}
void Throws<T>(Action a, string name) where T : Exception
{
    try { a(); Check(false, name + " (no exception)"); }
    catch (T) { Check(true, name); }
    catch (Exception e) { Check(false, $"{name} (wrong exception {e.GetType().Name}: {e.Message})"); }
}

var work = Path.Combine(Path.GetTempPath(), "fdtest_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
Console.WriteLine($"Work dir: {work}");

// ---- legacy migration -------------------------------------------------------
var legacy = Path.Combine(work, "legacy");
Directory.CreateDirectory(legacy);
var srcDb = args.Length > 0 ? args[0] : null;
if (srcDb != null && File.Exists(srcDb))
{
    File.Copy(srcDb, Path.Combine(legacy, "fire_dept.db"));
    var srcSettings = Path.Combine(Path.GetDirectoryName(srcDb)!, "settings.json");
    if (File.Exists(srcSettings)) File.Copy(srcSettings, Path.Combine(legacy, "settings.json"));
}
else
{
    // CI: no real database available — create an empty legacy DB (schema only).
    using (new Database(Path.Combine(legacy, "fire_dept.db"))) { }
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    srcDb = null;
}
File.WriteAllBytes(Path.Combine(legacy, "signature.png"), TinyPng());
var data = Path.Combine(work, "data");
Environment.SetEnvironmentVariable("FIREDEPT_DATA_DIR", data);
Console.WriteLine("[Migration]");
var copied = AppPaths.MigrateLegacyData(legacy);
Check(AppPaths.DataDir == data, "FIREDEPT_DATA_DIR override");
Check(File.Exists(Path.Combine(data, "signature.png")), "signature copied");
if (srcDb != null) Check(File.Exists(AppPaths.DbPath), "db copied");
var copied2 = AppPaths.MigrateLegacyData(legacy);
Check(srcDb == null || copied2.Count == 0, "migration does not overwrite existing data");

// ---- settings -----------------------------------------------------------------
Console.WriteLine("[Settings]");
var s = AppSettings.Load(data);
Check(AppSettings.IsHex(s.Accent), "accent is hex");
s.RateInTown = 15; s.RateOutOfTown = 30; s.RateMeetings = 10;
s.CallLocations["Structure Fire"] = "Station 1";
s.AddUserWord("Cosper");
s.Save();
var s2 = AppSettings.Load(data);
Check(s2.RateInTown == 15 && s2.RateOutOfTown == 30 && s2.RateMeetings == 10, "rates round-trip");
Check(s2.CallLocations.TryGetValue("Structure Fire", out var loc) && loc == "Station 1", "call locations round-trip");
Check(s2.UserWords.Contains("Cosper"), "user words round-trip");
var raw = File.ReadAllText(Path.Combine(data, "settings.json"));
Check(raw.Contains("\"rates\"") && raw.Contains("\"out_of_town\""), "python-compatible json keys");

// ---- dates --------------------------------------------------------------------
Console.WriteLine("[Dates]");
Check(DateUtil.UsToIso("03/09/2025") == "2025-03-09", "UsToIso MM/DD/YYYY");
Check(DateUtil.UsToIso("3/9/2025") == "2025-03-09", "UsToIso M/D/YYYY");
Check(DateUtil.IsoToUs("2025-03-09") == "03/09/2025", "IsoToUs");
Throws<FormatException>(() => DateUtil.UsToIso("13/40/2025"), "invalid date rejected");
Check(DateUtil.FormatServiceTime("2020-01-01", "2025-02-02") == "5 years, 1 month, 1 day", "service time format");
Check(DateUtil.MilestoneEmblem("2015-01-01", "2025-06-01").Years == 10, "10-year milestone");

// ---- database -----------------------------------------------------------------
Console.WriteLine("[Database]");
using var db = new Database(AppPaths.DbPath);
var m1 = db.AddMember("John", "Smith", "Q", "Jack", true, "12");
var m2 = db.AddMember("Jane", "Doe", "", "", true, "7");
var m3 = db.AddMember("Old", "Timer", "", "", false, "");
Check(db.GetMember(m1)!.Name.Contains("Smith"), "member name formatted");
Check(db.GetMembers(search: "jack").Any(m => m.Id == m1), "member search by nickname");
Check(db.CountActiveMembers() >= 2, "active count");
db.UpdateMemberDates(m1, "2015-04-01", "");
db.UpdateMemberDates(m3, "2010-01-01", "2024-06-30");
Throws<ValidationException>(() => db.AddMember("", "NoFirst"), "first name required");

var call1 = db.AddEvent("call", "2025-02-10", "Main St", "Structure Fire", "out_of_town", 2);
var call2 = db.AddEvent("call", "2025-03-15", "Elm", "EMS Assist", "in_town", 1);
var meet = db.AddEvent("meeting", "2025-01-08", "Station 1", "", null, 3);
Check(db.GetEvent(meet)!.CallMultiplier == 1 && db.GetEvent(meet)!.CallCategory == null, "meeting normalized");
Throws<ValidationException>(() => db.AddEvent("call", "2025-02-10", "x", "", null), "call needs category");
Throws<ValidationException>(() => db.AddEvent("call", "02/10/2025", "x", "", "in_town"), "event date must be ISO");
var q1 = db.GetOrCreateQuarter(2025, 1);
Check(db.GetEvent(call1)!.QuarterId == q1, "quarter derived from date");
db.SaveEventAttendance(call1, new Dictionary<long, bool> { [m1] = true, [m2] = true });
db.SaveEventAttendance(call2, new Dictionary<long, bool> { [m1] = true });
db.SaveEventAttendance(meet, new Dictionary<long, bool> { [m1] = true, [m2] = false });
Check(db.CountEventAttendees(call1) == 2, "attendee count");
var acc = db.AccountingStats(q1);
Check(acc.OutOfTown.Count == 1 && acc.OutOfTown.Attendees == 4, "accounting out-of-town weighted (2 x2)");
Check(acc.InTown.Attendees == 1 && acc.Meetings.Attendees == 1, "accounting in-town/meetings");
var ms = db.MemberStats(m1, q1);
Check(ms.MeetingsAttended == 1 && ms.TotalOutOfTown == 2, "member stats weighted");
var tot = db.MemberAttendanceTotals(m1);
Check(tot.MeetingsAttended == 1 && tot.CallsAttended == 3, "member totals weighted");
var ov = db.EventsOverview(q1);
Check(ov.TotalMeetings == 1 && ov.TotalCalls == 2 && ov.TotalCallCredits == 3, "events overview");
Check(db.GetDistinctCallTypes().Contains("Structure Fire"), "distinct call types");
// move event across quarters
db.UpdateEvent(call2, eventDate: "2025-04-02");
Check(db.GetEvent(call2)!.QuarterId == db.GetOrCreateQuarter(2025, 2), "update moves quarter");
Check(db.GetMembersForEvent("2025-02-10").All(m => m.Id != m3), "inactive-before-date member excluded");
for (int i = 0; i < 5; i++) db.AddEvent("meeting", $"2025-02-0{i + 1}", "Station 1");
Throws<ValidationException>(() => db.AddEvent("meeting", "2025-03-01", "Station 1"), "6 meetings/quarter limit");

// minutes
var body = new List<List<RichRun>> { new() { new RichRun { Text = "Opened at ", }, new RichRun { Text = "7pm", Bold = true } },
                                     new() { new RichRun { Text = "Adjourned", Italic = true, Underline = true } } };
var enc = RichTextModel.Encode(body);
db.SaveEventMinutes(meet, enc);
var back = RichTextModel.Parse(db.GetEventMinutes(meet));
Check(back.Count == 2 && back[0][1].Bold && back[1][0].Italic && back[1][0].Underline, "rich minutes round-trip");
Check(RichTextModel.IsEmpty("") && !RichTextModel.IsEmpty(enc), "IsEmpty");
var sub = RichTextModel.Substitute(RichTextModel.Parse(AppSettings.DefaultHeader),
    RichTextModel.Placeholders(5, "2025-01-08", "Station 1"));
var plain = RichTextModel.ToPlain(sub);
Check(plain.Contains("01/08/2025") && plain.Contains("Station 1") && plain.Contains("5"), "placeholder substitution");

// ---- exports ------------------------------------------------------------------
Console.WriteLine("[Excel]");
var xlsx = Path.Combine(work, "report.xlsx");
ExcelExporter.ExportQuarterWorkbook(db, xlsx, new List<long> { q1 }, false, true);
using (var wb = new XLWorkbook(xlsx))
{
    var names = wb.Worksheets.Select(w => w.Name).ToList();
    Console.WriteLine("    sheets: " + string.Join(", ", names));
    Check(names.Count >= 2, "workbook has summary + detail sheets");
    Check(names.Any(n => n.Contains("Roster", StringComparison.OrdinalIgnoreCase)), "roster sheet included");
}
var xlsxAll = Path.Combine(work, "all.xlsx");
ExcelExporter.ExportQuarterWorkbook(db, xlsxAll, null, true, false);
Check(File.Exists(xlsxAll), "all-quarters export");

Console.WriteLine("[Docx]");
var docx = Path.Combine(work, "minutes.docx");
var footer = RichTextModel.Parse(AppSettings.DefaultFooter);
footer.Add(new List<RichRun> { new RichRun { IsImage = true } });
DocxExporter.ExportMinutes(docx, sub, back, footer, Path.Combine(data, "signature.png"));
using (var wd = WordprocessingDocument.Open(docx, false))
{
    var text = wd.MainDocumentPart!.Document.Body!.InnerText;
    Check(text.Contains("MEETING MINUTES") && text.Contains("Adjourned"), "docx text content");
    Check(wd.MainDocumentPart.ImageParts.Any(), "docx has signature image");
}
var summary = DocxExporter.ExportQuarterMinutes(db, q1, work, s.MinutesHeader, s.MinutesFooter, null);
Check(summary.TotalMeetings == 6 && summary.Exported == 6 && summary.Blank == 5, $"quarter minutes export ({summary.Exported}/{summary.TotalMeetings}, blank {summary.Blank})");
Check(Directory.GetFiles(summary.Folder, "*.docx").Length == 6, "6 docx files on disk");

// delete cascade
db.DeleteMember(m2);
Check(db.GetEventAttendance(call1).Count == 1, "delete member cascades attendance");
db.DeleteEvent(call1);
Check(db.GetEvent(call1) == null, "delete event");

Console.WriteLine($"\n{passed} passed, {failures} failed. Outputs in {work}");
return failures == 0 ? 0 : 1;

static byte[] TinyPng() => Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP8z8DAwMDAxMDAwMDAAAANHQEDasKb6QAAAABJRU5ErkJggg==");
