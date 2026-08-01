namespace CodexBarWindows;

/// <summary>
/// Allocation-free extraction of a calendar date and an hour out of the raw timestamp text that
/// session logs carry.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a <c>Regex.Match(value, "\d{4}-\d{2}-\d{2}")</c> that ran once per usage row in
/// both readers. The static Regex overload takes a lock on the pattern cache and materialises a
/// Match object per call, which is real money on the hot scan path — a cold Codex scan visits
/// hundreds of thousands of lines. The hand-rolled scan below allocates nothing at all.
/// </para>
/// <para>
/// It deliberately reproduces the old semantics rather than improving on them: find the FIRST
/// yyyy-MM-dd anywhere in the string (session file NAMES are matched with the same routine) and
/// take it at face value, without converting time zones. The date a row lands on must not move
/// just because the reader learned to read hours.
/// </para>
/// </remarks>
internal static class UsageTimestampText
{
    /// <summary>
    /// Earliest day a session log can plausibly be ABOUT.
    /// </summary>
    /// <remarks>
    /// A log cannot predate the tool that writes it: Claude Code shipped in 2024 and the Codex CLI
    /// in 2025, so 2023-01-01 clears both by more than a year and cannot exclude a real session.
    /// What it DOES exclude is the two corruptions that actually occur — a missing/zeroed timestamp
    /// parsed as year 0001, and a nanosecond epoch read as seconds — and that matters far beyond one
    /// wrong row: a ledger day number drives shard fan-out, so a single year-0001 row expands a
    /// merge to ~739,000 covered days across ~2,000 year shards and the import appears to hang.
    /// Bounding it at PARSE time is what keeps the bad value out of every downstream structure.
    /// </remarks>
    public static readonly DateOnly EarliestPlausibleDay = new(2023, 1, 1);

    /// <summary>
    /// Days past today a timestamp may still claim. A log is written in its own frame and read in
    /// another (+14:00 is the widest real offset), plus a little clock skew; anything beyond that is
    /// corrupt rather than future, and future days are the cheap half of the fan-out problem.
    /// </summary>
    public const int FutureSlackDays = 2;

    /// <summary>True when a calendar day is one a session log could honestly be about.</summary>
    public static bool IsPlausibleDay(DateOnly day)
        => day >= EarliestPlausibleDay &&
            day.DayNumber <= DateOnly.FromDateTime(DateTime.UtcNow).DayNumber + FutureSlackDays;

    /// <summary>
    /// Finds the first <c>yyyy-MM-dd</c> in <paramref name="value"/>. <paramref name="index"/>
    /// receives the offset of the year digit so a caller can keep scanning for the time part.
    /// </summary>
    public static bool TryFindDate(string? value, out int index, out int year, out int month, out int day)
    {
        index = -1;
        year = 0;
        month = 0;
        day = 0;
        if (string.IsNullOrEmpty(value) || value.Length < 10)
        {
            return false;
        }

        for (var start = 0; start + 10 <= value.Length; start++)
        {
            if (!IsDigit(value[start]) ||
                !IsDigit(value[start + 1]) ||
                !IsDigit(value[start + 2]) ||
                !IsDigit(value[start + 3]) ||
                value[start + 4] != '-' ||
                !IsDigit(value[start + 5]) ||
                !IsDigit(value[start + 6]) ||
                value[start + 7] != '-' ||
                !IsDigit(value[start + 8]) ||
                !IsDigit(value[start + 9]))
            {
                continue;
            }

            index = start;
            year = (Digit(value[start]) * 1000) + (Digit(value[start + 1]) * 100) + (Digit(value[start + 2]) * 10) + Digit(value[start + 3]);
            month = (Digit(value[start + 5]) * 10) + Digit(value[start + 6]);
            day = (Digit(value[start + 8]) * 10) + Digit(value[start + 9]);
            return true;
        }

        return false;
    }

    /// <summary>Validates the triple the same way <c>DateOnly.TryParseExact</c> did (Feb 30 fails).</summary>
    public static bool TryMakeDate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    /// <summary>
    /// Parses "2026-07-30T18:04:11.123Z" (or a +05:30 offset, or no zone at all) into the hour it
    /// falls in, keeping the WRITER's frame: the returned <see cref="DateTimeOffset.DateTime"/> is
    /// the wall clock as written and the offset is the one the text declared, so the calendar day
    /// is unchanged from the old regex path while the true instant stays recoverable.
    /// </summary>
    public static bool TryParseHour(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!TryFindDate(value, out var index, out var year, out var month, out var day) ||
            !TryMakeDate(year, month, day, out var date) ||
            // The plausibility floor lives HERE rather than in the ledger alone, because a row is
            // cheapest to reject before it has been aggregated, cached and merged. See the remarks
            // on EarliestPlausibleDay for why a bad date is a fan-out problem, not a rounding one.
            !IsPlausibleDay(date))
        {
            return false;
        }

        var text = value!;
        var hour = 0;
        var cursor = index + 10;
        if (cursor + 2 < text.Length &&
            (text[cursor] is 'T' or 't' or ' ') &&
            IsDigit(text[cursor + 1]) &&
            IsDigit(text[cursor + 2]))
        {
            hour = (Digit(text[cursor + 1]) * 10) + Digit(text[cursor + 2]);
            if (hour > 23)
            {
                // 24:00 is legal ISO 8601 for midnight of the next day, and anything else here is
                // corrupt. Neither is worth a row; fall back to the start of the day.
                hour = 0;
            }

            cursor += 3;
        }

        var offset = ReadOffset(text, cursor);
        var wallClock = new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Unspecified);
        timestamp = new DateTimeOffset(wallClock, offset ?? SafeLocalOffset(wallClock));
        return true;
    }

    /// <summary>Returns the declared UTC offset, or null when the text carries none.</summary>
    private static TimeSpan? ReadOffset(string text, int fromIndex)
    {
        for (var i = fromIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (c is 'Z' or 'z')
            {
                return TimeSpan.Zero;
            }

            if (c is not ('+' or '-') ||
                i + 2 >= text.Length ||
                !IsDigit(text[i + 1]) ||
                !IsDigit(text[i + 2]))
            {
                continue;
            }

            var hours = (Digit(text[i + 1]) * 10) + Digit(text[i + 2]);
            var minutes = 0;
            var minuteIndex = i + 3 < text.Length && text[i + 3] == ':' ? i + 4 : i + 3;
            if (minuteIndex + 1 < text.Length && IsDigit(text[minuteIndex]) && IsDigit(text[minuteIndex + 1]))
            {
                minutes = (Digit(text[minuteIndex]) * 10) + Digit(text[minuteIndex + 1]);
            }

            if (hours > 14 || minutes > 59)
            {
                return null;
            }

            var offset = new TimeSpan(hours, minutes, 0);
            return c == '-' ? -offset : offset;
        }

        return null;
    }

    /// <summary>
    /// A wall clock inside a DST spring-forward gap has no local offset; the framework throws
    /// rather than picking one. A row is not worth an exception, so fall back to the base offset.
    /// </summary>
    private static TimeSpan SafeLocalOffset(DateTime wallClock)
    {
        try
        {
            return TimeZoneInfo.Local.GetUtcOffset(wallClock);
        }
        catch
        {
            return TimeZoneInfo.Local.BaseUtcOffset;
        }
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static int Digit(char c) => c - '0';
}
