using ClosedXML.Excel;

namespace FireDeptAttendance.Core;

/// <summary>Formatted quarterly attendance workbook — port of <c>excel_export.py</c>.</summary>
public static class ExcelExporter
{
    private static readonly XLColor TitleFill = XLColor.FromHtml("#B22222");
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#8B0000");
    private static readonly XLColor SubheaderFill = XLColor.FromHtml("#C0392B");
    private static readonly XLColor MonthFill = XLColor.FromHtml("#34495E");
    private static readonly XLColor TotalFill = XLColor.FromHtml("#F2F2F2");
    private static readonly XLColor BorderColor = XLColor.FromHtml("#999999");
    private static readonly XLColor InactiveColor = XLColor.FromHtml("#888888");

    private enum Align { Center, Left, CenterNoWrap }

    private static void Style(IXLRange range, bool bold = false, double? size = null, XLColor? color = null,
                              XLColor? fill = null, Align align = Align.Center, bool border = true,
                              bool italic = false)
    {
        var s = range.Style;
        if (bold) s.Font.Bold = true;
        if (italic) s.Font.Italic = true;
        if (size.HasValue) s.Font.FontSize = size.Value;
        if (color != null) s.Font.FontColor = color;
        if (fill != null) s.Fill.BackgroundColor = fill;
        switch (align)
        {
            case Align.Center:
                s.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                s.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                s.Alignment.WrapText = true;
                break;
            case Align.Left:
                s.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                s.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                break;
            case Align.CenterNoWrap:
                s.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                break;
        }
        if (border)
        {
            s.Border.OutsideBorder = XLBorderStyleValues.Thin;
            s.Border.OutsideBorderColor = BorderColor;
            s.Border.InsideBorder = XLBorderStyleValues.Thin;
            s.Border.InsideBorderColor = BorderColor;
        }
    }

    private static void Style(IXLCell cell, bool bold = false, double? size = null, XLColor? color = null,
                              XLColor? fill = null, Align align = Align.Center, bool border = true, bool italic = false)
        => Style(cell.AsRange(), bold, size, color, fill, align, border, italic);

    private static IXLRange Merge(IXLWorksheet ws, int r1, int c1, int r2, int c2)
    {
        var rng = ws.Range(r1, c1, r2, c2);
        if (r1 != r2 || c1 != c2) rng.Merge();
        return rng;
    }

    private static string SheetName(string name) => name.Length > 31 ? name[..31] : name;

    /// <summary>Write the workbook. <paramref name="quarterIds"/> null = every quarter in the DB.</summary>
    public static void ExportQuarterWorkbook(Database db, string path, IList<long>? quarterIds = null,
                                             bool activeOnly = false, bool includeRoster = false)
    {
        quarterIds ??= db.ListQuarters().Select(q => q.Id).ToList();
        using var wb = new XLWorkbook();
        if (quarterIds.Count == 0)
        {
            wb.AddWorksheet("No Data").Cell("A1").Value = "No quarters have been created yet.";
            wb.SaveAs(path);
            return;
        }
        bool single = quarterIds.Count == 1;
        foreach (var qid in quarterIds)
        {
            var q = db.GetQuarter(qid);
            if (q is null) continue;
            BuildSummary(wb, db, q, activeOnly, single);
            BuildDetails(wb, db, q, activeOnly, single);
        }
        if (includeRoster) BuildRoster(wb, db, activeOnly);
        wb.SaveAs(path);
    }

    private static void BuildSummary(XLWorkbook wb, Database db, Quarter q, bool activeOnly, bool single)
    {
        var months = Database.QuarterMonths[q.Number];
        var members = db.GetMembersForQuarter(q.Year, q.Number, activeOnly);
        var ws = wb.AddWorksheet(SheetName(single ? "Summary" : $"Q{q.Number} {q.Year} Summary"));
        var totalMeetings = db.CountMeetings(q.Id);
        int lastCol = 2 + months.Length * 2 + 2;

        var title = Merge(ws, 1, 1, 1, lastCol);
        title.FirstCell().Value = $"Fire Department Attendance  -  Q{q.Number} {q.Year}";
        Style(title, bold: true, size: 14, color: XLColor.White, fill: TitleFill);
        ws.Row(1).Height = 26;

        var sub = Merge(ws, 2, 1, 2, lastCol);
        sub.FirstCell().Value = $"Meetings held this quarter: {totalMeetings} of {Database.MeetingsPerQuarter}   " +
                                "(call counts are weighted by each call's multiplier — a x2 call counts as 2)";
        Style(sub, italic: true, size: 10, align: Align.CenterNoWrap);

        const int hr = 3, sr = 4;
        var m1 = Merge(ws, hr, 1, sr, 1);
        m1.FirstCell().Value = "Member";
        Style(m1, bold: true, color: XLColor.White, fill: HeaderFill);
        var m2 = Merge(ws, hr, 2, sr, 2);
        m2.FirstCell().Value = $"Meetings\n(out of {Database.MeetingsPerQuarter})";
        Style(m2, bold: true, color: XLColor.White, fill: HeaderFill);

        int col = 3;
        void SubHeaders(int c)
        {
            ws.Cell(sr, c).Value = "In-Town";
            ws.Cell(sr, c + 1).Value = "Out-of-Town";
            Style(ws.Range(sr, c, sr, c + 1), bold: true, size: 9, color: XLColor.White, fill: SubheaderFill);
        }
        foreach (var m in months)
        {
            var g = Merge(ws, hr, col, hr, col + 1);
            g.FirstCell().Value = Database.MonthNames[m];
            Style(g, bold: true, color: XLColor.White, fill: MonthFill);
            SubHeaders(col);
            col += 2;
        }
        var tg = Merge(ws, hr, col, hr, col + 1);
        tg.FirstCell().Value = "Quarter Call Totals";
        Style(tg, bold: true, color: XLColor.White, fill: HeaderFill);
        SubHeaders(col);

        int dataStart = sr + 1, r = dataStart;
        foreach (var mem in members)
        {
            var st = db.MemberStats(mem.Id, q.Id);
            var nc = ws.Cell(r, 1);
            nc.Value = mem.Name;
            Style(nc, align: Align.Left);
            if (!mem.Active) { nc.Style.Font.Italic = true; nc.Style.Font.FontColor = InactiveColor; }
            var mc = ws.Cell(r, 2);
            mc.Value = $"{st.MeetingsAttended} / {totalMeetings}";
            Style(mc);
            col = 3;
            foreach (var m in months)
            {
                var b = st.Breakdown[m];
                ws.Cell(r, col).Value = b.InTown;
                ws.Cell(r, col + 1).Value = b.OutOfTown;
                Style(ws.Range(r, col, r, col + 1));
                col += 2;
            }
            ws.Cell(r, col).Value = st.TotalInTown;
            ws.Cell(r, col + 1).Value = st.TotalOutOfTown;
            Style(ws.Range(r, col, r, col + 1), fill: TotalFill);
            r++;
        }
        if (members.Count == 0)
        {
            var nm = Merge(ws, r, 1, r, lastCol);
            nm.FirstCell().Value = "No members to display.";
            Style(nm, align: Align.CenterNoWrap);
        }
        ws.Column(1).Width = 24;
        ws.Column(2).Width = 12;
        for (int c = 3; c <= lastCol; c++) ws.Column(c).Width = 12;
        if (members.Count > 0) ws.SheetView.Freeze(dataStart - 1, 1);
    }

    private static string Group(Event ev) =>
        ev.IsMeeting ? "meetings" : ev.CallCategory == "out_of_town" ? "out_of_town" : "in_town";

    private static void BuildDetails(XLWorkbook wb, Database db, Quarter q, bool activeOnly, bool single)
    {
        var events = db.GetEvents(q.Id);
        foreach (var (key, label) in new[]
                 { ("meetings", "Meetings"), ("in_town", "In-Town Calls"), ("out_of_town", "Out-of-Town Calls") })
            BuildOneDetail(wb, db, q, activeOnly, single, label, events.Where(e => Group(e) == key).ToList());
    }

    private static int Mult(Event ev) => ev.IsCall ? Math.Max(1, ev.CallMultiplier) : 1;

    private static void BuildOneDetail(XLWorkbook wb, Database db, Quarter q, bool activeOnly, bool single,
                                       string label, List<Event> events)
    {
        var members = db.GetMembersForQuarter(q.Year, q.Number, activeOnly);
        var ws = wb.AddWorksheet(SheetName(single ? label : $"Q{q.Number} {q.Year} {label}"));
        var attendance = events.ToDictionary(e => e.Id, e => db.GetEventAttendance(e.Id));
        int n = events.Count, firstEv = 2, totalCol = firstEv + n, lastCol = totalCol;

        var title = Merge(ws, 1, 1, 1, lastCol);
        title.FirstCell().Value = $"{label}  -  Q{q.Number} {q.Year}";
        Style(title, bold: true, size: 14, color: XLColor.White, fill: TitleFill);
        ws.Row(1).Height = 26;

        var sub = Merge(ws, 2, 1, 2, lastCol);
        sub.FirstCell().Value = $"Members are listed down the left; each column is one entry in \"{label}\". " +
                                "A number marks attendance and equals the call\u2019s multiplier (1, 2 or 3), " +
                                "so the row and column totals add up correctly.";
        Style(sub, italic: true, size: 10, align: Align.CenterNoWrap);

        const int hDate = 3, hType = 4, hLoc = 5, dataStart = 6;
        var mh = Merge(ws, hDate, 1, hLoc, 1);
        mh.FirstCell().Value = "Member";
        Style(mh, bold: true, color: XLColor.White, fill: HeaderFill);
        var th = Merge(ws, hDate, totalCol, hLoc, totalCol);
        th.FirstCell().Value = "Total";
        Style(th, bold: true, color: XLColor.White, fill: HeaderFill);

        for (int j = 0; j < n; j++)
        {
            var ev = events[j];
            int col = firstEv + j;
            var typeLabel = ev.IsMeeting ? "Meeting" : ev.IsCall ? "Call" :
                (ev.EventType.Length > 0 ? char.ToUpper(ev.EventType[0]) + ev.EventType[1..].ToLower() : "");
            if (!string.IsNullOrEmpty(ev.CallCategory)) typeLabel += $" ({ev.CallCategory.Replace('_', '-')})";
            if (ev.IsCall && ev.CallMultiplier > 1) typeLabel += $"  x{ev.CallMultiplier}";
            ws.Cell(hDate, col).Value = DateUtil.IsoToUs(ev.EventDate);
            ws.Cell(hType, col).Value = typeLabel;
            ws.Cell(hLoc, col).Value = string.IsNullOrEmpty(ev.Location) ? "—" : ev.Location;
            Style(ws.Cell(hDate, col), bold: true, size: 9, color: XLColor.White, fill: HeaderFill);
            Style(ws.Cell(hType, col), bold: true, size: 9, color: XLColor.White, fill: MonthFill);
            Style(ws.Cell(hLoc, col), size: 9, color: XLColor.White, fill: MonthFill);
        }

        int r = dataStart;
        foreach (var mem in members)
        {
            var nc = ws.Cell(r, 1);
            nc.Value = mem.Name;
            Style(nc, align: Align.Left);
            if (!mem.Active) { nc.Style.Font.Italic = true; nc.Style.Font.FontColor = InactiveColor; }
            long rowTotal = 0;
            for (int j = 0; j < n; j++)
            {
                var ev = events[j];
                var c = ws.Cell(r, firstEv + j);
                if (attendance[ev.Id].TryGetValue(mem.Id, out var p) && p)
                {
                    c.Value = Mult(ev);
                    rowTotal += Mult(ev);
                }
                Style(c);
            }
            ws.Cell(r, totalCol).Value = rowTotal;
            Style(ws.Cell(r, totalCol), fill: TotalFill);
            r++;
        }
        if (members.Count == 0)
        {
            var nm = Merge(ws, r, 1, r, n > 0 ? lastCol : 1);
            nm.FirstCell().Value = "No members to display.";
            Style(nm, align: Align.CenterNoWrap);
            r++;
        }
        if (members.Count > 0)
        {
            var lc = ws.Cell(r, 1);
            lc.Value = "Total";
            Style(lc, bold: true, fill: TotalFill, align: Align.Left);
            long grand = 0;
            for (int j = 0; j < n; j++)
            {
                var ev = events[j];
                long colTotal = attendance[ev.Id].Values.Count(v => v) * (long)Mult(ev);
                grand += colTotal;
                ws.Cell(r, firstEv + j).Value = colTotal;
                Style(ws.Cell(r, firstEv + j), bold: true, fill: TotalFill);
            }
            ws.Cell(r, totalCol).Value = grand;
            Style(ws.Cell(r, totalCol), bold: true, fill: TotalFill);
            r++;
        }
        if (n == 0)
        {
            var note = Merge(ws, r, 1, r, Math.Max(lastCol, 1));
            note.FirstCell().Value = $"No {label.ToLowerInvariant()} recorded this quarter.";
            Style(note, align: Align.CenterNoWrap);
        }
        ws.Column(1).Width = 28;
        for (int j = 0; j < n; j++) ws.Column(firstEv + j).Width = 13;
        ws.Column(totalCol).Width = 14;
        ws.Row(hDate).Height = 16;
        ws.Row(hType).Height = 16;
        ws.Row(hLoc).Height = 28;
        if (members.Count > 0) ws.SheetView.Freeze(dataStart - 1, 1);
    }

    private static void BuildRoster(XLWorkbook wb, Database db, bool activeOnly)
    {
        var members = db.GetMembers(activeOnly);
        var ws = wb.AddWorksheet("Member Roster");
        string[] headers = { "Display Name", "First", "M.I.", "Last", "Nickname", "Radio #", "Status", "Join Date",
                             "Time of Service", "Milestone" };
        var title = Merge(ws, 1, 1, 1, headers.Length);
        title.FirstCell().Value = "Member Roster";
        Style(title, bold: true, size: 14, color: XLColor.White, fill: TitleFill);
        ws.Row(1).Height = 26;
        for (int i = 0; i < headers.Length; i++) ws.Cell(2, i + 1).Value = headers[i];
        Style(ws.Range(2, 1, 2, headers.Length), bold: true, color: XLColor.White, fill: HeaderFill);

        int r = 3;
        foreach (var m in members)
        {
            var join = m.JoinDate.Trim();
            string? end = m.Active ? null : m.InactiveDate.Trim();
            var svc = DateUtil.FormatServiceTime(join, end);
            var (emblem, ms) = DateUtil.MilestoneEmblem(join, end);
            string[] vals =
            {
                m.Name, m.FirstName, m.MiddleInitial, m.LastName, m.Nickname, m.RadioNumber,
                m.Active ? "Active" : "Inactive", join.Length > 0 ? DateUtil.IsoToUs(join) : "", svc,
                emblem.Length > 0 ? $"{emblem} {ms}-yr" : "",
            };
            for (int i = 0; i < vals.Length; i++)
            {
                var c = ws.Cell(r, i + 1);
                if (vals[i].Length > 0) c.Value = vals[i];   // leave truly blank like openpyxl
                Style(c, align: (i + 1) is 1 or 2 or 4 or 5 or 9 ? Align.Left : Align.Center);
                if (!m.Active) { c.Style.Font.Italic = true; c.Style.Font.FontColor = InactiveColor; }
            }
            r++;
        }
        if (members.Count == 0)
        {
            var nm = Merge(ws, r, 1, r, headers.Length);
            nm.FirstCell().Value = "No members to display.";
            Style(nm, align: Align.CenterNoWrap);
        }
        double[] widths = { 28, 16, 6, 16, 16, 10, 10, 12, 22, 12 };
        for (int i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        if (members.Count > 0) ws.SheetView.Freeze(2, 0);
    }
}
