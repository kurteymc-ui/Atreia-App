namespace AionDPS.Combat;

/// <summary>How a stagger check went: still going, the bar broke in time, it didn't (the cast ran out, or the bar filled again), or a bar without a cast
/// stopped going down before it broke (the boss died, or the fight ended).</summary>
public enum StaggerOutcome
{
    Running,
    Broken,
    Failed,
    Ended,
}

/// <summary>A skill a player hit the boss with during a stagger check, and how many times.</summary>
public sealed record StaggerUse(int Player, int SkillId, int Hits);

/// <summary>A hit of a player on the boss (seconds from the fight's start): what they hit it with in a stretch of the fight, whether it took anything off the bar or not.</summary>
public sealed record StaggerTouch(double At, int Player, int SkillId);

/// <summary>
/// One stagger check of a boss: from the moment its stagger bar was full until it broke or filled again. <see cref="Mechanic"/>: the boss's cast that started
/// it (Ultimate Berk's 8 s "Extinction for Eternity", Transcendent Bakarma's 18 s "Wave of Death"; 0 for a bar that is always up), <see cref="Deadline"/>: when
/// that cast ends - the bar has to break before. <see cref="Left"/>: what was still on the bar at the end (0 when it broke). <see cref="Credit"/>: who took
/// what off it (times in seconds from <see cref="Start"/>).
/// </summary>
public sealed record StaggerCheck(int Boss, DateTime Start, DateTime? Deadline, int Mechanic, int Max, DateTime? End, StaggerOutcome Outcome, int Left, StaggerReport Credit,
    IReadOnlyList<StaggerUse>? Uses = null)
{
    /// <summary>The skills each player hit the boss with in the check, those that took nothing off the bar too ("I used mine").</summary>
    public IReadOnlyList<StaggerUse> Used => Uses ?? Array.Empty<StaggerUse>();

    /// <summary>The time the cast gives to break the bar (seconds), null without a cast.</summary>
    public double? Limit => Deadline is DateTime d ? (d - Start).TotalSeconds : null;

    /// <summary>How long the check has run (to its end, else to <paramref name="now"/>).</summary>
    public double Seconds(DateTime now) => Math.Max(0, ((End ?? now) - Start).TotalSeconds);

    /// <summary>What came off the bar.</summary>
    public int Taken => Math.Max(0, Max - Left);

    /// <summary>The seconds left before the cast ends (null without a cast; 0 when it has).</summary>
    public double? TimeLeft(DateTime now) => Deadline is DateTime d ? Math.Max(0, (d - now).TotalSeconds) : null;
}

/// <summary>
/// The boss's stagger checks over a fight (6 November 2026, the owner: "groups where we don't manage to stagger but people say they used theirs ... see it at
/// a glance"). Read off recordings of 4 November 2026: the game shows a boss's stagger bar full when a check starts - Ultimate Berk's at the start of its 8 s
/// cast (480, then 720), Transcendent Bakarma's at its 18 s cast (557) - and the bar either breaks before the cast ends or fills up again when it ends (a
/// failed check: 53 of 480 still on it). Bosses whose bar is always up (Enhanced Harcon, 1,800) break it now and then and refill it some seconds later.
/// So a check runs from a full bar to a break (broken), to the bar filling again or its size changing (failed), or past the end of its cast (failed). Its
/// mechanic is the boss's cast of 3 s or more from 3 s before the bar filled to the first drop off it (Bakarma's bar fills after a break and waits for the
/// next cast); the check starts with that cast. Each check's drops are credited to the hits that caused them (<see cref="StaggerCredit"/>). A bar that sat
/// full with nothing taken off and no cast is no check. Pure but for the casts it learns to be mechanics (<see cref="IsMechanic"/>).
/// </summary>
public static class StaggerChecks
{
    /// <summary>A cast this long or longer can be a check's mechanic.</summary>
    public const int MechanicMs = 3000;

    /// <summary>A reading at this share of the bar or more starts a check (the first reading of all may start lower: the meter joined late).</summary>
    public const double FullShare = 0.9;

    /// <summary>The bar rose by this share of it at once: it filled again.</summary>
    public const double RefillShare = 0.3;

    /// <summary>Less than this share of the bar taken off: nobody broke it (a break then is the game's own).</summary>
    public const double ScriptedShare = 0.01;

    /// <summary>A check without a cast whose last reading is this old has ended (the fight moved on, or ended, without a break).</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(20);

    /// <param name="readings">The boss's stagger readings and breaks, in the order the game sent them.</param>
    /// <param name="windups">The boss's casts that took time.</param>
    /// <param name="hits">Hits on the boss, in the order the game sent them (only those of the party count).</param>
    /// <param name="now">The time now (a live fight: a check past the end of its cast has failed; a quiet one has ended).</param>
    public static List<StaggerCheck> Find(int boss, IReadOnlyList<StaggerEvent> readings, IReadOnlyList<WindupRecord> windups, IReadOnlyList<DamageEvent> hits, DateTime now)
    {
        var runs = new List<Run>();
        Run? run = null;
        int? previous = null;
        foreach (StaggerEvent r in readings)
        {
            if (r.Entity != boss)
            {
                continue;
            }

            if (r.Broke)
            {
                if (run is not null)
                {
                    run.Readings.Add(r);
                    run.End = r.At;
                    run.Outcome = StaggerOutcome.Broken;
                    run.Left = 0;
                    runs.Add(run);
                    run = null;
                }

                previous = null;
                continue;
            }

            if (r.Max <= 0)
            {
                continue;
            }

            // the bar filled again, or changed its size: the check before failed
            if (run is not null && (r.Max != run.Max || (previous is int p && r.Current - p >= RefillShare * r.Max)))
            {
                run.End = r.At;
                run.Outcome = StaggerOutcome.Failed;
                run.Left = previous ?? run.Max;
                runs.Add(run);
                run = null;
            }

            if (run is null)
            {
                // a check starts at a full bar (a late reading after a break, low on the bar, is not one); the first reading of all may be lower
                if (r.Current >= FullShare * r.Max || (runs.Count == 0 && previous is null))
                {
                    run = new Run(r.At, r.Max) { Left = r.Current };
                    run.Readings.Add(r);
                }

                previous = r.Current;
                continue;
            }

            run.Readings.Add(r);
            run.Left = r.Current;
            previous = r.Current;
        }

        if (run is not null)
        {
            runs.Add(run);
        }

        var checks = new List<StaggerCheck>();
        foreach (Run x in runs)
        {
            // the mechanic: the boss's long cast from 3 s before the bar filled to the first drop off it (Bakarma's bar fills after a break and waits, full,
            // for its next 18 s cast: the first hits on it come 6 s into that cast); while nothing has come off yet, a cast known to be one since
            DateTime? firstDrop = x.Readings.FirstOrDefault(r => !r.Broke && r.Current < x.Max).At is DateTime fd && fd != default ? fd : null;
            WindupRecord? cast = windups
                .Where(w => w.Actor == boss && w.DurationMs >= MechanicMs && w.At >= x.Start.AddSeconds(-3)
                    && (firstDrop is DateTime f ? w.At <= f.AddSeconds(0.5) : w.At <= x.Start.AddSeconds(1.5) || (x.Outcome == StaggerOutcome.Running && IsMechanic(w.SkillId) && w.At <= now)))
                .OrderByDescending(w => w.At).Cast<WindupRecord?>().FirstOrDefault();
            // Next to nothing came off the bar (under 1 % of it): nobody broke it. Eternal Ludra's 5,625 bar sits full the whole fight and the game breaks it
            // some 20 to 30 s after Purple Bolt (about 55 % health) and after Ludra's Trial (about 21 %) when the group lives through them (the owner's twelve
            // pulls of 7 November 2026: never a point taken off by hits, at most 8 right after it filled again) - shown, that was a "broken" check of three
            // minutes nobody had a part in.
            int lowest = x.Readings.Where(r => !r.Broke).Select(r => r.Current).DefaultIfEmpty(x.Max).Min();
            bool barely = x.Max - lowest < Math.Max(1, ScriptedShare * x.Max);
            if (cast is { } learnt && firstDrop is not null && !barely)
            {
                Learn(learnt.SkillId);
            }

            // a check that ended well after the cast had: not that cast's (the game fills the bar again when the cast ends)
            if (cast is { } late && x.End is DateTime ended && ended > late.At.AddMilliseconds(late.DurationMs).AddSeconds(1))
            {
                cast = null;
            }

            DateTime start = cast is { } c && c.At > x.Start ? c.At : x.Start;
            DateTime? deadline = cast is { } cc ? cc.At.AddMilliseconds(cc.DurationMs) : null;
            // the bar sat full: no check - with a cast too, unless it is one known to start a check (Isolation Specialist Rahu's 15 s cast in Ludra comes
            // after its bar filled again, and hundreds of hits during it take nothing off: the boss can't be staggered then, there is nothing to break)
            if (barely && (x.Outcome == StaggerOutcome.Broken || cast is not { } shown || !IsMechanic(shown.SkillId)))
            {
                continue; // a break of the game's own (Ludra), or a bar that sat full: no check
            }

            if (x.Outcome == StaggerOutcome.Running)
            {
                DateTime last = x.Readings[^1].At;
                if (deadline is DateTime d && now > d.AddSeconds(0.7))
                {
                    x.Outcome = StaggerOutcome.Failed; // the cast ran out and the bar is still up
                    x.End = d;
                }
                else if (deadline is null && now - last > Quiet)
                {
                    x.Outcome = StaggerOutcome.Ended; // nothing more came off it (the boss died, the fight ended): not broken, but no check was missed
                    x.End = last;
                }
            }

            DateTime to = x.End ?? now;
            var mine = hits.Where(h => h.TargetObjectId == boss && !h.IsHeal && h.Timestamp >= start - StaggerCredit.Window && h.Timestamp <= to.AddSeconds(0.2)).ToList();
            var ofCheck = x.Readings.Where(r => r.At >= start || r.At == x.Start).ToList();
            StaggerReport credit = StaggerCredit.Build(ofCheck, mine, start);
            var used = mine.Where(h => h.Timestamp >= start && h.SourceObjectId != 0).GroupBy(h => (h.SourceObjectId, h.SkillId))
                .Select(g => new StaggerUse(g.Key.SourceObjectId, g.Key.SkillId, g.Count())).ToList();
            checks.Add(new StaggerCheck(boss, start, deadline, cast?.SkillId ?? 0, x.Max, x.End, x.Outcome, x.Outcome == StaggerOutcome.Broken ? 0 : x.Left, credit, used));
        }

        return checks;
    }

    /// <summary>The boss casts that started a stagger check in the recordings (Ultimate Berk's "Extinction for Eternity", Transcendent Bakarma's "Wave of
    /// Death"), and those the meter has seen start one since it started: one of them starting shows the check at once, before anybody has hit the bar.</summary>
    private static readonly HashSet<int> Mechanics = new() { 1800141, 1800655 };

    private static readonly object MechanicsGate = new();

    public static bool IsMechanic(int skillId)
    {
        lock (MechanicsGate)
        {
            return Mechanics.Contains(skillId);
        }
    }

    private static void Learn(int skillId)
    {
        lock (MechanicsGate)
        {
            Mechanics.Add(skillId);
        }
    }

    /// <summary>The check to show now: the one running, else the last one that ended (null without any).</summary>
    public static StaggerCheck? Latest(IReadOnlyList<StaggerCheck> checks) =>
        checks.LastOrDefault(c => c.Outcome == StaggerOutcome.Running) ?? checks.LastOrDefault();

    private sealed class Run(DateTime start, int max)
    {
        public DateTime Start { get; } = start;
        public int Max { get; } = max;
        public List<StaggerEvent> Readings { get; } = new();
        public DateTime? End { get; set; }
        public StaggerOutcome Outcome { get; set; } = StaggerOutcome.Running;
        public int Left { get; set; }
    }
}
