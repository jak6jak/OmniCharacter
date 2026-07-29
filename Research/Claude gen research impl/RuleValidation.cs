using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using CharacterKit.Abilities;

namespace CharacterKit.Animation;

public sealed record Issue(string Severity, string Message)
{
    public override string ToString() => $"[{Severity}] {Message}";
}

public static class RuleValidation
{
    // =================================================================
    // 1. Install-time checks. Run these in _Ready under #if DEBUG.
    // =================================================================
    public static List<Issue> Validate(
        RuleTable table,
        IReadOnlyCollection<StringName> knownClips,
        IReadOnlyCollection<StringName> requiredSlots)
    {
        var issues = new List<Issue>();
        var rules = table.All;

        // (a) Every referenced clip must actually exist in a registered library.
        foreach (var r in rules)
            if (!knownClips.Contains(r.Clip))
                issues.Add(new("ERROR", $"{r.Source}: clip '{r.Clip}' not found in any installed library"));

        // (b) Every slot needs a terminal rule — no tag requirements, no
        //     predicate — or some state will resolve to null and T-pose.
        foreach (var slot in requiredSlots)
        {
            bool hasTerminal = rules.Any(r => r.Slot == slot && r.IsTerminal);

            if (!hasTerminal)
                issues.Add(new("ERROR", $"slot '{slot}' has no unconditional fallback rule"));
        }

        // (c) Rules that contradict themselves and can never fire at all.
        issues.AddRange(RuleOverlap.FindUnsatisfiable(table));

        // (d) Unreachable rules. This used to hand-check one narrow pattern —
        //     an earlier rule with no conditions whose required tags were a
        //     subset of a later rule's. It missed every case involving a
        //     numeric threshold, which is most of them. Region containment is
        //     exactly decidable, so ask the general question instead.
        foreach (var s in RuleOverlap.Find(table).Where(s => s.Total))
            issues.Add(new("WARN",
                $"{s.Loser.Origin}: '{s.Loser.Clip}' is unreachable — " +
                $"'{s.Winner.Clip}' ({s.Winner.Origin}) matches every state it does"));

        return issues;
    }

    // =================================================================
    // 2. State-space sweep. This is the actual guarantee: enumerate the
    //    states, resolve every slot, assert on the result. Snapshot it to a
    //    golden file and diff on every change.
    //
    //    Prefer this overload. The domain comes from the rule table, so it
    //    covers every tag any rule mentions and samples either side of every
    //    threshold any rule declares — including thresholds introduced by a
    //    pack the test file has never heard of. The hand-listed overload
    //    below covers whatever the caller remembered, which is the reason the
    //    golden could miss an insertion in the first place.
    // =================================================================
    public static Dictionary<string, string> Sweep(
        RuleTable table,
        IReadOnlyList<StringName> slots,
        SweepDomain domain,
        IReadOnlyCollection<StringName> optionalSlots = null)
    {
        var results = new Dictionary<string, string>();
        var state = new CharState();

        foreach (var (key, tags, assignment) in domain.States())
        {
            SweepDomain.Apply(state, assignment, tags);
            results[key] = ResolveLine(table, slots, state, optionalSlots);
        }

        return results;
    }

    /// A required slot that resolved to nothing. This is the T-pose.
    public const string Hole = "<HOLE>";

    /// An optional slot that resolved to nothing. This is the layer at
    /// weight 0, which is correct and extremely common.
    public const string Empty = "--";

    /// One line of the golden: every slot's winner, with the pack that
    /// supplied it, plus the sticky answer when hysteresis makes it differ.
    ///
    /// The two flavours of "nothing" get different tokens on purpose. When
    /// they shared one, AssertNoHoles could not tell a production T-pose from
    /// an unarmed upper body, so it reported both — and since the second is
    /// the normal case in most of the state space, the real signal was buried
    /// under tens of thousands of false ones.
    private static string ResolveLine(RuleTable table, IReadOnlyList<StringName> slots,
                                      CharState state,
                                      IReadOnlyCollection<StringName> optionalSlots = null)
    {
        var line = new StringBuilder();

        foreach (var slot in slots)
        {
            bool optional = optionalSlots is not null && optionalSlots.Contains(slot);

            // Cold: what you get entering this state fresh.
            var cold = table.ResolveRule(slot, state);

            // Sticky: what you get if that same rule were already winning.
            // With hysteresis these differ, and the sweep used to report only
            // the cold half — a green test over untested behavior.
            var sticky = table.ResolveRule(slot, state, cold);

            string name = cold is null
                ? (optional ? Empty : Hole)
                : $"{cold.Clip}@{cold.Origin}";

            if (cold is not null && !ReferenceEquals(cold, sticky))
                name += $" (sticky:{sticky?.Clip.ToString() ?? (optional ? Empty : Hole)})";

            line.Append($"{slot}={name} ");
        }

        return line.ToString().TrimEnd();
    }

    /// Manual domain. Kept for targeted tests where you want a handful of
    /// named states rather than the full derived cross product.
    public static Dictionary<string, string> Sweep(
        RuleTable table,
        IReadOnlyList<StringName> slots,
        IReadOnlyList<StringName[]> tagCombos,
        float[] speeds = null,
        float[] strafeAngles = null,
        IReadOnlyDictionary<StringName, float> scalars = null)
    {
        speeds ??= new[] { 0f, 0.5f, 2.0f, 4.4f, 4.6f, 7.5f };  // straddle thresholds
        strafeAngles ??= new[] { 0f, Mathf.Pi / 4, Mathf.Pi / 2, Mathf.Pi };

        var results = new Dictionary<string, string>();
        var state = new CharState();

        foreach (var combo in tagCombos)
        foreach (var speed in speeds)
        foreach (var angle in strafeAngles)
        {
            foreach (var t in state.Tags.All.ToList()) state.Tags.Remove(t);
            foreach (var t in combo) state.Tags.Add(t);

            state.LocalVelocity = new Vector3(Mathf.Sin(angle) * speed, 0, Mathf.Cos(angle) * speed);
            state.Grounded = !combo.Contains(T.Airborne);
            state.GroundNormal = Vector3.Up;

            // Rules can key on pack-authored scalars. Without these the sweep
            // silently evaluates every Field.Scalar condition against 0.
            if (scalars is not null)
                foreach (var (scalarKey, value) in scalars) state.SetScalar(scalarKey, value);

            state.Publish(1f / 60f);

            var key = $"{string.Join('+', combo.Select(t => t.ToString()))} | " +
                      $"spd={speed:0.0} ang={Mathf.RadToDeg(angle):0}";

            results[key] = ResolveLine(table, slots, state);
        }
        return results;
    }

    /// Fails loudly on any state that would T-pose — once, with examples,
    /// rather than once per state.
    ///
    /// A hole is rarely a single state. A slot missing a fallback is missing
    /// it across a whole region of the sweep, so the honest report is a count
    /// and a sample; one issue per state is the same fact restated thousands
    /// of times, and it pushes everything else off the screen.
    public static List<Issue> AssertNoHoles(Dictionary<string, string> sweep, int maxExamples = 4)
    {
        var holes = sweep.Where(kv => kv.Value.Contains(Hole)).ToList();
        if (holes.Count == 0) return new List<Issue>();

        return new List<Issue>
        {
            new("ERROR",
                $"{holes.Count} of {sweep.Count} swept states leave a required slot " +
                $"unresolved (T-pose). Examples:\n        " +
                string.Join("\n        ", holes.Take(maxExamples).Select(kv => $"{kv.Key}\n            {kv.Value}")))
        };
    }

    /// Write next to your test fixtures and diff in CI. When installing a new
    /// pack silently changes an unrelated state, this is what tells you.
    public static string ToGolden(Dictionary<string, string> sweep) =>
        string.Join('\n', sweep.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}\n    {kv.Value}"));

    // =================================================================
    // Golden diff, attributed.
    //
    // The golden already carried `clip@origin`, so a diff has always been
    // able to say WHICH pack supplied the new answer. What was missing was
    // timing: the diff only ran when somebody ran the test file, so the
    // report arrived nowhere near the edit that caused it. Running it inside
    // Install recovers in time what an HFSM gets in structure — the mistake
    // reports at the insertion.
    //
    // Both dictionaries must come from the SAME domain or the keys will not
    // line up. AnimDirector derives the domain from the post-install table
    // and uses it for both halves.
    // =================================================================
    public static List<Issue> DiffSweep(
        string packId,
        Dictionary<string, string> before,
        Dictionary<string, string> after,
        int maxExamples = 4)
    {
        var issues = new List<Issue>();
        var claimed = new List<string>();
        var foreign = new List<string>();

        foreach (var (key, now) in after)
        {
            if (!before.TryGetValue(key, out var was) || was == now) continue;

            // Attribution by origin tag. If the new answer names this pack
            // somewhere, the pack took the state — that is what installing it
            // is for. If it does not, installing this pack changed the outcome
            // of a contest between two OTHER packs, which no one asked for.
            if (now.Contains($"@{packId}")) claimed.Add($"{key}: {was}  ->  {now}");
            else                            foreign.Add($"{key}: {was}  ->  {now}");
        }

        if (foreign.Count > 0)
            issues.Add(new("ERROR",
                $"installing '{packId}' changed {foreign.Count} state(s) it does not serve — " +
                $"the winner in each is a different pack. " +
                $"Examples:\n        {string.Join("\n        ", foreign.Take(maxExamples))}"));

        if (claimed.Count > 0)
            issues.Add(new("INFO",
                $"'{packId}' took over {claimed.Count} state(s). " +
                $"Examples:\n        {string.Join("\n        ", claimed.Take(maxExamples))}"));

        return issues;
    }

    // =================================================================
    // Ramp test.
    //
    // Sweep is stateless, so it can see hysteresis but cannot exercise it,
    // and cannot see MinDwell at all — both only exist across frames. This
    // walks a value up and back down while carrying the active rule and the
    // dwell timer exactly as AnimDirector.Evaluate does, then counts how
    // many times the slot actually switched.
    //
    // A clean ramp through one threshold switches twice. More than that is
    // thrash, and it is the failure that looks like "the animation flickers"
    // rather than "the animation is wrong".
    // =================================================================
    public static List<Issue> RampTest(
        RuleTable table,
        StringName slot,
        StringName[] tags,
        Action<CharState, float> apply,
        float from, float to,
        int steps = 120,
        float dt = 1f / 60f,
        int maxSwitches = 2)
    {
        var issues = new List<Issue>();
        var state = new CharState();
        foreach (var t in tags) state.Tags.Add(t);

        AnimRule active = null;
        float dwell = 0f;
        int switches = 0;
        var path = new List<string>();

        void Step(float v)
        {
            apply(state, v);
            state.Publish(dt);

            dwell = Mathf.Max(0f, dwell - dt);
            var next = table.ResolveRule(slot, state, active);

            if (ReferenceEquals(next, active) || dwell > 0f) return;

            // The very first resolve is the layer acquiring a clip from empty,
            // not a switch between two. Counting it made every clean ramp
            // report three transitions against a limit of two, so the check
            // failed on hysteresis that was working: the rifle carry entered
            // at 6.0 and left at 5.5 exactly as authored, and was reported as
            // thrash.
            bool acquiring = active is null;

            if (!acquiring) switches++;
            path.Add(acquiring
                ? $"start:{next?.Clip.ToString() ?? "<NULL>"}"
                : $"{v:0.00}->{next?.Clip.ToString() ?? "<NULL>"}");

            active = next;
            dwell = next?.MinDwell ?? 0f;
        }

        for (int i = 0; i <= steps; i++) Step(Mathf.Lerp(from, to, i / (float)steps));
        for (int i = 0; i <= steps; i++) Step(Mathf.Lerp(to, from, i / (float)steps));

        if (switches > maxSwitches)
            issues.Add(new("ERROR",
                $"slot '{slot}' switched {switches}x over one ramp (max {maxSwitches}) — " +
                $"needs hysteresis or a longer MinDwell. Path: {string.Join(", ", path)}"));

        return issues;
    }

    /// Ramps planar speed, the most common thrash source.
    public static List<Issue> RampSpeed(RuleTable table, StringName slot,
                                        StringName[] tags, float max = 8f) =>
        RampTest(table, slot, tags,
                 (s, v) => s.LocalVelocity = new Vector3(0, 0, v),
                 0f, max);
}

// =====================================================================
// 3. Blendspace sample placement.
//
//    A directional blendspace is only correct if each sample sits at the
//    velocity the clip ACTUALLY travels at. Hand-typed positions like
//    (0.7, 0.7) for a 45-degree walk are the usual cause of foot sliding
//    that no amount of IK will fix — the blend produces a velocity the
//    controller isn't moving at.
// =====================================================================
public static class BlendspaceBuilder
{
    /// Average root velocity of a clip, in character space.
    /// Assumes root motion is on a position track for the root bone; adjust
    /// the track lookup to match how your rig is set up.
    // Godot.Animation must be qualified: this file sits in namespace
    // CharacterKit.Animation, so the bare name resolves to the namespace.
    public static Vector3 MeasureVelocity(Godot.Animation anim, string rootTrackPath = ":root")
    {
        int track = anim.FindTrack(rootTrackPath, Godot.Animation.TrackType.Position3D);
        if (track < 0 || anim.Length <= 0f) return Vector3.Zero;

        int keys = anim.TrackGetKeyCount(track);
        if (keys < 2) return Vector3.Zero;

        var first = anim.PositionTrackInterpolate(track, 0.0);
        var last  = anim.PositionTrackInterpolate(track, anim.Length);
        return (last - first) / anim.Length;
    }

    /// Build a 2D blendspace from a set of directional clips, placing each
    /// sample at its measured velocity rather than a guessed position.
    public static AnimationNodeBlendSpace2D Build(
        AnimationLibrary lib, string libId, IEnumerable<string> clipNames)
    {
        var space = new AnimationNodeBlendSpace2D();
        space.SetBlendMode(AnimationNodeBlendSpace2D.BlendModeEnum.Interpolated);

        foreach (var name in clipNames)
        {
            var anim = lib.GetAnimation(name);
            if (anim is null) { GD.PushWarning($"missing clip {name}"); continue; }

            var v = MeasureVelocity(anim);
            var node = new AnimationNodeAnimation();
            node.Animation = $"{libId}/{name}";

            // X = lateral, Y = forward. Same axes the trajectory is published in.
            space.AddBlendPoint(node, new Vector2(v.X, v.Z));
        }

        // A space with holes triangulates into garbage. Symmetric 8-way
        // coverage is the practical minimum for a strafing character;
        // mirror the left-side clips to get the right side for free.
        if (space.GetBlendPointCount() < 8)
            GD.PushWarning($"{libId}: only {space.GetBlendPointCount()} blend points — " +
                            "expect extrapolation artifacts in uncovered directions");

        return space;
    }
}

// =====================================================================
// 4. The ability/pack contract check.
//
//    This is the mitigation for the one real cost of keeping abilities and
//    packs separate: the tag vocabulary is shared by convention, and nothing
//    forces a pack to actually serve every tag a phase claims. A missing rule
//    is silent — the slot falls through to a base clip and the swing renders
//    as a fall.
//
//    Dry-run each phase, collect its (slot, tag) claims, and check that the
//    tag actually changes what the slot resolves to. If it doesn't, no
//    installed pack serves that claim.
// =====================================================================
public static class ContractValidation
{
    public static List<Issue> Check(RuleTable table, params AbilityPhase[] phases)
    {
        var issues = new List<Issue>();
        var probe = new CharState();

        foreach (var phase in phases)
        {
            var claims = Scope.DryRun(phase).Claims;

            foreach (var (slot, tag) in claims)
            {
                foreach (var t in probe.Tags.All.ToList()) probe.Tags.Remove(t);
                probe.GroundNormal = Vector3.Up;
                probe.Publish(1f / 60f);

                var without = table.Resolve(slot, probe);
                probe.Tags.Add(tag);
                var with = table.Resolve(slot, probe);

                if (with is null)
                    issues.Add(new("ERROR",
                        $"{phase.GetType().Name}: claims {slot}/{tag}, resolves to nothing"));
                else if (with == without)
                    issues.Add(new("ERROR",
                        $"{phase.GetType().Name}: claims {slot}/{tag}, but no installed pack " +
                        $"serves it — falls through to '{without}'"));
            }

            if (claims.Count == 0)
                issues.Add(new("WARN", $"{phase.GetType().Name}: declares no animation claim"));
        }
        return issues;
    }
}
