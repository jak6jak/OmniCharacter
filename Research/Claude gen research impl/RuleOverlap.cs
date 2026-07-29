using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;

namespace CharacterKit.Animation;

// =====================================================================
// Overlap analysis.
//
// The one structural complaint about first-match-wins over an ordered list
// is failure locality: inserting a rule changes behavior for states you
// weren't thinking about, and nothing tells you which ones. An HFSM scopes
// transitions to a source state, so a mistake stays local.
//
// The answer is not to give up the flat table. It is to notice that once
// `Condition` became plain data instead of a lambda, rule overlap became
// exactly decidable:
//
//   - Tags are a finite Boolean lattice. Two TagQuerys can both match iff
//     neither's Require set is hit by the other's Exclude set.
//   - Conditions are interval constraints over a closed Field enum. Collapse
//     each rule to one interval per field; the rules can both match iff every
//     field's interval pair overlaps.
//
// So for any rule pair we can decide not just WHETHER one shadows the other
// but over WHICH region. That is precisely the information that goes missing
// at insertion time, and `AnimPack.Shadows` is what makes a pack declare it.
//
// The `When` lambda escape hatch is undecidable, so a rule carrying one is
// treated as overlapping everything in its slot. That conservatism is why
// AnimDirector reports lambda rules at WARN rather than INFO.
// =====================================================================

/// One field's admissible range. Open vs closed matters: Above(v) and Below(v)
/// do not overlap at v, and reporting that they do would make the shadow check
/// cry wolf on every threshold pair in the table.
public readonly struct Interval
{
    public readonly float Lo, Hi;
    public readonly bool  LoClosed, HiClosed;

    public Interval(float lo, bool loClosed, float hi, bool hiClosed)
    {
        Lo = lo; LoClosed = loClosed; Hi = hi; HiClosed = hiClosed;
    }

    public static readonly Interval Unbounded =
        new(float.NegativeInfinity, false, float.PositiveInfinity, false);

    public bool IsUnbounded => float.IsNegativeInfinity(Lo) && float.IsPositiveInfinity(Hi);

    public bool IsEmpty => Lo > Hi || (Lo == Hi && !(LoClosed && HiClosed));

    /// Largest interval contained in both.
    public Interval Meet(Interval o)
    {
        float lo; bool loC;
        if (Lo > o.Lo)      { lo = Lo;   loC = LoClosed; }
        else if (Lo < o.Lo) { lo = o.Lo; loC = o.LoClosed; }
        else                { lo = Lo;   loC = LoClosed && o.LoClosed; }

        float hi; bool hiC;
        if (Hi < o.Hi)      { hi = Hi;   hiC = HiClosed; }
        else if (Hi > o.Hi) { hi = o.Hi; hiC = o.HiClosed; }
        else                { hi = Hi;   hiC = HiClosed && o.HiClosed; }

        return new Interval(lo, loC, hi, hiC);
    }

    public bool Intersects(Interval o) => !Meet(o).IsEmpty;

    /// True when every value this admits, `outer` admits too.
    public bool ContainedBy(Interval outer)
    {
        if (IsEmpty) return true;
        bool loOk = outer.Lo < Lo || (outer.Lo == Lo && (outer.LoClosed || !LoClosed));
        bool hiOk = outer.Hi > Hi || (outer.Hi == Hi && (outer.HiClosed || !HiClosed));
        return loOk && hiOk;
    }

    public override string ToString()
    {
        if (IsUnbounded) return "any";
        var sb = new StringBuilder();
        sb.Append(float.IsNegativeInfinity(Lo) ? "(-inf" : (LoClosed ? "[" : "(") + Lo.ToString("0.###"));
        sb.Append(", ");
        sb.Append(float.IsPositiveInfinity(Hi) ? "inf)" : Hi.ToString("0.###") + (HiClosed ? "]" : ")"));
        return sb.ToString();
    }
}

/// The exact set of CharStates a rule can match, in decidable form.
public sealed class Region
{
    public readonly HashSet<StringName> Require = new();
    public readonly HashSet<StringName> Exclude = new();
    public readonly Dictionary<string, Interval> Fields = new();

    /// Carries a lambda. Undecidable, so assume it reaches everywhere.
    public bool Opaque;

    public static string KeyOf(Field f, StringName scalarKey) =>
        f == Field.Scalar ? $"scalar:{scalarKey}" : f.ToString();

    /// Hysteresis widens a rule's range while it is already winning, so the
    /// widened form is the set of states the rule can occupy on SOME frame.
    /// Shadowing that only happens while sticky is still shadowing.
    public static Region Of(AnimRule r)
    {
        var reg = new Region { Opaque = r.When is not null };

        foreach (var t in r.Require.Require) reg.Require.Add(t);
        foreach (var t in r.Require.Exclude) reg.Exclude.Add(t);

        foreach (var c in r.Conditions)
        {
            string key = KeyOf(c.Field, c.ScalarKey);

            float v = c.Op is Cmp.Greater or Cmp.GreaterOrEqual
                ? c.Value - c.Hysteresis
                : c.Value + c.Hysteresis;

            Interval i = c.Op switch
            {
                Cmp.Less           => new Interval(float.NegativeInfinity, false, v, false),
                Cmp.LessOrEqual    => new Interval(float.NegativeInfinity, false, v, true),
                Cmp.Greater        => new Interval(v, false, float.PositiveInfinity, false),
                Cmp.GreaterOrEqual => new Interval(v, true, float.PositiveInfinity, false),
                _                  => Interval.Unbounded
            };

            reg.Fields[key] = reg.Fields.TryGetValue(key, out var prev) ? prev.Meet(i) : i;
        }

        return reg;
    }

    /// A rule that can never fire. Usually a copy-paste of contradictory
    /// conditions, or a tag both required and excluded.
    public bool IsEmpty =>
        Require.Overlaps(Exclude) || Fields.Values.Any(i => i.IsEmpty);

    public bool Intersects(Region o)
    {
        if (IsEmpty || o.IsEmpty) return false;
        if (Opaque || o.Opaque) return true;

        if (Require.Overlaps(o.Exclude)) return false;
        if (o.Require.Overlaps(Exclude)) return false;

        foreach (var (k, mine) in Fields)
            if (o.Fields.TryGetValue(k, out var theirs) && !mine.Intersects(theirs))
                return false;

        return true;
    }

    /// True when every state `inner` matches, this matches too — i.e. `inner`
    /// sitting below this in the table is dead, not merely shadowed in part.
    public bool Contains(Region inner)
    {
        if (inner.IsEmpty) return true;
        if (Opaque || inner.Opaque) return false;   // cannot prove it either way

        if (!Require.IsSubsetOf(inner.Require)) return false;
        if (!Exclude.IsSubsetOf(inner.Exclude)) return false;

        foreach (var (k, mine) in Fields)
        {
            var theirs = inner.Fields.TryGetValue(k, out var t) ? t : Interval.Unbounded;
            if (!theirs.ContainedBy(mine)) return false;
        }
        return true;
    }

    /// The overlap itself, for reporting. "which states did I just capture."
    public Region Meet(Region o)
    {
        var m = new Region { Opaque = Opaque || o.Opaque };
        m.Require.UnionWith(Require);
        m.Require.UnionWith(o.Require);
        m.Exclude.UnionWith(Exclude);
        m.Exclude.UnionWith(o.Exclude);

        foreach (var (k, i) in Fields) m.Fields[k] = i;
        foreach (var (k, i) in o.Fields)
            m.Fields[k] = m.Fields.TryGetValue(k, out var prev) ? prev.Meet(i) : i;

        return m;
    }

    public override string ToString()
    {
        var parts = new List<string>();

        if (Require.Count > 0) parts.Add("+" + string.Join(" +", Require.OrderBy(t => t.ToString())));
        if (Exclude.Count > 0) parts.Add("-" + string.Join(" -", Exclude.OrderBy(t => t.ToString())));

        foreach (var (k, i) in Fields.OrderBy(kv => kv.Key))
            if (!i.IsUnbounded) parts.Add($"{k} in {i}");

        if (Opaque) parts.Add("lambda(unknown)");

        return parts.Count == 0 ? "all states" : string.Join(", ", parts);
    }
}

/// `Winner` sits above `Loser` in the table and their regions intersect.
/// `Total` means Loser can never win at all — it is dead, not just narrowed.
public readonly record struct Shadow(AnimRule Winner, AnimRule Loser, Region Overlap, bool Total)
{
    public override string ToString() =>
        $"'{Winner.Clip}'({Winner.Origin}) {(Total ? "buries" : "shadows")} " +
        $"'{Loser.Clip}'({Loser.Origin}) over [{Overlap}]";
}

public static class RuleOverlap
{
    /// Every pair in the table where the higher-precedence rule steals states
    /// from the lower one. O(n^2) per slot; the table is small and this only
    /// runs at install and in tests.
    public static List<Shadow> Find(RuleTable table)
    {
        var regions = new Dictionary<AnimRule, Region>();
        foreach (var r in table.All) regions[r] = Region.Of(r);

        var found = new List<Shadow>();

        foreach (var group in table.All.GroupBy(r => r.Slot))
        {
            // table.All is already in precedence order, and GroupBy preserves it.
            var ordered = group.ToList();

            for (int i = 0; i < ordered.Count; i++)
            for (int j = i + 1; j < ordered.Count; j++)
            {
                var w = regions[ordered[i]];
                var l = regions[ordered[j]];
                if (!w.Intersects(l)) continue;

                found.Add(new Shadow(ordered[i], ordered[j], w.Meet(l), w.Contains(l)));
            }
        }

        return found;
    }

    /// Rules that can never fire under any state, independent of ordering.
    public static List<Issue> FindUnsatisfiable(RuleTable table) =>
        table.All
            .Where(r => Region.Of(r).IsEmpty)
            .Select(r => new Issue("ERROR",
                $"{r.Origin}: rule for '{r.Clip}' is unsatisfiable — " +
                $"its own tags and conditions contradict each other"))
            .ToList();

    // -----------------------------------------------------------------
    // Self-scoped shadows.
    //
    // Without this the declaration burden collapses the idea. The grapple
    // pack's throw rule outranks every weapon pack's upper-body rules, so
    // grapple would have to declare Shadows = ["rifle", "pistol", "bow", ...]
    // — every weapon that will ever exist, listed by a pack that has no
    // business knowing about any of them.
    //
    // But a grapple rule only fires when `action.grapple_throw` is set, and
    // that tag is in grapple's own `Serves`. A shadow gated behind a tag the
    // winning pack itself claims is scoped, by construction, to states that
    // pack was asked to own. That is the same locality an HFSM gets from a
    // source state — expressed with the declaration the design already has.
    // -----------------------------------------------------------------
    public static bool IsSelfScoped(AnimRule winner, AnimPack pack)
    {
        if (pack.Serves is null || pack.Serves.Length == 0) return false;

        var claimed = pack.Serves
            .Where(c => c.Slot == winner.Slot)
            .Select(c => c.Tag)
            .ToHashSet();

        return winner.Require.Require.Any(claimed.Contains);
    }

    // -----------------------------------------------------------------
    // Declaration granularity.
    //
    // A `Shadows` entry naming a whole pack was the first shape tried, and it
    // is too coarse to be worth having. The rifle legitimately displaces
    // base's locomotion, so it declares "base" — and that one word then also
    // blanket-approves the accidental capture of base/fall, base/jump_rise
    // and base/crouch_space. The check passed clean on the exact bug it was
    // built to find.
    //
    // Entries therefore name the CLIP being displaced. The rifle declares
    // that it displaces base/loco_space and base/idle; the moment the same
    // rule starts displacing base/fall as well, base/fall is not on the list
    // and the install fails. A bare pack id is still accepted as the coarse
    // opt-out, but it is the exception rather than the shape.
    // -----------------------------------------------------------------
    private static bool IsDeclared(HashSet<string> declared, AnimRule loser) =>
        declared.Contains(loser.Clip.ToString()) || declared.Contains(loser.Origin);

    public enum Verdict
    {
        /// This pack outranks another pack's clip without declaring it.
        UndeclaredShadow,
        /// Declared, but total — the other clip can never play again.
        TotalBurial,
        /// This pack's own rule arrived unreachable under an existing one.
        DeadOnArrival,
        /// One of this pack's rules buries another of its own.
        SelfOverlap,
    }

    public readonly record struct Violation(
        Verdict Kind, string Severity, AnimRule Winner, AnimRule Loser, Region Overlap, bool Total);

    // -----------------------------------------------------------------
    // Detection is separated from presentation so the check can be
    // asserted on without parsing message strings.
    //
    // That separation is not cosmetic. A validator whose only output is
    // formatted prose can only be tested by string matching, which nobody
    // maintains — so the validator goes untested, and a validator that has
    // silently become a no-op is indistinguishable from one that is passing.
    // `Tests/ShadowFixtures.cs` asserts against this structured form.
    // -----------------------------------------------------------------
    public static List<Violation> Inspect(RuleTable table, AnimPack pack)
    {
        var found = new List<Violation>();
        var declared = new HashSet<string>(pack.Shadows ?? Array.Empty<string>());

        foreach (var s in Find(table))
        {
            bool iWin  = s.Winner.Origin == pack.Id;
            bool iLose = s.Loser.Origin  == pack.Id;

            // Pre-existing overlap between two other packs. It was reported at
            // whichever install created it; repeating it here is noise.
            if (!iWin && !iLose) continue;

            if (iWin && iLose)
            {
                // Both mine. The author wrote these adjacently in one array, so
                // ordering is at least visible. Only a total shadow is a bug,
                // and then it is a dead rule rather than a surprise.
                if (s.Total)
                    found.Add(new(Verdict.SelfOverlap, "WARN", s.Winner, s.Loser, s.Overlap, s.Total));
                continue;
            }

            if (iWin)
            {
                if (IsSelfScoped(s.Winner, pack)) continue;

                if (!IsDeclared(declared, s.Loser))
                    found.Add(new(Verdict.UndeclaredShadow, "ERROR", s.Winner, s.Loser, s.Overlap, s.Total));
                else if (s.Total)
                    found.Add(new(Verdict.TotalBurial, "WARN", s.Winner, s.Loser, s.Overlap, s.Total));
            }
            else // iLose
            {
                // Symmetric hazard: a pack installed under an existing one and
                // arrived dead. Only reported when total — anything less fires
                // for every weapon installed after an ability pack.
                if (s.Total)
                    found.Add(new(Verdict.DeadOnArrival, "ERROR", s.Winner, s.Loser, s.Overlap, s.Total));
            }
        }

        return found;
    }

    /// The install-time gate. Everything a pack is about to do to the rules
    /// already in the table, checked against what it declared it would do.
    public static List<Issue> CheckDeclarations(RuleTable table, AnimPack pack) =>
        Inspect(table, pack).Select(v => new Issue(v.Severity, Describe(v, pack))).ToList();

    private static string Describe(Violation v, AnimPack pack) => v.Kind switch
    {
        Verdict.UndeclaredShadow =>
            $"pack '{pack.Id}': '{v.Winner.Clip}' {(v.Total ? "buries" : "shadows")} " +
            $"'{v.Loser.Clip}' from '{v.Loser.Origin}' over [{v.Overlap}] — " +
            $"add \"{v.Loser.Clip}\" to Shadows if that is intended, " +
            $"or narrow the rule so it stops capturing those states",

        Verdict.TotalBurial =>
            $"pack '{pack.Id}': '{v.Winner.Clip}' buries '{v.Loser.Clip}' " +
            $"({v.Loser.Origin}) entirely, not just in part — " +
            $"uninstalling '{pack.Id}' is the only way that clip plays again",

        Verdict.DeadOnArrival =>
            $"pack '{pack.Id}': rule '{v.Loser.Clip}' can never win — " +
            $"'{v.Winner.Clip}' from '{v.Winner.Origin}' (Order {v.Winner.Order} " +
            $"vs {v.Loser.Order}) matches every state it does",

        Verdict.SelfOverlap =>
            $"pack '{pack.Id}': rule '{v.Loser.Clip}' is unreachable — " +
            $"its own '{v.Winner.Clip}' covers every state it matches",

        _ => $"pack '{pack.Id}': {v.Kind}",
    };

    /// Human-readable dump of the whole precedence structure. Print this when
    /// a clip is "not playing" and the reason is a rule three packs away.
    public static string Explain(RuleTable table)
    {
        var sb = new StringBuilder();

        foreach (var group in table.All.GroupBy(r => r.Slot))
        {
            sb.AppendLine($"slot {group.Key}:");
            foreach (var r in group)
                sb.AppendLine($"    [{r.Order,4}] {r.Clip,-28} {r.Origin,-10} {Region.Of(r)}");
        }

        var shadows = Find(table);
        if (shadows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("shadows:");
            foreach (var s in shadows) sb.AppendLine($"    {s}");
        }

        return sb.ToString();
    }
}
