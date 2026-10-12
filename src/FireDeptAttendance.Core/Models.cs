namespace FireDeptAttendance.Core;

/// <summary>A row of the <c>members</c> table.</summary>
public sealed class Member
{
    public long Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string MiddleInitial { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public string CreatedAt { get; set; } = "";
    public string JoinDate { get; set; } = "";
    public string InactiveDate { get; set; } = "";
    public string RadioNumber { get; set; } = "";
}

/// <summary>A row of the <c>quarters</c> table.</summary>
public sealed class Quarter
{
    public long Id { get; set; }
    public int Year { get; set; }
    public int Number { get; set; }
}

/// <summary>A row of the <c>events</c> table (a meeting or a call).</summary>
public sealed class Event
{
    public long Id { get; set; }
    public long QuarterId { get; set; }
    public string EventType { get; set; } = "call";   // "meeting" | "call"
    public string Title { get; set; } = "";
    public string Location { get; set; } = "";
    public string? CallCategory { get; set; }          // "in_town" | "out_of_town" | null
    public string EventDate { get; set; } = "";        // ISO yyyy-MM-dd
    public int Month { get; set; }
    public int CallMultiplier { get; set; } = 1;
    public string Minutes { get; set; } = "";
    // Populated by GetAllEvents (joined from quarters).
    public int Year { get; set; }
    public int QuarterNumber { get; set; }

    public bool IsMeeting => EventType == "meeting";
    public bool IsCall => EventType == "call";
}

public sealed class MonthBreakdown
{
    public string MonthName { get; set; } = "";
    public long InTown { get; set; }
    public long OutOfTown { get; set; }
    public long Meetings { get; set; }
}

public sealed class MemberStats
{
    public long MemberId { get; set; }
    public long TotalMeetingsPossible { get; set; }
    public long MeetingsAttended { get; set; }
    public double MeetingPct { get; set; }
    public long TotalCalls { get; set; }
    public long TotalInTown { get; set; }
    public long TotalOutOfTown { get; set; }
    public SortedDictionary<int, MonthBreakdown> Breakdown { get; } = new();
}

public sealed class DepartmentStats
{
    public long ActiveMembers { get; set; }
    public long InactiveMembers { get; set; }
    public long TotalMembers { get; set; }
    public long TotalMeetings { get; set; }
    public long TotalCalls { get; set; }
    public long TotalCallCredits { get; set; }
    public long AttendanceMarks { get; set; }
}

public sealed class MonthSlot
{
    public long Meetings { get; set; }
    public long Calls { get; set; }
    public long CallCredits { get; set; }
}

public sealed class EventsOverview
{
    public long TotalMeetings { get; set; }
    public long TotalCalls { get; set; }
    public long TotalCallCredits { get; set; }
    public SortedDictionary<int, MonthSlot> ByMonth { get; } = new();
}

public sealed class CategoryCounts
{
    public long Count { get; set; }
    public long Attendees { get; set; }
}

public sealed class AccountingStats
{
    public CategoryCounts OutOfTown { get; set; } = new();
    public CategoryCounts InTown { get; set; } = new();
    public CategoryCounts Meetings { get; set; } = new();
}

public sealed class MemberTotals
{
    public long MeetingsAttended { get; set; }
    public long CallsAttended { get; set; }
}

/// <summary>Validation / business-rule error with a user-facing message.</summary>
public sealed class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }
}
