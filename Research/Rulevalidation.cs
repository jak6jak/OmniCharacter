// GEN BY CLAUDE

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
            bool hasTerminal = rules.Any(r =>
                r.Slot == slot &&
                r.When is null &&
                r.Require.Require.Length == 0 &&
                r.Require.Exclude.Length == 0);

            if (!hasTerminal)
                issues.Add(new("ERROR", $"slot '{slot}' has no unconditional fallback rule"));
        }

        // (c) Unreachable rules: a rule whose tag requirements are a superset
        //     of an earlier rule's in the same slot can never win.
        var bySlot = rules.GroupBy(r => r.Slot);
        foreach (var group in bySlot)
        {
            var ordered = group.ToList();
            for (int i = 0; i < ordered.Count; i++)
            for (int j = i + 1; j < ordered.Count; j++)
            {
                var earlier = ordered[i];
                var later   = ordered[j];

                bool earlierIsLooser =
                    earlier.When is null &&
                    earlier.Require.Exclude.Length == 0 &&
                    earlier.Require.Require.All(t => later.Require.Require.Contains(t));

                if (earlierIsLooser)
                    issues.Add(new("WARN",
                        $"{later.Source}: '{later.Clip}' is unreachable — " +
                        $"'{earlier.Clip}' ({earlier.Source}) always matches first"));
            }
        }

        return issues;
    }

    // =================================================================
    // 2. State-space sweep. This is the actual guarantee: enumerate the
    //    states you care about, resolve every slot, assert on the result.
    //    Snapshot it to a golden file and diff on every change.
    // =================================================================
    public static Dictionary<string, string> Sweep(
        RuleTable table,
        IReadOnlyList<StringName> slots,
        IReadOnlyList<StringName[]> tagCombos,
        float[] speeds = null,
        float[] strafeAngles = null)
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
            state.Publish(1f / 60f);

            var key = $"{string.Join('+', combo.Select(t => t.ToString()))} | " +
                      $"spd={speed:0.0} ang={Mathf.RadToDeg(angle):0}";

            var line = new StringBuilder();
            foreach (var slot in slots)
            {
                var clip = table.Resolve(slot, state);
                line.Append($"{slot}={clip?.ToString() ?? "<NULL>"} ");
            }
            results[key] = line.ToString().TrimEnd();
        }
        return results;
    }

    /// Fails loudly on any state that would T-pose.
    public static List<Issue> AssertNoHoles(Dictionary<string, string> sweep) =>
        sweep.Where(kv => kv.Value.Contains("<NULL>"))
             .Select(kv => new Issue("ERROR", $"unresolved slot in state: {kv.Key} -> {kv.Value}"))
             .ToList();

    /// Write next to your test fixtures and diff in CI. When installing a new
    /// pack silently changes an unrelated state, this is what tells you.
    public static string ToGolden(Dictionary<string, string> sweep) =>
        string.Join('\n', sweep.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}\n    {kv.Value}"));
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
    public static Vector3 MeasureVelocity(Animation anim, string rootTrackPath = ":root")
    {
        int track = anim.FindTrack(rootTrackPath, Animation.TrackType.Position3D);
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
