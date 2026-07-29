using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace CharacterKit.Animation;

// =====================================================================
// Sweep domain derivation.
//
// The sweep used to take its tag combos from the caller and default its
// speeds to { 0, 0.5, 2.0, 4.4, 4.6, 7.5 } — numbers chosen to straddle
// thresholds somebody remembered. That is why it only PARTIALLY covered the
// insertion hazard: a pack introducing a threshold at 6.0, or a tag nobody
// added to the hand-written combo list, was sampled by luck.
//
// Both axes are derivable from the table itself:
//
//   Tag alphabet — every tag named in any rule's Require or Exclude. The
//     combinatorics stay bounded because tags are already namespaced, and
//     within a namespace they are usually mutually exclusive: one weapon,
//     one stance. That turns 2^n into a product of small factors.
//
//   Field samples — for every Condition, emit points either side of the
//     threshold AND either side of its hysteresis-widened threshold. Classic
//     boundary-value coverage, generated from the rules that exist rather
//     than from memory.
//
// The result is complete with respect to the installed rule set, by
// construction, and it re-derives itself when a pack adds a threshold.
// =====================================================================
public sealed class SweepDomain
{
    /// Namespaces whose tags CAN co-occur, so they need a power set rather
    /// than one-or-none. Getting this list wrong is the one way the sweep
    /// silently under-covers, so it is explicit rather than inferred:
    /// `status.injured` and `status.burning` are simultaneous, but a
    /// character is never holding two weapons or in two stances.
    public HashSet<string> IndependentNamespaces = new() { "status" };

    /// Guard against a pack turning the sweep into a hang. Exceeding it is
    /// reported, not silently truncated.
    public int MaxStates = 50_000;

    public float Epsilon = 1e-3f;

    public readonly List<StringName[]> TagCombos = new();
    public readonly Dictionary<string, float[]> Axes = new();
    public readonly List<Issue> Issues = new();

    public long StateCount
    {
        get
        {
            long n = TagCombos.Count;
            foreach (var a in Axes.Values) n *= a.Length;
            return n;
        }
    }

    public static SweepDomain FromTable(RuleTable table, Action<SweepDomain> configure = null)
    {
        var d = new SweepDomain();
        configure?.Invoke(d);
        d.DeriveTags(table);
        d.DeriveAxes(table);
        d.CheckBudget();
        return d;
    }

    /// Extra axis the rules don't mention. StrafeAngle is the usual one — no
    /// rule keys on it, so derivation correctly drops it, but it still moves
    /// the blendspace and a golden that covers it catches directional holes.
    public SweepDomain AddAxis(Field f, params float[] values)
    {
        Axes[Region.KeyOf(f, null)] = values;
        return this;
    }

    public SweepDomain AddScalarAxis(StringName key, params float[] values)
    {
        Axes[Region.KeyOf(Field.Scalar, key)] = values;
        return this;
    }

    // -----------------------------------------------------------------

    private void DeriveTags(RuleTable table)
    {
        var alphabet = new HashSet<StringName>();
        foreach (var r in table.All)
        {
            foreach (var t in r.Require.Require) alphabet.Add(t);
            foreach (var t in r.Require.Exclude) alphabet.Add(t);
        }

        // Namespace = text before the first dot. "weapon.rifle" -> "weapon".
        var byNamespace = alphabet
            .GroupBy(t =>
            {
                var s = t.ToString();
                int dot = s.IndexOf('.');
                return dot < 0 ? s : s[..dot];
            })
            .OrderBy(g => g.Key)
            .ToList();

        // Each namespace contributes a list of alternatives, one of which is
        // "none of them". Exclusive namespaces contribute n+1; independent
        // ones contribute their full power set.
        var choices = new List<List<StringName[]>>();

        foreach (var ns in byNamespace)
        {
            var tags = ns.OrderBy(t => t.ToString()).ToArray();
            var options = new List<StringName[]>();

            if (IndependentNamespaces.Contains(ns.Key))
            {
                for (int mask = 0; mask < (1 << tags.Length); mask++)
                    options.Add(Enumerable.Range(0, tags.Length)
                                          .Where(b => (mask & (1 << b)) != 0)
                                          .Select(b => tags[b])
                                          .ToArray());
            }
            else
            {
                options.Add(Array.Empty<StringName>());
                foreach (var t in tags) options.Add(new[] { t });
            }

            choices.Add(options);
        }

        TagCombos.Clear();
        foreach (var pick in Cartesian(choices))
            TagCombos.Add(pick.SelectMany(x => x).ToArray());

        if (TagCombos.Count == 0) TagCombos.Add(Array.Empty<StringName>());
    }

    private void DeriveAxes(RuleTable table)
    {
        var thresholds = new Dictionary<string, SortedSet<float>>();

        foreach (var r in table.All)
        foreach (var c in r.Conditions)
        {
            string key = Region.KeyOf(c.Field, c.ScalarKey);
            if (!thresholds.TryGetValue(key, out var set))
                thresholds[key] = set = new SortedSet<float>();

            // Both the nominal threshold and the hysteresis-widened one. A
            // rule with hysteresis has two edges, and the sweep reports the
            // cold and sticky answers separately at each.
            set.Add(c.Value);
            if (c.Hysteresis != 0f)
                set.Add(c.Op is Cmp.Greater or Cmp.GreaterOrEqual
                    ? c.Value - c.Hysteresis
                    : c.Value + c.Hysteresis);
        }

        foreach (var (key, set) in thresholds)
        {
            var samples = new SortedSet<float> { 0f };
            foreach (var v in set)
            {
                samples.Add(v - Epsilon);
                samples.Add(v + Epsilon);
            }
            // One point clear of every threshold, so "well past the top of the
            // range" is covered rather than assumed.
            samples.Add(set.Max + Mathf.Max(1f, set.Max * 0.25f));

            if (Axes.ContainsKey(key)) continue;   // caller-supplied axis wins
            Axes[key] = samples.ToArray();
        }
    }

    private void CheckBudget()
    {
        if (StateCount > MaxStates)
            Issues.Add(new("WARN",
                $"sweep domain is {StateCount} states (budget {MaxStates}) — " +
                $"{TagCombos.Count} tag combos x " +
                string.Join(" x ", Axes.Select(a => $"{a.Value.Length} {a.Key}")) +
                ". Add namespaces to IndependentNamespaces only when needed, " +
                "or raise MaxStates deliberately."));
    }

    private static IEnumerable<T[]> Cartesian<T>(List<List<T>> lists)
    {
        if (lists.Count == 0) { yield return Array.Empty<T>(); yield break; }

        var idx = new int[lists.Count];
        while (true)
        {
            var row = new T[lists.Count];
            for (int i = 0; i < lists.Count; i++) row[i] = lists[i][idx[i]];
            yield return row;

            int k = lists.Count - 1;
            while (k >= 0 && ++idx[k] >= lists[k].Count) { idx[k] = 0; k--; }
            if (k < 0) yield break;
        }
    }

    // -----------------------------------------------------------------
    // Applying an assignment to CharState.
    //
    // Publish() derives PlanarSpeed, VerticalSpeed, StrafeAngle and SlopeAngle
    // from LocalVelocity and GroundNormal, and ACCUMULATES the ground timers.
    // So the source fields are written before Publish and the accumulated ones
    // after it — writing them in the wrong order is how a sweep ends up
    // testing a state it never actually built.
    // -----------------------------------------------------------------
    public static void Apply(CharState s, IReadOnlyDictionary<string, float> assignment,
                             IReadOnlyCollection<StringName> tags, float dt = 1f / 60f)
    {
        foreach (var t in s.Tags.All.ToList()) s.Tags.Remove(t);
        foreach (var t in tags) s.Tags.Add(t);

        float speed = Get(assignment, nameof(Field.PlanarSpeed), 0f);
        float angle = Get(assignment, nameof(Field.StrafeAngle), 0f);
        float vert  = Get(assignment, nameof(Field.VerticalSpeed), 0f);
        float slope = Get(assignment, nameof(Field.SlopeAngle), 0f);

        s.LocalVelocity = new Vector3(Mathf.Sin(angle) * speed, vert, Mathf.Cos(angle) * speed);
        s.GroundNormal  = new Vector3(Mathf.Sin(slope), Mathf.Cos(slope), 0f).Normalized();
        s.Grounded      = !tags.Contains(T.Airborne);

        foreach (var (k, v) in assignment)
            if (k.StartsWith("scalar:", StringComparison.Ordinal))
                s.SetScalar(k["scalar:".Length..], v);

        s.Publish(dt);

        // Written after Publish because Publish increments them.
        if (assignment.TryGetValue(nameof(Field.TimeGrounded), out var tg)) s.TimeGrounded = tg;
        if (assignment.TryGetValue(nameof(Field.TimeAirborne), out var ta)) s.TimeAirborne = ta;
        if (assignment.TryGetValue(nameof(Field.TurnRate),     out var tr)) s.TurnRate     = tr;
    }

    private static float Get(IReadOnlyDictionary<string, float> d, string k, float fallback) =>
        d.TryGetValue(k, out var v) ? v : fallback;

    /// Every (tags, assignment) pair in the domain, with a stable key.
    public IEnumerable<(string Key, StringName[] Tags, Dictionary<string, float> Assignment)> States()
    {
        var keys = Axes.Keys.OrderBy(k => k).ToList();
        var valueLists = keys.Select(k => Axes[k].ToList()).ToList();

        foreach (var combo in TagCombos)
        foreach (var values in Cartesian(valueLists))
        {
            var assignment = new Dictionary<string, float>();
            for (int i = 0; i < keys.Count; i++) assignment[keys[i]] = values[i];

            string tagPart = combo.Length == 0
                ? "(no tags)"
                : string.Join('+', combo.Select(t => t.ToString()).OrderBy(x => x));

            string numPart = string.Join(' ', keys.Select((k, i) => $"{k}={values[i]:0.###}"));

            yield return ($"{tagPart} | {numPart}", combo, assignment);
        }
    }
}
