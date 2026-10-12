using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace FireDeptAttendance.Core;

/// <summary>
/// SQLite data-access layer. Schema, migrations and business rules are a
/// faithful port of the original Python <c>database.py</c> so existing
/// <c>fire_dept.db</c> files open unchanged.
/// </summary>
public sealed class Database : IDisposable
{
    public const int MaxMembers = 40;
    public const int MeetingsPerQuarter = 6;

    public static readonly IReadOnlyDictionary<int, int[]> QuarterMonths = new Dictionary<int, int[]>
    {
        [1] = new[] { 1, 2, 3 },
        [2] = new[] { 4, 5, 6 },
        [3] = new[] { 7, 8, 9 },
        [4] = new[] { 10, 11, 12 },
    };

    public static readonly string[] MonthNames =
    {
        "", "January", "February", "March", "April", "May", "June", "July",
        "August", "September", "October", "November", "December",
    };

    public static int QuarterForMonth(int month) => (month - 1) / 3 + 1;

    private readonly SqliteConnection _conn;
    private readonly object _lock = new();
    public string DbPath { get; }

    public Database(string dbPath)
    {
        DbPath = dbPath;
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _conn.Open();
        Exec("PRAGMA foreign_keys = ON");
        CreateTables();
        MigrateMemberNames();
        MigrateEvents();
    }

    public void Dispose() => _conn.Dispose();

    // ------------------------------------------------------------------ //
    // Low-level helpers
    // ------------------------------------------------------------------ //
    private SqliteCommand Cmd(string sql, params object?[] args)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        return cmd;
    }

    private int Exec(string sql, params object?[] args)
    {
        lock (_lock) { using var c = Cmd(sql, args); return c.ExecuteNonQuery(); }
    }

    private long Scalar(string sql, params object?[] args)
    {
        lock (_lock)
        {
            using var c = Cmd(sql, args);
            var v = c.ExecuteScalar();
            return v is null || v is DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }
    }

    private long Insert(string sql, params object?[] args)
    {
        lock (_lock)
        {
            using var c = Cmd(sql, args);
            c.ExecuteNonQuery();
            using var id = _conn.CreateCommand();
            id.CommandText = "SELECT last_insert_rowid()";
            return (long)id.ExecuteScalar()!;
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        lock (_lock)
        {
            using var c = Cmd(sql, args);
            using var r = c.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    private static string Str(SqliteDataReader r, string col)
    {
        int i;
        try { i = r.GetOrdinal(col); } catch (ArgumentOutOfRangeException) { return ""; }
        return r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
    }

    private static long Lng(SqliteDataReader r, string col, long dflt = 0)
    {
        int i;
        try { i = r.GetOrdinal(col); } catch (ArgumentOutOfRangeException) { return dflt; }
        return r.IsDBNull(i) ? dflt : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
    }

    private static bool HasCol(SqliteDataReader r, string col)
    {
        for (int i = 0; i < r.FieldCount; i++)
            if (r.GetName(i) == col) return true;
        return false;
    }

    private static Member MapMember(SqliteDataReader r) => new()
    {
        Id = Lng(r, "id"),
        FirstName = Str(r, "first_name"),
        LastName = Str(r, "last_name"),
        MiddleInitial = Str(r, "middle_initial"),
        Nickname = Str(r, "nickname"),
        Name = Str(r, "name"),
        Active = Lng(r, "active") != 0,
        CreatedAt = Str(r, "created_at"),
        JoinDate = Str(r, "join_date"),
        InactiveDate = Str(r, "inactive_date"),
        RadioNumber = Str(r, "radio_number"),
    };

    private static Event MapEvent(SqliteDataReader r) => new()
    {
        Id = Lng(r, "id"),
        QuarterId = Lng(r, "quarter_id"),
        EventType = Str(r, "event_type"),
        Title = Str(r, "title"),
        Location = Str(r, "location"),
        CallCategory = string.IsNullOrEmpty(Str(r, "call_category")) ? null : Str(r, "call_category"),
        EventDate = Str(r, "event_date"),
        Month = (int)Lng(r, "month"),
        CallMultiplier = (int)Lng(r, "call_multiplier", 1),
        Minutes = Str(r, "minutes"),
        Year = HasCol(r, "year") ? (int)Lng(r, "year") : 0,
        QuarterNumber = HasCol(r, "quarter") ? (int)Lng(r, "quarter") : 0,
    };

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TryParseIso(string s, out DateTime dt) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt);

    // ------------------------------------------------------------------ //
    // Member-name helpers
    // ------------------------------------------------------------------ //
    private static string CollapseWs(string s) =>
        string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>First "Nick" Last / First M. Last / First Last.</summary>
    public static string FormatMemberName(string? first, string? last, string? mi = "", string? nick = "")
    {
        var f = (first ?? "").Trim();
        var l = (last ?? "").Trim();
        var m = (mi ?? "").Trim().TrimEnd('.');
        var n = (nick ?? "").Trim();
        string core = n.Length > 0 ? $"{f} \"{n}\" {l}"
                    : m.Length > 0 ? $"{f} {m}. {l}"
                    : $"{f} {l}";
        return CollapseWs(core);
    }

    private static readonly Regex NickRe = new("[\"\u201c\u2018']([^\"\u201d\u2019']+)[\"\u201d\u2019']");

    public static (string First, string Last, string Mi, string Nick) ParseLegacyName(string? name)
    {
        name = (name ?? "").Trim();
        var nick = "";
        var m = NickRe.Match(name);
        if (m.Success)
        {
            nick = m.Groups[1].Value.Trim();
            name = (name[..m.Index] + " " + name[(m.Index + m.Length)..]).Trim();
        }
        var tokens = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string first = "", last = "", middle = "";
        if (tokens.Length == 0) first = "Unknown";
        else if (tokens.Length == 1) first = tokens[0];
        else if (tokens.Length == 2) { first = tokens[0]; last = tokens[1]; }
        else
        {
            first = tokens[0];
            last = tokens[^1];
            var middles = tokens[1..^1];
            var cand = middles[0].TrimEnd('.');
            if (cand.Length == 1) middle = cand;
            else first = string.Join(" ", new[] { tokens[0] }.Concat(middles));
        }
        return (first, last, middle, nick);
    }

    // ------------------------------------------------------------------ //
    // Schema + migrations
    // ------------------------------------------------------------------ //
    private void CreateTables()
    {
        Exec(@"
CREATE TABLE IF NOT EXISTS members (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    first_name     TEXT NOT NULL DEFAULT '',
    last_name      TEXT NOT NULL DEFAULT '',
    middle_initial TEXT DEFAULT '',
    nickname       TEXT DEFAULT '',
    name           TEXT NOT NULL,
    active         INTEGER NOT NULL DEFAULT 1,
    created_at     TEXT NOT NULL,
    join_date      TEXT DEFAULT '',
    inactive_date  TEXT DEFAULT '',
    radio_number   TEXT DEFAULT ''
);
CREATE TABLE IF NOT EXISTS quarters (
    id       INTEGER PRIMARY KEY AUTOINCREMENT,
    year     INTEGER NOT NULL,
    quarter  INTEGER NOT NULL,
    UNIQUE(year, quarter)
);
CREATE TABLE IF NOT EXISTS events (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    quarter_id      INTEGER NOT NULL,
    event_type      TEXT NOT NULL,
    title           TEXT,
    location        TEXT,
    call_category   TEXT,
    event_date      TEXT NOT NULL,
    month           INTEGER NOT NULL,
    call_multiplier INTEGER NOT NULL DEFAULT 1,
    minutes         TEXT NOT NULL DEFAULT '',
    FOREIGN KEY (quarter_id) REFERENCES quarters(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS attendance (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id  INTEGER NOT NULL,
    member_id INTEGER NOT NULL,
    present   INTEGER NOT NULL DEFAULT 0,
    UNIQUE(event_id, member_id),
    FOREIGN KEY (event_id)  REFERENCES events(id)  ON DELETE CASCADE,
    FOREIGN KEY (member_id) REFERENCES members(id) ON DELETE CASCADE
);");
    }

    private HashSet<string> TableColumns(string table) =>
        Query($"PRAGMA table_info({table})", r => Str(r, "name")).ToHashSet();

    private void MigrateEvents()
    {
        var cols = TableColumns("events");
        if (!cols.Contains("call_multiplier"))
            Exec("ALTER TABLE events ADD COLUMN call_multiplier INTEGER NOT NULL DEFAULT 1");
        if (!cols.Contains("minutes"))
            Exec("ALTER TABLE events ADD COLUMN minutes TEXT NOT NULL DEFAULT ''");
    }

    private void MigrateMemberNames()
    {
        var cols = TableColumns("members");
        foreach (var (col, ddl) in new[]
        {
            ("first_name", "ALTER TABLE members ADD COLUMN first_name TEXT NOT NULL DEFAULT ''"),
            ("last_name", "ALTER TABLE members ADD COLUMN last_name TEXT NOT NULL DEFAULT ''"),
            ("middle_initial", "ALTER TABLE members ADD COLUMN middle_initial TEXT DEFAULT ''"),
            ("nickname", "ALTER TABLE members ADD COLUMN nickname TEXT DEFAULT ''"),
            ("join_date", "ALTER TABLE members ADD COLUMN join_date TEXT DEFAULT ''"),
            ("inactive_date", "ALTER TABLE members ADD COLUMN inactive_date TEXT DEFAULT ''"),
            ("radio_number", "ALTER TABLE members ADD COLUMN radio_number TEXT DEFAULT ''"),
        })
        {
            if (!cols.Contains(col)) Exec(ddl);
        }

        var today = Today;
        foreach (var (id, created, join) in Query("SELECT id, created_at, join_date FROM members",
                     r => (Lng(r, "id"), Str(r, "created_at"), Str(r, "join_date"))))
        {
            if (join.Trim().Length > 0) continue;
            var c = created.Length >= 10 ? created[..10] : created;
            if (c.Length == 0) c = today;
            Exec("UPDATE members SET join_date = $p0 WHERE id = $p1", c, id);
        }
        foreach (var (id, active, inact, join) in Query("SELECT id, active, inactive_date, join_date FROM members",
                     r => (Lng(r, "id"), Lng(r, "active"), Str(r, "inactive_date"), Str(r, "join_date"))))
        {
            if (active == 0 && inact.Trim().Length == 0)
                Exec("UPDATE members SET inactive_date = $p0 WHERE id = $p1",
                    join.Length > 0 ? join : today, id);
        }
        foreach (var (id, name, first, last) in Query("SELECT id, name, first_name, last_name FROM members",
                     r => (Lng(r, "id"), Str(r, "name"), Str(r, "first_name"), Str(r, "last_name"))))
        {
            if (first.Trim().Length > 0 || last.Trim().Length > 0) continue;
            var p = ParseLegacyName(name);
            var display = FormatMemberName(p.First, p.Last, p.Mi, p.Nick);
            Exec(@"UPDATE members SET first_name=$p0, last_name=$p1, middle_initial=$p2,
                   nickname=$p3, name=$p4 WHERE id=$p5",
                p.First, p.Last, p.Mi, p.Nick, display.Length > 0 ? display : name, id);
        }
    }

    // ------------------------------------------------------------------ //
    // Members
    // ------------------------------------------------------------------ //
    public List<Member> GetMembers(bool activeOnly = false, string? search = null)
    {
        var sql = "SELECT * FROM members";
        var clauses = new List<string>();
        var args = new List<object?>();
        if (activeOnly) clauses.Add("active = 1");
        if (!string.IsNullOrEmpty(search))
        {
            var s = "%" + search.ToLowerInvariant() + "%";
            clauses.Add("(LOWER(name) LIKE $p0 OR LOWER(first_name) LIKE $p0 " +
                        "OR LOWER(last_name) LIKE $p0 OR LOWER(IFNULL(nickname,'')) LIKE $p0 " +
                        "OR LOWER(IFNULL(middle_initial,'')) LIKE $p0)");
            args.Add(s);
        }
        if (clauses.Count > 0) sql += " WHERE " + string.Join(" AND ", clauses);
        sql += " ORDER BY last_name COLLATE NOCASE, first_name COLLATE NOCASE, name COLLATE NOCASE";
        return Query(sql, MapMember, args.ToArray());
    }

    public long CountMembers() => Scalar("SELECT COUNT(*) FROM members");
    public long CountActiveMembers() => Scalar("SELECT COUNT(*) FROM members WHERE active = 1");

    public Member? GetMember(long id) =>
        Query("SELECT * FROM members WHERE id = $p0", MapMember, id).FirstOrDefault();

    private static (string, string, string, string) CleanNameParts(string? first, string? last, string? mi, string? nick)
    {
        var f = (first ?? "").Trim();
        var l = (last ?? "").Trim();
        var m = (mi ?? "").Trim().TrimEnd('.');
        var n = (nick ?? "").Trim();
        if (f.Length == 0) throw new ValidationException("First name is required.");
        if (l.Length == 0) throw new ValidationException("Last name is required.");
        if (m.Length > 1) throw new ValidationException("Middle initial must be a single character.");
        return (f, l, m, n);
    }

    public long AddMember(string first, string last = "", string mi = "", string nick = "",
                          bool active = true, string radio = "")
    {
        var (f, l, m, n) = CleanNameParts(first, last, mi, nick);
        var display = FormatMemberName(f, l, m, n);
        radio = (radio ?? "").Trim();
        if (active && CountActiveMembers() >= MaxMembers)
            throw new ValidationException(
                $"Cannot add member: the maximum of {MaxMembers} active members has been reached. " +
                "Deactivate a current member first, or add this person as inactive.");
        if (Scalar("SELECT COUNT(*) FROM members WHERE LOWER(name) = $p0", display.ToLowerInvariant()) > 0)
            throw new ValidationException($"A member named '{display}' already exists.");
        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Insert(@"INSERT INTO members
            (first_name, last_name, middle_initial, nickname, name, active, created_at,
             join_date, inactive_date, radio_number)
            VALUES ($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9)",
            f, l, m, n, display, active ? 1 : 0,
            now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            today, active ? "" : today, radio);
    }

    /// <summary>Update a member. Null arguments leave the field unchanged.</summary>
    public void UpdateMember(long id, string? first = null, string? last = null, string? mi = null,
                             string? nick = null, bool? active = null, string? radio = null)
    {
        var mem = GetMember(id) ?? throw new ValidationException("Member not found.");
        var (f, l, m, n) = CleanNameParts(first ?? mem.FirstName, last ?? mem.LastName,
                                          mi ?? mem.MiddleInitial, nick ?? mem.Nickname);
        var display = FormatMemberName(f, l, m, n);
        var newRadio = radio is null ? mem.RadioNumber : radio.Trim();
        if (Scalar("SELECT COUNT(*) FROM members WHERE LOWER(name) = $p0 AND id != $p1",
                display.ToLowerInvariant(), id) > 0)
            throw new ValidationException($"A member named '{display}' already exists.");
        var newActive = active ?? mem.Active;
        if (newActive && !mem.Active && CountActiveMembers() >= MaxMembers)
            throw new ValidationException(
                $"Cannot activate member: the maximum of {MaxMembers} active members has been reached. " +
                "Deactivate a current member first.");
        var newInactive = mem.InactiveDate;
        if (newActive && !mem.Active) newInactive = "";
        else if (!newActive && mem.Active) newInactive = Today;
        Exec(@"UPDATE members SET first_name=$p0, last_name=$p1, middle_initial=$p2, nickname=$p3,
               name=$p4, active=$p5, inactive_date=$p6, radio_number=$p7 WHERE id=$p8",
            f, l, m, n, display, newActive ? 1 : 0, newInactive, newRadio, id);
    }

    public void SetMemberActive(long id, bool active)
    {
        if (active)
        {
            var mem = GetMember(id);
            if (mem != null && !mem.Active && CountActiveMembers() >= MaxMembers)
                throw new ValidationException(
                    $"Cannot activate member: the maximum of {MaxMembers} active members has been reached. " +
                    "Deactivate a current member first.");
            Exec("UPDATE members SET active = 1, inactive_date = '' WHERE id = $p0", id);
        }
        else
        {
            Exec("UPDATE members SET active = 0, inactive_date = $p0 WHERE id = $p1", Today, id);
        }
    }

    private static List<Member> SortRoster(IEnumerable<Member> list) =>
        list.OrderBy(m => m.LastName.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(m => m.FirstName.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(m => m.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();

    /// <summary>Members eligible to be marked at an event on <paramref name="eventDate"/> (ISO).</summary>
    public List<Member> GetMembersForEvent(string eventDate)
    {
        var result = new List<Member>();
        foreach (var m in Query("SELECT * FROM members", MapMember))
        {
            var join = m.JoinDate.Trim();
            if (join.Length > 0 && !string.IsNullOrEmpty(eventDate) && string.CompareOrdinal(join, eventDate) > 0)
                continue;
            if (m.Active) { result.Add(m); continue; }
            var inact = m.InactiveDate.Trim();
            if (inact.Length > 0 && !string.IsNullOrEmpty(eventDate) && string.CompareOrdinal(eventDate, inact) < 0)
                result.Add(m);
        }
        return SortRoster(result);
    }

    /// <summary>Members who were active at some point during the given quarter.</summary>
    public List<Member> GetMembersForQuarter(int year, int quarter, bool activeOnly = false)
    {
        var months = QuarterMonths[quarter];
        var qStart = $"{year:D4}-{months[0]:D2}-01";
        var qEnd = $"{year:D4}-{months[2]:D2}-{DateTime.DaysInMonth(year, months[2]):D2}";
        var result = new List<Member>();
        foreach (var m in Query("SELECT * FROM members", MapMember))
        {
            if (activeOnly && !m.Active) continue;
            var join = m.JoinDate.Trim();
            if (join.Length > 0 && string.CompareOrdinal(join, qEnd) > 0) continue;
            if (m.Active) { result.Add(m); continue; }
            var inact = m.InactiveDate.Trim();
            if (inact.Length > 0 && string.CompareOrdinal(inact, qStart) >= 0) result.Add(m);
        }
        return SortRoster(result);
    }

    /// <summary>Update join/inactive dates (ISO). Null means unchanged; "" clears inactive date.</summary>
    public void UpdateMemberDates(long id, string? joinDate = null, string? inactiveDate = null)
    {
        var mem = GetMember(id) ?? throw new ValidationException("Member not found.");
        var newJoin = joinDate is null ? mem.JoinDate : joinDate.Trim();
        var newInactive = inactiveDate is null ? mem.InactiveDate : inactiveDate.Trim();
        var today = Today;
        if (newJoin.Length > 0)
        {
            if (!TryParseIso(newJoin, out _))
                throw new ValidationException("Join date must be in YYYY-MM-DD format.");
            if (string.CompareOrdinal(newJoin, today) > 0)
                throw new ValidationException("Join date cannot be in the future.");
        }
        if (newInactive.Length > 0 && mem.Active)
            throw new ValidationException("Inactive date can only be set for inactive members.");
        if (newInactive.Length > 0)
        {
            if (!TryParseIso(newInactive, out _))
                throw new ValidationException("Inactive date must be in YYYY-MM-DD format.");
            if (newJoin.Length > 0 && string.CompareOrdinal(newInactive, newJoin) < 0)
                throw new ValidationException("Inactive date must be on or after the join date.");
        }
        Exec("UPDATE members SET join_date = $p0, inactive_date = $p1 WHERE id = $p2", newJoin, newInactive, id);
    }

    public MemberTotals MemberAttendanceTotals(long memberId) => new()
    {
        MeetingsAttended = Scalar(@"SELECT COUNT(*) FROM attendance a JOIN events e ON a.event_id = e.id
            WHERE a.member_id = $p0 AND e.event_type = 'meeting' AND a.present = 1", memberId),
        CallsAttended = Scalar(@"SELECT COALESCE(SUM(e.call_multiplier), 0) FROM attendance a
            JOIN events e ON a.event_id = e.id
            WHERE a.member_id = $p0 AND e.event_type = 'call' AND a.present = 1", memberId),
    };

    public List<string> GetDistinctCallTypes() =>
        Query(@"SELECT DISTINCT TRIM(title) AS t FROM events
                WHERE title IS NOT NULL AND TRIM(title) != '' ORDER BY t COLLATE NOCASE",
            r => Str(r, "t"));

    public void DeleteMember(long id) => Exec("DELETE FROM members WHERE id = $p0", id);

    // ------------------------------------------------------------------ //
    // Quarters
    // ------------------------------------------------------------------ //
    public long GetOrCreateQuarter(int year, int quarter)
    {
        var id = Scalar("SELECT id FROM quarters WHERE year = $p0 AND quarter = $p1", year, quarter);
        if (id > 0) return id;
        return Insert("INSERT INTO quarters (year, quarter) VALUES ($p0, $p1)", year, quarter);
    }

    private static Quarter MapQuarter(SqliteDataReader r) => new()
    {
        Id = Lng(r, "id"), Year = (int)Lng(r, "year"), Number = (int)Lng(r, "quarter"),
    };

    public Quarter? GetQuarter(long id) =>
        Query("SELECT * FROM quarters WHERE id = $p0", MapQuarter, id).FirstOrDefault();

    public List<Quarter> ListQuarters() => Query("SELECT * FROM quarters ORDER BY year, quarter", MapQuarter);

    // ------------------------------------------------------------------ //
    // Events
    // ------------------------------------------------------------------ //
    public long CountMeetings(long quarterId) =>
        Scalar("SELECT COUNT(*) FROM events WHERE quarter_id = $p0 AND event_type = 'meeting'", quarterId);

    private static int CleanMultiplier(int value)
    {
        if (value < 1 || value > 3) throw new ValidationException("Multiplier must be 1, 2 or 3.");
        return value;
    }

    public long GetOrCreateQuarterForDate(string eventDate)
    {
        if (!TryParseIso(eventDate, out var dt)) throw new ValidationException("Date must be in YYYY-MM-DD format.");
        return GetOrCreateQuarter(dt.Year, QuarterForMonth(dt.Month));
    }

    /// <summary>Create a meeting or call. The quarter is derived from the date.</summary>
    public long AddEvent(string eventType, string eventDate, string location = "", string title = "",
                         string? callCategory = null, int callMultiplier = 1)
    {
        if (eventType != "meeting" && eventType != "call")
            throw new ValidationException("event_type must be 'meeting' or 'call'.");
        if (!TryParseIso(eventDate, out var dt)) throw new ValidationException("Date must be in YYYY-MM-DD format.");
        var qid = GetOrCreateQuarter(dt.Year, QuarterForMonth(dt.Month));
        if (eventType == "meeting")
        {
            if (CountMeetings(qid) >= MeetingsPerQuarter)
                throw new ValidationException($"This quarter already has the maximum of {MeetingsPerQuarter} meetings.");
            callCategory = null;
            callMultiplier = 1;
        }
        else
        {
            if (callCategory != "in_town" && callCategory != "out_of_town")
                throw new ValidationException("Calls must be categorized as 'in_town' or 'out_of_town'.");
            callMultiplier = CleanMultiplier(callMultiplier);
        }
        return Insert(@"INSERT INTO events (quarter_id, event_type, title, location, call_category,
                        event_date, month, call_multiplier) VALUES ($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7)",
            qid, eventType, (title ?? "").Trim(), (location ?? "").Trim(), callCategory, eventDate, dt.Month,
            callMultiplier);
    }

    public void UpdateEvent(long id, string? eventDate = null, string? location = null, string? title = null,
                            string? callCategory = null, string? eventType = null, int? callMultiplier = null)
    {
        var ev = GetEvent(id) ?? throw new ValidationException("Event not found.");
        var newType = eventType ?? ev.EventType;
        if (newType != "meeting" && newType != "call")
            throw new ValidationException("event_type must be 'meeting' or 'call'.");
        var newDate = eventDate ?? ev.EventDate;
        var month = ev.Month;
        var targetQ = ev.QuarterId;
        if (eventDate != null)
        {
            if (!TryParseIso(eventDate, out var dt)) throw new ValidationException("Date must be in YYYY-MM-DD format.");
            month = dt.Month;
            targetQ = GetOrCreateQuarter(dt.Year, QuarterForMonth(dt.Month));
        }
        if (newType == "meeting")
        {
            var already = ev.EventType == "meeting" && targetQ == ev.QuarterId;
            if (!already && CountMeetings(targetQ) >= MeetingsPerQuarter)
                throw new ValidationException(
                    $"Cannot save as a meeting: that quarter already has the maximum of {MeetingsPerQuarter} meetings.");
        }
        var newLoc = location is null ? ev.Location : location.Trim();
        var newTitle = title is null ? ev.Title : title.Trim();
        string? newCat;
        int newMult;
        if (newType == "call")
        {
            newCat = callCategory ?? ev.CallCategory;
            if (newCat != "in_town" && newCat != "out_of_town")
                throw new ValidationException("Calls must be 'in_town' or 'out_of_town'.");
            newMult = callMultiplier is null ? ev.CallMultiplier : CleanMultiplier(callMultiplier.Value);
        }
        else { newCat = null; newMult = 1; }
        Exec(@"UPDATE events SET quarter_id=$p0, event_type=$p1, event_date=$p2, month=$p3, location=$p4,
               title=$p5, call_category=$p6, call_multiplier=$p7 WHERE id=$p8",
            targetQ, newType, newDate, month, newLoc, newTitle, newCat, newMult, id);
    }

    public void DeleteEvent(long id) => Exec("DELETE FROM events WHERE id = $p0", id);

    public Event? GetEvent(long id) =>
        Query("SELECT * FROM events WHERE id = $p0", MapEvent, id).FirstOrDefault();

    public List<Event> GetEvents(long quarterId, string? eventType = null, int? month = null)
    {
        var sql = "SELECT * FROM events WHERE quarter_id = $p0";
        var args = new List<object?> { quarterId };
        if (!string.IsNullOrEmpty(eventType)) { sql += $" AND event_type = $p{args.Count}"; args.Add(eventType); }
        if (month is > 0) { sql += $" AND month = $p{args.Count}"; args.Add(month); }
        sql += " ORDER BY event_date, id";
        return Query(sql, MapEvent, args.ToArray());
    }

    public List<Event> GetAllEvents() =>
        Query(@"SELECT e.*, q.year AS year, q.quarter AS quarter FROM events e
                JOIN quarters q ON e.quarter_id = q.id ORDER BY e.event_date, e.id", MapEvent);

    /// <summary>Events for a quarter with year/quarter populated.</summary>
    public List<Event> GetEventsWithQuarter(long quarterId) =>
        Query(@"SELECT e.*, q.year AS year, q.quarter AS quarter FROM events e
                JOIN quarters q ON e.quarter_id = q.id WHERE e.quarter_id = $p0
                ORDER BY e.event_date, e.id", MapEvent, quarterId);

    public long CountEventAttendees(long eventId) =>
        Scalar("SELECT COUNT(*) FROM attendance WHERE event_id = $p0 AND present = 1", eventId);

    /// <summary>Present counts for every event in one query (event_id → count).</summary>
    public Dictionary<long, long> AttendeeCountsByEvent() =>
        Query("SELECT event_id, COUNT(*) AS c FROM attendance WHERE present = 1 GROUP BY event_id",
            r => (Lng(r, "event_id"), Lng(r, "c"))).ToDictionary(t => t.Item1, t => t.Item2);

    public string GetEventMinutes(long eventId) =>
        Query("SELECT minutes FROM events WHERE id = $p0", r => Str(r, "minutes"), eventId).FirstOrDefault() ?? "";

    public void SaveEventMinutes(long eventId, string? minutes) =>
        Exec("UPDATE events SET minutes = $p0 WHERE id = $p1", minutes ?? "", eventId);

    // ------------------------------------------------------------------ //
    // Attendance
    // ------------------------------------------------------------------ //
    private const string UpsertAttendance = @"INSERT INTO attendance (event_id, member_id, present)
        VALUES ($p0, $p1, $p2) ON CONFLICT(event_id, member_id) DO UPDATE SET present = excluded.present";

    public void SetAttendance(long eventId, long memberId, bool present) =>
        Exec(UpsertAttendance, eventId, memberId, present ? 1 : 0);

    public void SaveEventAttendance(long eventId, IDictionary<long, bool> presence)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var (mid, p) in presence)
            {
                using var c = Cmd(UpsertAttendance, eventId, mid, p ? 1 : 0);
                c.Transaction = tx;
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public Dictionary<long, bool> GetEventAttendance(long eventId) =>
        Query("SELECT member_id, present FROM attendance WHERE event_id = $p0",
            r => (Lng(r, "member_id"), Lng(r, "present") != 0), eventId).ToDictionary(t => t.Item1, t => t.Item2);

    // ------------------------------------------------------------------ //
    // Statistics
    // ------------------------------------------------------------------ //
    public MemberStats MemberStats(long memberId, long quarterId)
    {
        var q = GetQuarter(quarterId) ?? throw new ValidationException("Quarter not found.");
        var stats = new MemberStats { MemberId = memberId, TotalMeetingsPossible = CountMeetings(quarterId) };
        stats.MeetingsAttended = Scalar(@"SELECT COUNT(*) FROM attendance a JOIN events e ON a.event_id = e.id
            WHERE a.member_id = $p0 AND e.quarter_id = $p1 AND e.event_type = 'meeting' AND a.present = 1",
            memberId, quarterId);
        foreach (var m in QuarterMonths[q.Number])
        {
            const string callSql = @"SELECT COALESCE(SUM(e.call_multiplier), 0) FROM attendance a
                JOIN events e ON a.event_id = e.id WHERE a.member_id = $p0 AND e.quarter_id = $p1
                AND e.month = $p2 AND e.event_type = 'call' AND e.call_category = $p3 AND a.present = 1";
            stats.Breakdown[m] = new MonthBreakdown
            {
                MonthName = MonthNames[m],
                InTown = Scalar(callSql, memberId, quarterId, m, "in_town"),
                OutOfTown = Scalar(callSql, memberId, quarterId, m, "out_of_town"),
                Meetings = Scalar(@"SELECT COUNT(*) FROM attendance a JOIN events e ON a.event_id = e.id
                    WHERE a.member_id = $p0 AND e.quarter_id = $p1 AND e.month = $p2
                    AND e.event_type = 'meeting' AND a.present = 1", memberId, quarterId, m),
            };
        }
        stats.TotalInTown = stats.Breakdown.Values.Sum(b => b.InTown);
        stats.TotalOutOfTown = stats.Breakdown.Values.Sum(b => b.OutOfTown);
        stats.TotalCalls = stats.TotalInTown + stats.TotalOutOfTown;
        stats.MeetingPct = stats.TotalMeetingsPossible > 0
            ? stats.MeetingsAttended * 100.0 / stats.TotalMeetingsPossible : 0.0;
        return stats;
    }

    public DepartmentStats DepartmentStats()
    {
        var active = CountActiveMembers();
        var total = CountMembers();
        return new DepartmentStats
        {
            ActiveMembers = active,
            InactiveMembers = total - active,
            TotalMembers = total,
            TotalMeetings = Scalar("SELECT COUNT(*) FROM events WHERE event_type = 'meeting'"),
            TotalCalls = Scalar("SELECT COUNT(*) FROM events WHERE event_type = 'call'"),
            TotalCallCredits = Scalar("SELECT COALESCE(SUM(call_multiplier), 0) FROM events WHERE event_type = 'call'"),
            AttendanceMarks = Scalar("SELECT COUNT(*) FROM attendance WHERE present = 1"),
        };
    }

    public EventsOverview EventsOverview(long? quarterId = null)
    {
        var sql = "SELECT event_type, month, call_category, call_multiplier FROM events";
        var args = new List<object?>();
        if (quarterId != null) { sql += " WHERE quarter_id = $p0"; args.Add(quarterId); }
        var ov = new EventsOverview();
        foreach (var (type, month, mult) in Query(sql,
                     r => (Str(r, "event_type"), (int)Lng(r, "month"), Lng(r, "call_multiplier", 1)), args.ToArray()))
        {
            if (!ov.ByMonth.TryGetValue(month, out var slot)) ov.ByMonth[month] = slot = new MonthSlot();
            if (type == "meeting") { ov.TotalMeetings++; slot.Meetings++; }
            else
            {
                var m = mult == 0 ? 1 : mult;
                ov.TotalCalls++; ov.TotalCallCredits += m;
                slot.Calls++; slot.CallCredits += m;
            }
        }
        return ov;
    }

    public AccountingStats AccountingStats(long quarterId)
    {
        CategoryCounts Counts(string where) => new()
        {
            Count = Scalar($"SELECT COUNT(*) FROM events e WHERE {where}", quarterId),
            Attendees = Scalar($@"SELECT COALESCE(SUM(e.call_multiplier), 0) FROM attendance a
                JOIN events e ON a.event_id = e.id WHERE a.present = 1 AND {where}", quarterId),
        };
        return new AccountingStats
        {
            OutOfTown = Counts("e.quarter_id = $p0 AND e.event_type = 'call' AND e.call_category = 'out_of_town'"),
            InTown = Counts("e.quarter_id = $p0 AND e.event_type = 'call' AND e.call_category = 'in_town'"),
            Meetings = Counts("e.quarter_id = $p0 AND e.event_type = 'meeting'"),
        };
    }
}
