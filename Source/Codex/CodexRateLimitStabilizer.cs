namespace CodexBarWindows;

internal sealed class CodexRateLimitStabilizer
{
    private const int RequiredInitialAgreement = 2;
    private const int RequiredConflictConfirmations = 3;
    private const double PercentRegressionTolerance = 0.5;
    private const double InitialPrimaryPercentTolerance = 10;
    private const double InitialSecondaryPercentTolerance = 5;
    private static readonly TimeSpan ResetTimeTolerance = TimeSpan.FromSeconds(90);
    private readonly object sync = new();
    private CodexRateLimitSnapshot? accepted;
    private CodexRateLimitSnapshot? pendingConflict;
    private int pendingConflictCount;

    public bool NeedsInitialConsensus
    {
        get
        {
            lock (sync)
            {
                return accepted is null;
            }
        }
    }

    public UsageLookupResult Stabilize(IReadOnlyList<UsageLookupResult> samples, DateTimeOffset now)
    {
        lock (sync)
        {
            var snapshots = samples
                .Where(sample => sample.Snapshot is not null)
                .Select(sample => sample.Snapshot!)
                .ToList();

            if (accepted is null)
            {
                return EstablishInitialConsensus(snapshots, samples);
            }

            if (snapshots.Count == 0)
            {
                var error = samples.Select(sample => sample.Error).FirstOrDefault(message => !string.IsNullOrWhiteSpace(message))
                    ?? "Live Codex usage refresh failed.";
                return new UsageLookupResult(
                    accepted,
                    $"Showing last confirmed Codex limits. {error}");
            }

            var candidate = snapshots[^1];
            if (IsExpectedProgression(accepted, candidate, now))
            {
                Accept(candidate);
                return new UsageLookupResult(candidate, null);
            }

            if (pendingConflict is not null && EquivalentWindows(pendingConflict, candidate))
            {
                pendingConflict = candidate;
                pendingConflictCount++;
            }
            else
            {
                pendingConflict = candidate;
                pendingConflictCount = 1;
            }

            if (pendingConflictCount >= RequiredConflictConfirmations)
            {
                if (candidate.Primary.ResetsAt is { } candidateReset && candidateReset <= now)
                {
                    return new UsageLookupResult(
                        accepted,
                        "Codex repeatedly returned an expired usage window; showing the last confirmed limits.");
                }

                Accept(candidate);
                return new UsageLookupResult(candidate, null);
            }

            return new UsageLookupResult(
                accepted,
                "Codex returned conflicting usage windows; showing the last confirmed limits.");
        }
    }

    private UsageLookupResult EstablishInitialConsensus(
        IReadOnlyList<CodexRateLimitSnapshot> snapshots,
        IReadOnlyList<UsageLookupResult> samples)
    {
        var groups = new List<List<CodexRateLimitSnapshot>>();
        foreach (var snapshot in snapshots)
        {
            var group = groups.FirstOrDefault(existing => EquivalentInitialSamples(existing[0], snapshot));
            if (group is null)
            {
                groups.Add([snapshot]);
            }
            else
            {
                group.Add(snapshot);
            }
        }

        var consensus = groups
            .OrderByDescending(group => group.Count)
            .FirstOrDefault();
        if (consensus is null || consensus.Count < RequiredInitialAgreement)
        {
            var liveError = samples.Select(sample => sample.Error).FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
            return new UsageLookupResult(
                null,
                liveError ?? "Codex returned conflicting rate-limit windows; no two live samples agreed.");
        }

        var selected = consensus.OrderByDescending(snapshot => snapshot.ObservedAt).First();
        Accept(selected);
        return new UsageLookupResult(selected, null);
    }

    private static bool IsExpectedProgression(
        CodexRateLimitSnapshot previous,
        CodexRateLimitSnapshot candidate,
        DateTimeOffset now)
    {
        if (previous.Primary.ResetsAt is { } previousReset &&
            candidate.Primary.ResetsAt is { } candidateReset &&
            previousReset <= now &&
            candidateReset > now)
        {
            return true;
        }

        if (!EquivalentWindows(previous, candidate))
        {
            return false;
        }

        return previous.Windows
            .Zip(candidate.Windows)
            .All(pair => pair.Second.UsedPercent + PercentRegressionTolerance >= pair.First.UsedPercent);
    }

    internal static bool EquivalentWindows(CodexRateLimitSnapshot left, CodexRateLimitSnapshot right)
    {
        return left.Windows.Count == right.Windows.Count &&
               left.Windows.Zip(right.Windows).All(pair => EquivalentWindow(pair.First, pair.Second));
    }

    private static bool EquivalentInitialSamples(CodexRateLimitSnapshot left, CodexRateLimitSnapshot right)
    {
        if (!EquivalentWindows(left, right))
        {
            return false;
        }

        return left.Windows
            .Zip(right.Windows)
            .Select((pair, index) => new
            {
                Difference = Math.Abs(pair.First.UsedPercent - pair.Second.UsedPercent),
                Tolerance = index == 0 ? InitialPrimaryPercentTolerance : InitialSecondaryPercentTolerance
            })
            .All(comparison => comparison.Difference <= comparison.Tolerance);
    }

    private static bool EquivalentWindow(UsageWindow left, UsageWindow right)
    {
        if (left.WindowMinutes != right.WindowMinutes)
        {
            return false;
        }

        return (left.ResetsAt, right.ResetsAt) switch
        {
            (null, null) => true,
            ({ } leftReset, { } rightReset) => (leftReset - rightReset).Duration() <= ResetTimeTolerance,
            _ => false
        };
    }

    private void Accept(CodexRateLimitSnapshot snapshot)
    {
        accepted = snapshot;
        pendingConflict = null;
        pendingConflictCount = 0;
    }
}
