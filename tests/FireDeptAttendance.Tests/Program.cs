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
// Empty databases are deliberately skipped by the migration, so make sure this one has a record.
using (var ldb = new Database(Path.Combine(legacy, "fire_dept.db")))
    if (ldb.GetMembers().Count == 0) ldb.AddMember("Legacy", "Member", "", "", false, "");
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
File.WriteAllBytes(Path.Combine(legacy, "signature.png"), TinyPng());
var data = Path.Combine(work, "data");
Environment.SetEnvironmentVariable("FIREDEPT_DATA_DIR", data);
Console.WriteLine("[Migration]");
var copied = AppPaths.MigrateLegacyData(legacy);
Check(AppPaths.DataDir == data, "FIREDEPT_DATA_DIR override");
Check(File.Exists(Path.Combine(data, "signature.png")), "signature copied");
Check(File.Exists(AppPaths.DbPath), "db copied");
var copied2 = AppPaths.MigrateLegacyData(legacy);
Check(copied2.Count == 0, "migration does not overwrite existing data");

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

// ---- tab characters in minutes ---------------------------------------------------
Console.WriteLine("[Tabs]");
var tabParas = new List<List<RichRun>>
{
    new() { new RichRun { Text = "\tIndented first line" } },
    new() { new RichRun { Text = "Item\tValue", Bold = true }, new RichRun { Text = "\t\tend", Italic = true } },
    new(),
    new() { new RichRun { Text = "\t" } },
};
var tabEnc = RichTextModel.Encode(tabParas);
var tabBack = RichTextModel.Parse(tabEnc);
Check(tabBack.Count == 4 && tabBack[0][0].Text == "\tIndented first line" && tabBack[1][0].Text == "Item\tValue" &&
      tabBack[1][0].Bold && tabBack[1][1].Text == "\t\tend" && tabBack[1][1].Italic && tabBack[3][0].Text == "\t",
    "tabs round-trip through rich-v1 save/load");
Check(RichTextModel.Parse("{\"format\": \"rich-v1\", \"paras\": [[{\"t\": \"\\tA\\tB\", \"b\": 0, \"i\": 0, \"u\": 0}]]}")[0][0].Text == "\tA\tB",
    "Python-written escaped tabs load");
Check(RichTextModel.Parse("line1\n\tline2")[1][0].Text == "\tline2", "tabs kept in legacy plain-text minutes");
Check(RichTextModel.ToPlain(tabBack) == "\tIndented first line\nItem\tValue\t\tend\n\n\t", "tabs kept in .txt export");
Check(!RichTextModel.IsEmpty(tabEnc) && RichTextModel.IsEmpty(RichTextModel.Encode(new() { new() { new RichRun { Text = "\t\t" } } })),
    "tab-only minutes count as empty");
var subTab = RichTextModel.Substitute(RichTextModel.Parse("{date}\t{location}"), RichTextModel.Placeholders(3, "2025-02-03", "Station 1"));
Check(subTab[0][0].Text == "02/03/2025\tStation 1", "placeholders substituted around tabs");
var tabDocx = Path.Combine(work, "tabs.docx");
DocxExporter.ExportMinutes(tabDocx, new() { new() }, tabParas, new() { new() });
using (var wd = WordprocessingDocument.Open(tabDocx, false))
{
    var b = wd.MainDocumentPart!.Document.Body!;
    var tabsInDoc = b.Descendants<DocumentFormat.OpenXml.Wordprocessing.TabChar>().Count();
    var texts = b.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(t => t.Text).ToList();
    Check(tabsInDoc == 5, $"docx has one <w:tab/> per tab character ({tabsInDoc})");
    Check(texts.All(t => !t.Contains('\t')) && texts.Contains("Indented first line") && texts.Contains("Item") &&
          texts.Contains("Value") && texts.Contains("end"), "docx text split around tabs, no raw tabs");
    var boldRun = b.Descendants<DocumentFormat.OpenXml.Wordprocessing.Run>()
        .First(r => r.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Any(t => t.Text == "Item"));
    Check(boldRun.RunProperties?.Bold != null && boldRun.Elements<DocumentFormat.OpenXml.Wordprocessing.TabChar>().Count() == 1,
        "tab stays inside its formatted run");
}
var tabEvent = meet;
var savedMinutes = db.GetEventMinutes(tabEvent);
db.SaveEventMinutes(tabEvent, tabEnc);
Check(db.GetEventMinutes(tabEvent) == tabEnc, "tabbed minutes saved to database unchanged");
var tabSummary = DocxExporter.ExportQuarterMinutes(db, q1, Path.Combine(work, "tabbatch"), s.MinutesHeader, s.MinutesFooter, null);
var tabFile = Directory.GetFiles(tabSummary.Folder, "*.docx").Select(f =>
{
    using var wd = WordprocessingDocument.Open(f, false);
    return wd.MainDocumentPart!.Document.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.TabChar>().Count();
}).Max();
Check(tabFile >= 5, "batch minutes export writes tabs");
db.SaveEventMinutes(tabEvent, savedMinutes);

// delete cascade
db.DeleteMember(m2);
Check(db.GetEventAttendance(call1).Count == 1, "delete member cascades attendance");
db.DeleteEvent(call1);
Check(db.GetEvent(call1) == null, "delete event");

// ---- data folder: pointer file + move/copy ------------------------------------
Console.WriteLine("[Data folder]");
Environment.SetEnvironmentVariable("FIREDEPT_DATA_DIR", null);
var defDir = Path.Combine(work, "appdata_default");
AppPaths.DefaultDirOverride = defDir;
Check(AppPaths.DataDir == defDir && AppPaths.ReadPointer() == null, "no pointer -> default folder");
SeedDataFolder(defDir, "Default", "Person");
var custom = Path.Combine(work, "custom data");
AppPaths.ChangeDataDir(custom, DataMoveMode.Copy);
Check(File.Exists(AppPaths.PointerPath), "pointer file written");
Check(AppPaths.DataDir == Path.GetFullPath(custom) && AppPaths.ReadPointer() == Path.GetFullPath(custom), "pointer resolves to custom folder");
Check(File.Exists(Path.Combine(custom, "fire_dept.db")) && File.Exists(Path.Combine(custom, "settings.json")) &&
      File.Exists(Path.Combine(custom, "signature.png")) && File.Exists(Path.Combine(custom, "backups", "old.db")),
    "copy: db, settings, signature and backups copied");
Check(File.Exists(Path.Combine(defDir, "fire_dept.db")) && File.Exists(Path.Combine(defDir, "backups", "old.db")),
    "copy: originals kept");
var moved = Path.Combine(work, "moved");
AppPaths.ChangeDataDir(moved, DataMoveMode.Move);
Check(AppPaths.DataDir == Path.GetFullPath(moved) && DataImport.Inspect(Path.Combine(moved, "fire_dept.db")).Members == 1,
    "move: data readable in new folder");
Check(!File.Exists(Path.Combine(custom, "fire_dept.db")) && !File.Exists(Path.Combine(custom, "settings.json")) &&
      !File.Exists(Path.Combine(custom, "backups", "old.db")), "move: removed from old folder");
var occupied = Path.Combine(work, "occupied");
SeedDataFolder(occupied, "Other", "Dept");
Throws<IOException>(() => AppPaths.ChangeDataDir(occupied, DataMoveMode.Copy), "copy refuses to overwrite existing data");
Check(AppPaths.DataDir == Path.GetFullPath(moved), "failed change leaves folder unchanged");
Check(DataImport.Inspect(Path.Combine(occupied, "fire_dept.db")).Members == 1, "existing data in target untouched");
AppPaths.ChangeDataDir(occupied, DataMoveMode.UseExisting);
Check(AppPaths.DataDir == Path.GetFullPath(occupied), "use existing data in new folder");
AppPaths.ChangeDataDir(null, DataMoveMode.UseExisting);
Check(AppPaths.DataDir == defDir && !File.Exists(AppPaths.PointerPath), "reset to default removes pointer");
Check(AppPaths.ValidateFolder("relative/path") != null, "relative folder rejected");
var aFile = Path.Combine(work, "not_a_folder.txt");
File.WriteAllText(aFile, "x");
Check(AppPaths.ValidateFolder(aFile) != null, "file path rejected as folder");
Throws<IOException>(() => AppPaths.ChangeDataDir(aFile, DataMoveMode.Copy), "change to invalid folder throws");
File.WriteAllText(AppPaths.PointerPath, "{ not json");
Check(AppPaths.DataDir == defDir, "corrupt pointer falls back to default");
File.WriteAllText(AppPaths.PointerPath, "{\"data_dir\": \"" + moved.Replace("\\", "\\\\") + "\"}");
Check(AppPaths.DataDir == moved, "hand-written pointer honoured");
var envDir = Path.Combine(work, "env");
Environment.SetEnvironmentVariable("FIREDEPT_DATA_DIR", envDir);
Check(AppPaths.DataDir == envDir && AppPaths.IsEnvOverride, "FIREDEPT_DATA_DIR overrides pointer");
Throws<InvalidOperationException>(() => AppPaths.ChangeDataDir(custom, DataMoveMode.Copy), "env override locks folder setting");
Environment.SetEnvironmentVariable("FIREDEPT_DATA_DIR", null);
File.Delete(AppPaths.PointerPath);

// ---- import: validation, preview, backup, replace -----------------------------
Console.WriteLine("[Import]");
Check(!DataImport.Inspect(Path.Combine(work, "nope.db")).IsValid, "missing file invalid");
var zero = Path.Combine(work, "zero.db"); File.WriteAllBytes(zero, Array.Empty<byte>());
Check(!DataImport.Inspect(zero).IsValid, "0-byte file invalid");
var txt = Path.Combine(work, "text.db"); File.WriteAllText(txt, "hello, this is not sqlite at all");
Check(!DataImport.Inspect(txt).IsValid, "non-SQLite file invalid");
var other = Path.Combine(work, "other.db");
RawSql(other, "CREATE TABLE foo (id INTEGER)");
var otherP = DataImport.Inspect(other);
Check(!otherP.IsValid && otherP.Error.Contains("not a Fire Dept"), "SQLite without attendance tables invalid");

// Legacy DB exactly as the first Python version (f15f34e) created it: name-only members,
// no join_date / multiplier / minutes columns. Written in WAL mode with the last rows still
// only in the -wal file (connection kept open) to prove WAL content is imported.
var oldDir = Path.Combine(work, "old_python_app");
Directory.CreateDirectory(oldDir);
var oldDb = Path.Combine(oldDir, "fire_dept.db");
var walConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={oldDb};Pooling=False");
walConn.Open();
ExecOn(walConn, @"
CREATE TABLE members (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, active INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL);
CREATE TABLE quarters (id INTEGER PRIMARY KEY AUTOINCREMENT, year INTEGER NOT NULL, quarter INTEGER NOT NULL, UNIQUE(year, quarter));
CREATE TABLE events (id INTEGER PRIMARY KEY AUTOINCREMENT, quarter_id INTEGER NOT NULL, event_type TEXT NOT NULL, title TEXT,
  location TEXT, call_category TEXT, event_date TEXT NOT NULL, month INTEGER NOT NULL,
  FOREIGN KEY (quarter_id) REFERENCES quarters(id) ON DELETE CASCADE);
CREATE TABLE attendance (id INTEGER PRIMARY KEY AUTOINCREMENT, event_id INTEGER NOT NULL, member_id INTEGER NOT NULL,
  present INTEGER NOT NULL DEFAULT 0, UNIQUE(event_id, member_id));
INSERT INTO members (name, active, created_at) VALUES ('John Smith', 1, '2019-05-01 10:00:00');
INSERT INTO quarters (year, quarter) VALUES (2024, 3);
INSERT INTO events (quarter_id, event_type, title, location, call_category, event_date, month)
  VALUES (1, 'meeting', 'Monthly', 'Station 1', NULL, '2024-07-09', 7);");
ExecOn(walConn, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
ExecOn(walConn, @"
INSERT INTO members (name, active, created_at) VALUES ('Jane Doe', 0, '2020-01-15 09:00:00');
INSERT INTO events (quarter_id, event_type, title, location, call_category, event_date, month)
  VALUES (1, 'call', 'Fire', 'Main St', 'out_of_town', '2024-09-21', 9);
INSERT INTO attendance (event_id, member_id, present) VALUES (1, 1, 1), (2, 1, 1), (2, 2, 1);");
Check(File.Exists(oldDb + "-wal") && new FileInfo(oldDb + "-wal").Length > 0, "legacy db has un-checkpointed -wal data");
File.WriteAllText(Path.Combine(oldDir, "settings.json"),
    "{\"theme\": \"dark\", \"rates\": {\"in_town\": 12.5, \"out_of_town\": 25, \"meetings\": 5}, \"some_python_key\": 1}");
File.WriteAllBytes(Path.Combine(oldDir, "signature.png"), TinyPng());

var pv = DataImport.Inspect(oldDb);
Check(pv.IsValid && pv.Members == 2 && pv.ActiveMembers == 1, $"preview members incl. WAL rows ({pv.Members})");
Check(pv.Events == 2 && pv.Meetings == 1 && pv.Calls == 1 && pv.Quarters == 1 && pv.AttendanceMarks == 3, "preview events/quarters/marks");
Check(pv.FirstEventDate == "2024-07-09" && pv.LastEventDate == "2024-09-21", "preview date range");
Check(pv.Describe().Contains("Members: 2"), "preview description");

var target = Path.Combine(work, "target");
SeedDataFolder(target, "Current", "Member");
var ts = AppSettings.Load(target);
ts.ExportExcelDir = "/reports/here"; ts.LastImportFile = "/somewhere/fire_dept.db"; ts.Save();
var res = DataImport.Import(oldDb, target, includeSideFiles: true);
walConn.Dispose();
Check(res.BackupPath != null && File.Exists(res.BackupPath) && res.BackupPath.Contains(Path.Combine("target", "backups")),
    "current db backed up to backups folder");
Check(res.BackupPath != null && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(res.BackupPath), @"^fire_dept_\d{8}_\d{6}"),
    "backup file is timestamped");
Check(res.BackupPath != null && DataImport.Inspect(res.BackupPath).Members == 1, "backup holds the previous data");
using (var idb = new Database(Path.Combine(target, "fire_dept.db")))
{
    var mem = idb.GetMembers();
    Check(mem.Count == 2, "imported db has both members");
    var john = mem.FirstOrDefault(m => m.LastName == "Smith");
    Check(john != null && john.FirstName == "John" && john.JoinDate == "2019-05-01", "legacy names/join date migrated");
    Check(mem.Any(m => m.LastName == "Doe" && !m.Active && m.InactiveDate.Length > 0), "legacy inactive member migrated");
    var evs = idb.GetAllEvents();
    Check(evs.Count == 2 && evs.All(e => e.CallMultiplier == 1), "legacy events get multiplier column");
    var fireId = evs.First(e => e.EventType == "call").Id;
    Check(idb.CountEventAttendees(fireId) == 2, "attendance imported");
}
var ts2 = AppSettings.Load(target);
Check(ts2.Theme == "dark" && ts2.RateInTown == 12.5, "settings.json imported from source folder");
Check(ts2.ExportExcelDir == "/reports/here" && ts2.LastImportFile == "/somewhere/fire_dept.db", "folder preferences kept on import");
Check(File.ReadAllText(Path.Combine(target, "settings.json")).Contains("some_python_key"), "unknown settings keys kept");
Check(File.Exists(Path.Combine(target, "signature.png")), "signature imported");
using (var chk = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={oldDb};Mode=ReadOnly;Pooling=False"))
{
    chk.Open();
    using var c = chk.CreateCommand();
    c.CommandText = "SELECT COUNT(*) FROM pragma_table_info('members') WHERE name = 'first_name'";
    Check(Convert.ToInt64(c.ExecuteScalar()) == 0, "source database left unmodified");
}
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
Throws<InvalidDataException>(() => DataImport.Import(txt, target, false), "import rejects invalid file");
Check(DataImport.Inspect(Path.Combine(target, "fire_dept.db")).Members == 2, "failed import leaves data intact");

var rres = DataImport.RestoreBackup(res.BackupPath!, target);
Check(rres.Imported.Members == 1 && rres.Imported.Events == 0, "restore previous backup");
Check(DataImport.ListBackups(target).Count(b => Path.GetFileName(b).Contains("_before_")) == 2, "restore backs up current data first");

// ---- migration-before-create fix ------------------------------------------------
Console.WriteLine("[Startup migration]");
var fresh = Path.Combine(work, "fresh_data");
Environment.SetEnvironmentVariable("FIREDEPT_DATA_DIR", fresh);
using (new Database(AppPaths.DbPath)) { }  // the empty DB a previous app start left behind
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
var emptyCand = Path.Combine(work, "empty_candidate");
using (new Database(Path.Combine(emptyCand, "fire_dept.db"))) { }
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
var mig = AppPaths.MigrateLegacyData(emptyCand, oldDir);
Check(mig.Count > 0 && AppPaths.LastMigrationSource == oldDb, "empty candidate skipped, real data found");
Check(DataImport.Inspect(AppPaths.DbPath).Members == 2, "existing EMPTY db replaced by legacy data");
var mig2 = AppPaths.MigrateLegacyData(oldDir);
Check(mig2.Count == 0 && AppPaths.LastMigrationSource == null, "db with data is never overwritten by migration");
Check(AppPaths.LegacyCandidateDirs(Path.Combine(work, "app", "current")).Contains(Path.Combine(work, "app")),
    "candidate dirs include exe parent");

// ---- settings: folder preferences -------------------------------------------------
Console.WriteLine("[Folder settings]");
var ps = AppSettings.Load(fresh);
ps.ExportExcelDir = work; ps.ExportMinutesDir = "/does/not/exist"; ps.SkipImportPrompt = true; ps.Save();
var ps2 = AppSettings.Load(fresh);
Check(ps2.ExportExcelDir == work && ps2.SkipImportPrompt, "folder preferences round-trip");
Check(AppSettings.ResolveDir(ps2.ExportExcelDir) == work, "existing export folder used");
Check(AppSettings.ResolveDir(ps2.ExportMinutesDir) != "/does/not/exist", "missing export folder falls back");
ps2.ResetToDefaults();
Check(ps2.ExportExcelDir == work, "reset to defaults keeps folder preferences");
Environment.SetEnvironmentVariable("FIREDEPT_DATA_DIR", data);
AppPaths.DefaultDirOverride = null;

Console.WriteLine($"\n{passed} passed, {failures} failed. Outputs in {work}");
return failures == 0 ? 0 : 1;

static void SeedDataFolder(string dir, string first, string last)
{
    Directory.CreateDirectory(Path.Combine(dir, "backups"));
    using (var d = new Database(Path.Combine(dir, "fire_dept.db"))) d.AddMember(first, last);
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"theme\": \"light\"}");
    File.WriteAllBytes(Path.Combine(dir, "signature.png"), TinyPng());
    File.WriteAllText(Path.Combine(dir, "backups", "old.db"), "backup");
}

static void ExecOn(Microsoft.Data.Sqlite.SqliteConnection c, string sql)
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}

static void RawSql(string path, string sql)
{
    using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
    {
        c.Open();
        ExecOn(c, sql);
    }
}

static byte[] TinyPng() => Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP8z8DAwMDAxMDAwMDAAAANHQEDasKb6QAAAABJRU5ErkJggg==");
