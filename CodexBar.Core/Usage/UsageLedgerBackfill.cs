using System.Globalization;

namespace CodexBarWindows;

/// <summary>
/// One session-log file the backfill intends to read.
/// </summary>
/// <param name="Stamp">
/// The day the file is ABOUT, from its name where the corpus dates it (Codex rollouts do) and
/// from its last write time otherwise. Used only to sort oldest-first and to name the month in
/// the progress label — never to include or exclude a row, which is what the timestamp inside the
/// line is for.
/// </param>
/// <param name="IsSecondary">Selects the source's second parser (pi transcripts for Codex).</param>
internal readonly record struct UsageLedgerBackfillFile(string Path, DateOnly Stamp, bool IsSecondary);

/// <summary>
/// One corpus the backfill can walk, implemented by the reader that owns its file format.
/// </summary>
/// <remarks>
/// The parsing has to stay inside the readers: the ledger's numbers are only trustworthy while
/// they are produced by the SAME code the flyout's 30-day figures come from. A second parser here
/// would drift from the first the moment either provider changed a field, and the drift would show
/// up as a step in the history exactly where the backfilled months meet the scanned ones.
/// </remarks>
internal interface IUsageLedgerBackfillSource
{
    UsageLedgerScope Scope { get; }

    /// <summary>Human name of the corpus, for the progress label.</summary>
    string DisplayName { get; }

    /// <summary>The scanner semantics version the owning reader stamps on its own batches.</summary>
    int AccountingVersion { get; }

    /// <summary>Every file on disk, oldest first. Enumeration only — nothing is opened here.</summary>
    IReadOnlyList<UsageLedgerBackfillFile> EnumerateFiles();

    /// <summary>
    /// Parses one file and adds its rows to <paramref name="builder"/>. Called concurrently with
    /// a builder private to the calling worker, so an implementation must only synchronise state
    /// it shares between files (Claude's cross-file dedup set).
    /// </summary>
    void Scan(UsageLedgerBackfillFile file, UsageLedgerBatchBuilder builder);
}

public enum UsageLedgerBackfillOutcome
{
    Imported,
    NothingFound,
    Cancelled,
    Failed
}

/// <param name="FilesDone">Files parsed so far, across every corpus.</param>
/// <param name="Label">Human sentence naming what is being read right now.</param>
public sealed record UsageLedgerBackfillProgress(int FilesDone, int FileCount, string Label)
{
    public double Fraction => FileCount <= 0 ? 0 : Math.Clamp((double)FilesDone / FileCount, 0, 1);
}

public sealed record UsageLedgerBackfillResult(
    UsageLedgerBackfillOutcome Outcome,
    int FilesScanned,
    int DaysImported,
    DateOnly? FirstDay,
    DateOnly? LastDay,
    string Message);

/// <summary>
/// Rebuilds the ledger from EVERY session log on disk, not just the 30-day scan window.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: the scan the graphs window triggers is windowed twice over — enumeration keeps
/// only files touched in the last 32 days and aggregation keeps only rows inside the reported 30 —
/// so the months already sitting in ~/.codex/sessions and ~/.claude/projects can never reach the
/// ledger through it. This walks the whole corpus once, on demand.
/// </para>
/// <para>
/// NEVER called at startup, from a timer, or as a side effect of opening a window. It is minutes of
/// disk churn and full-rate JSON parsing; wiring it to anything automatic would destroy the app's
/// zero-idle-cost property outright. The only caller is a button.
/// </para>
/// <para>
/// MEMORY. The corpus is gigabytes and the process must not grow with it, so nothing is ever
/// materialised whole: files are streamed line by line by the readers, each file's parsed rows are
/// folded into an aggregate and dropped before the next file is opened, and the aggregate itself is
/// keyed (hour, model, flags) — bounded by the CALENDAR, not by the log size. A year of hourly
/// buckets over a handful of models is tens of thousands of records, a few MB. The one structure
/// that does scale with row count is Claude's cross-file dedup set, which is why it stores 128-bit
/// fingerprints rather than the id strings themselves (see the Claude source).
/// </para>
/// <para>
/// CONSISTENCY UNDER CANCELLATION. Nothing is written until a corpus has been read in full: the
/// merge happens once, after the last file of that corpus. Cancelling therefore cannot leave a
/// half-written day — the affected corpus is simply not merged at all. This also has to be per
/// corpus rather than per file, because the merge REPLACES the days a complete batch covers: merging
/// file by file would make each file delete the previous file's rows for any day they share.
/// </para>
/// </remarks>
public static class UsageLedgerBackfill
{
    /// <summary>
    /// Ceiling on files read in one run. Reached only by a corpus far outside anything observed
    /// (this user: ~1,800 files); hitting it marks the batch incomplete so replace-by-scope cannot
    /// delete days the truncated enumeration never reached.
    /// </summary>
    private const int MaxFilesPerSource = 40_000;

    /// <summary>
    /// Files between progress reports. Every file would post ~1,800 callbacks onto the UI thread
    /// for a bar that moves less than a pixel per file.
    /// </summary>
    private const int ProgressStride = 16;

    /// <summary>Bounded so an import leaves cores free — it runs while the user keeps working.</summary>
    private static int ScanParallelism => Math.Clamp(Environment.ProcessorCount - 2, 1, 8);

    public static Task<UsageLedgerBackfillResult> RunAsync(
        IProgress<UsageLedgerBackfillProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Task.Run, not an async state machine: every step below is synchronous file I/O and JSON
        // parsing. The caller is a UI thread and must not execute one line of this.
        return Task.Run(() => Run(progress, cancellationToken), CancellationToken.None);
    }

    internal static UsageLedgerBackfillResult Run(
        IProgress<UsageLedgerBackfillProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<IUsageLedgerBackfillSource>? sources = null)
    {
        sources ??=
        [
            CodexUsageInsightsReader.CreateBackfillSource(),
            ClaudeUsageInsightsReader.CreateBackfillSource()
        ];

        // Hoisted out of the try on purpose: cancellation unwinds through it, and the result then
        // has to be able to say what was already COMMITTED. See the catch below.
        var done = 0;
        var days = new HashSet<int>();
        var merged = 0;
        var failed = 0;

        try
        {
            var plans = new List<(IUsageLedgerBackfillSource Source, IReadOnlyList<UsageLedgerBackfillFile> Files)>();
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                plans.Add((source, source.EnumerateFiles()));
            }

            var total = plans.Sum(plan => plan.Files.Count);
            if (total == 0)
            {
                return new UsageLedgerBackfillResult(
                    UsageLedgerBackfillOutcome.NothingFound,
                    0,
                    0,
                    null,
                    null,
                    "No Codex or Claude session logs were found on this PC.");
            }

            progress?.Report(new UsageLedgerBackfillProgress(0, total, $"Reading {total:N0} session logs..."));

            var scannedAt = DateTimeOffset.Now;

            foreach (var (source, files) in plans)
            {
                if (files.Count == 0)
                {
                    continue;
                }

                var builder = ScanSource(source, files, progress, total, ref done, cancellationToken);

                // The ONLY write, and only once the whole corpus has been read. See the remarks:
                // this is what makes cancellation safe and what stops a per-file merge from
                // replacing each day with the last file that happened to touch it.
                cancellationToken.ThrowIfCancellationRequested();
                if (builder.EarliestUtcHour is { } earliest)
                {
                    // A complete pass over the whole corpus genuinely covers every day from the
                    // first row on disk to now, including the days it found empty — that is what
                    // lets replace-by-scope correct a day the 30-day scan got wrong.
                    //
                    // `earliest` is data, so it is not trusted to bound anything: CoverDays clamps
                    // the range to the ledger's plausible span. A single corrupt timestamp must not
                    // be able to turn one merge into thousands of shard rebuilds.
                    builder.CoverDays(UsageLedger.FromUtcHour(earliest), scannedAt);
                }

                var batch = builder.Build(scannedAt);

                // Days are counted only once the write SUCCEEDED. "Imported 40 days" for a batch
                // that never reached disk is the same class of lie as "nothing was changed" for one
                // that did.
                if (UsageLedger.TryMerge(source.Scope, batch))
                {
                    merged++;
                    foreach (var record in batch.Records)
                    {
                        days.Add(UsageLedger.UtcDayOfHour(record.Key.UtcHour));
                    }
                }
                else
                {
                    failed++;
                }
            }

            progress?.Report(new UsageLedgerBackfillProgress(done, total, "Finishing..."));

            var first = days.Count == 0 ? (DateOnly?)null : UsageLedger.FromUtcDay(days.Min());
            var last = days.Count == 0 ? (DateOnly?)null : UsageLedger.FromUtcDay(days.Max());

            if (merged == 0 && failed > 0)
            {
                return new UsageLedgerBackfillResult(
                    UsageLedgerBackfillOutcome.Failed,
                    done,
                    0,
                    null,
                    null,
                    "Read the session logs, but the history file could not be written.");
            }

            if (days.Count == 0)
            {
                return new UsageLedgerBackfillResult(
                    UsageLedgerBackfillOutcome.NothingFound,
                    done,
                    0,
                    null,
                    null,
                    $"Read {done:N0} session logs and found no token usage in them.");
            }

            var partial = failed > 0 ? " One provider's history could not be written." : string.Empty;
            return new UsageLedgerBackfillResult(
                failed > 0 ? UsageLedgerBackfillOutcome.Failed : UsageLedgerBackfillOutcome.Imported,
                done,
                days.Count,
                first,
                last,
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"Imported {days.Count:N0} days of history ({first:d} to {last:d}) from {done:N0} session logs.{partial}"));
        }
        catch (OperationCanceledException)
        {
            // HONEST, not reassuring. The merge is per corpus (see the remarks), so a cancel that
            // arrives while the second corpus is being read finds the first one already fully
            // replaced on disk — and telling the user their data is untouched when it is not is
            // worse than telling them the import stopped halfway.
            //
            // Not rolled back, deliberately: what committed is a COMPLETE, correct view of that
            // corpus, so undoing it would delete good history to restore an older answer. The
            // guarantee this path owns is consistency, and that still holds — no corpus is ever
            // half-written, because nothing is written until its whole walk is done.
            var cancelledFirst = days.Count == 0 ? (DateOnly?)null : UsageLedger.FromUtcDay(days.Min());
            var cancelledLast = days.Count == 0 ? (DateOnly?)null : UsageLedger.FromUtcDay(days.Max());

            return new UsageLedgerBackfillResult(
                UsageLedgerBackfillOutcome.Cancelled,
                done,
                days.Count,
                cancelledFirst,
                cancelledLast,
                merged == 0
                    ? "Import cancelled. Nothing was changed."
                    : string.Create(
                        CultureInfo.CurrentCulture,
                        $"Import cancelled. {days.Count:N0} days of history ({cancelledFirst:d} to {cancelledLast:d}) had already been imported from {done:N0} session logs and were kept; the rest was not read."));
        }
        catch (Exception exception)
        {
            // Same contract as every other ledger path: a failure degrades to a sentence, never an
            // exception escaping into a UI callback.
            return new UsageLedgerBackfillResult(
                UsageLedgerBackfillOutcome.Failed,
                0,
                0,
                null,
                null,
                $"Could not import history: {exception.Message}");
        }
    }

    private static UsageLedgerBatchBuilder ScanSource(
        IUsageLedgerBackfillSource source,
        IReadOnlyList<UsageLedgerBackfillFile> files,
        IProgress<UsageLedgerBackfillProgress>? progress,
        int total,
        ref int done,
        CancellationToken cancellationToken)
    {
        var aggregate = new UsageLedgerBatchBuilder(source.AccountingVersion);
        if (files.Count >= MaxFilesPerSource)
        {
            aggregate.MarkIncomplete();
        }

        var completed = done;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = ScanParallelism,
            CancellationToken = cancellationToken
        };

        try
        {
            Parallel.ForEach(
                files,
                options,
                // Per-worker builder: the aggregate is a plain Dictionary and taking a lock per ROW
                // (millions of them) would serialise the parse it is meant to overlap with.
                () => new UsageLedgerBatchBuilder(source.AccountingVersion),
                (file, _, local) =>
                {
                    try
                    {
                        source.Scan(file, local);
                    }
                    catch
                    {
                        // One unreadable file must not abandon the import — but it does mean this
                        // batch is a lower bound, so it merges per-key MAX instead of replacing.
                        local.MarkIncomplete();
                    }

                    var at = Interlocked.Increment(ref completed);
                    if (at % ProgressStride == 0 || at == total)
                    {
                        // The label follows the file that just finished. Files are sorted oldest-first
                        // so it walks forward through the months; with several workers in flight it can
                        // repeat a month, which is honest and costs nothing to allow.
                        progress?.Report(new UsageLedgerBackfillProgress(
                            at,
                            total,
                            string.Create(CultureInfo.CurrentCulture, $"Importing {source.DisplayName} sessions from {file.Stamp:MMMM yyyy}...")));
                    }

                    return local;
                },
                local =>
                {
                    lock (aggregate)
                    {
                        aggregate.AddFrom(local);
                    }
                });
        }
        finally
        {
            // In the finally, because cancellation unwinds THROUGH Parallel.ForEach: the files this
            // pass did read are the ones the cancelled result reports, and losing the count here is
            // what made "zero files" part of the old cancellation lie.
            done = Volatile.Read(ref completed);
        }

        return aggregate;
    }
}
