using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace CharacterKit.Animation;

public enum Axis { Whole, X, Y }

/// Routes a pack-authored scalar in CharState to an AnimationTree parameter,
/// so a pack can drive its own blendspace without the director knowing about it.
public sealed class ScalarBinding
{
    public StringName Scalar;
    public string     Param;
    public Axis       Axis = Axis.Whole;
    public float      Scale = 1f;
}

public sealed class LayerDef
{
    public StringName Slot;
    public bool       Additive;
    public StringName BoneMask;
    public float      DefaultWeight;

    /// True when this layer resolving to nothing is a normal resting state
    /// rather than a hole.
    ///
    /// `<NULL>` used to carry both meanings at once. On `base` it is a T-pose
    /// in production, which is exactly what AssertNoHoles exists to catch. On
    /// an additive or request-claimed layer it is the correct answer almost
    /// all the time — SwitchClip drives the weight to 0 and nothing is wrong.
    /// Both tripped the same assertion, so a clean run reported one error per
    /// unresolved optional slot per swept state: tens of thousands of lines
    /// that all said "the upper body is empty while unarmed".
    ///
    /// Defaults to false — must always resolve. The dangerous case is the one
    /// that gets a declaration, not the safe one.
    public bool Optional;
}

// =====================================================================
// The pack interface.
//
// Everything a unit of installable content brings, plus the two things it
// must declare about its relationship to the rest of the system:
//   Requires — packs that must already be installed
//   Serves   — the (slot, tag) claims it promises to answer
//
// Serves is what lets an ability's claims be checked against a pack without
// instantiating either. It is the compiler the shared tag vocabulary
// otherwise doesn't have.
//
// Shadows is the third member of the same family, and it exists because the
// first two were not enough. Requires makes dependency explicit; Serves makes
// the ability contract explicit; Shadows makes PRECEDENCE explicit. Each names
// a relationship a pack really has to the rest of the system but that is
// invisible in its own code, and each is checked at install against what is
// actually there.
//
// Precedence was the one left undeclared, and it is the one the flat rule
// table gets criticized for: a rule inserted at the wrong Order silently
// changes states nobody was thinking about. Declaring it turns a silent
// behavior change into a failed install that names the states.
// =====================================================================
public sealed class AnimPack
{
    public string Id;
    public int    Order;

    /// Pack ids that must be installed first. Checked at install, not assumed.
    public string[] Requires = Array.Empty<string>();

    /// Clips from other packs this pack is allowed to displace. Displacing
    /// anything not listed here fails the install and names the region.
    ///
    /// Clip granularity rather than pack granularity, because pack
    /// granularity does not work: the rifle really does displace base's
    /// locomotion, so "base" would be a legitimate entry — and it would then
    /// also wave through the accidental capture of base/fall, which is the
    /// exact bug the check exists to find. A bare pack id is still accepted
    /// as a coarse opt-out for cases that genuinely mean "all of it".
    ///
    /// Rules gated behind a tag in this pack's own `Serves` are exempt — they
    /// only fire in states this pack was asked to own, so their shadow is
    /// scoped by construction. Without that exemption a grapple pack would
    /// have to list every weapon clip that will ever exist.
    public string[] Shadows = Array.Empty<string>();

    public AnimationLibrary Library;
    public LayerDef[]       Layers   = Array.Empty<LayerDef>();
    public AnimRule[]       Rules    = Array.Empty<AnimRule>();
    public ScalarBinding[]  Bindings = Array.Empty<ScalarBinding>();

    /// Claims this pack promises to serve. Abilities declare the other side.
    public Claim[] Serves = Array.Empty<Claim>();

    /// Loads a library only if it is actually there.
    ///
    /// A pack whose clips have not been authored yet is a normal state while
    /// the design is being settled — the rules, the ordering and the shadow
    /// relationships are all still worth validating without a single .res on
    /// disk. Plain GD.Load prints a three-frame stack trace per missing file,
    /// which buries the report the harness exists to produce.
    public static AnimationLibrary LoadLibrary(string path) =>
        ResourceLoader.Exists(path) ? GD.Load<AnimationLibrary>(path) : null;
}

public sealed class InstallResult
{
    public bool Ok => Issues.All(i => i.Severity != "ERROR");
    public readonly List<Issue> Issues = new();
}

internal sealed class Layer
{
    public LayerDef  Def;
    public string    Owner;          // pack id, so uninstall removes only its own
    public float     Weight;
    public AnimRule  Current;
    public float     DwellRemaining;
}

/// Facade the rest of the game talks to. Nothing outside this file should
/// ever touch AnimationTree directly.
public sealed partial class AnimDirector : Node
{
    [Export] public AnimationTree   Tree;
    [Export] public AnimationPlayer Player;
    [Export] public bool ValidateOnInstall = true;

    /// Sweeps the derived state space before and after each install and
    /// reports what moved. Costs real time on a large table, so it is the
    /// debug-build default rather than the shipping one.
    [Export] public bool SweepOnInstall = true;

    public readonly RuleTable Rules = new();

    private readonly Dictionary<StringName, Layer> _layers = new();
    private readonly List<AnimRequest> _active = new();
    private readonly List<(string Pack, ScalarBinding Binding)> _bindings = new();
    private readonly Dictionary<string, AnimPack> _installed = new();

    /// Registration sequence breaks Order ties in the rule table, so it is
    /// part of the table's meaning and has to be reproducible. A Dictionary's
    /// enumeration order is not a promise; this is.
    private readonly List<string> _installOrder = new();

    private int _nextId = 1;

    public IReadOnlyCollection<string> InstalledPacks => _installOrder;

    // ---- installation -------------------------------------------------

    public InstallResult Install(AnimPack pack)
    {
        var result = new InstallResult();

        if (_installed.ContainsKey(pack.Id))
        {
            result.Issues.Add(new("WARN", $"pack '{pack.Id}' already installed"));
            return result;
        }

        foreach (var dep in pack.Requires)
            if (!_installed.ContainsKey(dep))
                result.Issues.Add(new("ERROR", $"pack '{pack.Id}' requires '{dep}', which is not installed"));

        if (!result.Ok) return result;

        if (pack.Library is not null)
            Player.AddAnimationLibrary(pack.Id, pack.Library);

        foreach (var l in pack.Layers)
        {
            if (_layers.TryGetValue(l.Slot, out var existing))
            {
                // Sharing a slot is normal — grapple and rifle both use
                // upperbody. Only the first definition owns the layer.
                result.Issues.Add(new("INFO",
                    $"pack '{pack.Id}' reuses slot '{l.Slot}' owned by '{existing.Owner}'"));
                continue;
            }
            _layers[l.Slot] = new Layer { Def = l, Owner = pack.Id, Weight = l.DefaultWeight };
        }

        foreach (var b in pack.Bindings)
            _bindings.Add((pack.Id, b));

        Rules.Register(pack.Id, pack.Order, pack.Rules);
        _installed[pack.Id] = pack;
        _installOrder.Add(pack.Id);

        if (ValidateOnInstall)
        {
            result.Issues.AddRange(ValidatePack(pack));
            result.Issues.AddRange(RuleOverlap.CheckDeclarations(Rules, pack));

            if (SweepOnInstall)
                result.Issues.AddRange(SweepDelta(pack));
        }

        // A pack that fails validation must not stay half-installed. `Ok ==
        // false` is supposed to mean nothing changed; it previously meant
        // "nothing changed except the rules, layers, bindings and library",
        // and EquipmentSystem.Equip bails on !Ok without recording the pack id
        // — so a rejected pack stayed installed with nothing tracking it.
        if (!result.Ok) Uninstall(pack.Id);

        return result;
    }

    /// What this install did to every state in the derived domain.
    private List<Issue> SweepDelta(AnimPack pack)
    {
        var slots = _layers.Keys.OrderBy(s => s.ToString()).ToList();
        if (slots.Count == 0) return new List<Issue>();

        // Domain derived from the post-install table, so thresholds and tags
        // the new pack introduced are sampled on BOTH sides of the diff.
        var domain = SweepDomain.FromTable(Rules);

        var issues = new List<Issue>(domain.Issues);
        var after  = RuleValidation.Sweep(Rules, slots, domain);
        var before = RuleValidation.Sweep(RebuildWithout(pack.Id), slots, domain);

        issues.AddRange(RuleValidation.DiffSweep(pack.Id, before, after));
        issues.AddRange(RuleValidation.AssertNoHoles(after));
        return issues;
    }

    /// The rule table as it would be without one pack, registered in the same
    /// order so Order ties break identically.
    private RuleTable RebuildWithout(string excludePackId)
    {
        var t = new RuleTable();
        foreach (var id in _installOrder)
        {
            if (id == excludePackId) continue;
            var p = _installed[id];
            t.Register(p.Id, p.Order, p.Rules);
        }
        return t;
    }

    /// Fully reverses Install. Every list Install touches is unwound here —
    /// the earlier version dropped only the rules, so bindings accumulated
    /// across equip cycles and layers outlived the pack that defined them.
    public void Uninstall(string packId)
    {
        if (!_installed.Remove(packId, out var pack)) return;

        _installOrder.Remove(packId);
        Rules.Unregister(packId);
        _bindings.RemoveAll(b => b.Pack == packId);

        foreach (var slot in _layers.Where(kv => kv.Value.Owner == packId)
                                    .Select(kv => kv.Key).ToList())
        {
            Tree.Set($"parameters/{slot}_w/blend_amount", 0f);
            _layers.Remove(slot);
        }

        // Requests owned by this pack's content can outlive it if an ability
        // is mid-phase; drop them rather than leave a claim on a dead slot.
        _active.RemoveAll(r => !_layers.ContainsKey(r.Slot));

        if (pack.Library is not null)
            Player.RemoveAnimationLibrary(packId);
    }

    private List<Issue> ValidatePack(AnimPack pack)
    {
        var issues = new List<Issue>();
        var probe = new CharState();

        foreach (var claim in pack.Serves)
        {
            foreach (var t in probe.Tags.All.ToList()) probe.Tags.Remove(t);
            probe.Publish(1f / 60f);

            var without = Rules.Resolve(claim.Slot, probe);
            probe.Tags.Add(claim.Tag);
            var with = Rules.Resolve(claim.Slot, probe);

            if (with is null || with == without)
                issues.Add(new("ERROR",
                    $"pack '{pack.Id}' declares it serves {claim.Slot}/{claim.Tag}, " +
                    $"but no rule answers it"));
        }

        // Escalated from INFO. A lambda is not just a serialization problem
        // any more: overlap analysis cannot see inside it, so the rule is
        // treated as reaching every state in its slot and the shadow check
        // degrades to guessing for anything below it.
        foreach (var r in pack.Rules.Where(r => r.When is not null))
            issues.Add(new("WARN",
                $"pack '{pack.Id}': rule for '{r.Clip}' uses a lambda predicate — " +
                $"the pack cannot be serialized as a Resource, and shadow " +
                $"analysis must assume this rule matches everything in " +
                $"slot '{r.Slot}'"));

        return issues;
    }

    // ---- requests -----------------------------------------------------

    public AnimHandle Request(StringName slot, StringName tag, Priority priority,
                              float blendIn = 0.12f, object owner = null)
    {
        var r = new AnimRequest
        {
            Slot = slot, Tag = tag, Priority = priority,
            BlendIn = blendIn, Owner = owner, Id = _nextId++
        };
        _active.Add(r);
        return new AnimHandle(r.Id);
    }

    public void Release(AnimHandle h) => _active.RemoveAll(r => r.Id == h.Id);

    public void SetLayerWeight(StringName slot, float w)
    {
        if (_layers.TryGetValue(slot, out var l)) l.Weight = Mathf.Clamp(w, 0f, 1f);
    }

    // ---- per-frame ----------------------------------------------------

    public void Evaluate(CharState state, double delta)
    {
        float dt = (float)delta;

        var winners = _active
            .GroupBy(r => r.Slot)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => (int)r.Priority).First());

        foreach (var w in winners.Values)
            state.Tags.Add(w.Tag);

        foreach (var (slot, layer) in _layers)
        {
            layer.DwellRemaining = Mathf.Max(0f, layer.DwellRemaining - dt);

            // The currently winning rule is passed back in so its conditions
            // can apply hysteresis in the direction that keeps it winning.
            var next = Rules.ResolveRule(slot, state, layer.Current);

            if (!ReferenceEquals(next, layer.Current) && layer.DwellRemaining <= 0f)
            {
                float blend = winners.TryGetValue(slot, out var w) ? w.BlendIn : 0.15f;
                SwitchClip(layer, next, blend);
                layer.DwellRemaining = next?.MinDwell ?? 0f;
            }
        }

        PushBlendParameters(state);

        foreach (var w in winners.Values)
            state.Tags.Remove(w.Tag);
    }

    private void SwitchClip(Layer layer, AnimRule rule, float blendIn)
    {
        layer.Current = rule;

        if (rule?.Clip is null)
        {
            Tree.Set($"parameters/{layer.Def.Slot}_w/blend_amount", 0f);
            return;
        }

        Tree.Set($"parameters/{layer.Def.Slot}/animation", rule.Clip);
        Tree.Set($"parameters/{layer.Def.Slot}_seek/seek_request", 0.0);

        // NOTE: still a plain crossfade. Replace with inertialization —
        // snapshot the pose delta here and decay it over blendIn.
        Tree.Set($"parameters/{layer.Def.Slot}_w/blend_amount", layer.Weight);
    }

    private void PushBlendParameters(CharState s)
    {
        Tree.Set("parameters/loco_space/blend_position",
                 new Vector2(s.Future[2].Position.X, s.Future[2].Position.Z));
        Tree.Set("parameters/lean/blend_amount", Mathf.Clamp(s.TurnRate * 0.3f, -1f, 1f));

        foreach (var (slot, layer) in _layers)
            if (layer.Def.Additive)
                Tree.Set($"parameters/{slot}_w/blend_amount", layer.Weight);

        foreach (var (_, b) in _bindings)
        {
            float v = s.Scalar(b.Scalar) * b.Scale;
            if (b.Axis == Axis.Whole) { Tree.Set(b.Param, v); continue; }

            var cur = Tree.Get(b.Param).AsVector2();
            Tree.Set(b.Param, b.Axis == Axis.X ? new Vector2(v, cur.Y) : new Vector2(cur.X, v));
        }
    }

    // ---- debug --------------------------------------------------------

    public string Explain(StringName slot) =>
        Rules.LastDecision.TryGetValue(slot, out var s) ? s : "(no capture — set Rules.DebugCapture)";

    /// Every rule, its region, and every shadow between them. This is the
    /// "why is my clip not playing" dump when the reason is a rule in a pack
    /// you did not write.
    public string ExplainTable() => RuleOverlap.Explain(Rules);

    public string WhoOwns(StringName slot) =>
        _layers.TryGetValue(slot, out var l)
            ? $"layer:{l.Owner} clip:{l.Current?.Clip} from:{l.Current?.Origin}"
            : "(no such layer)";
}
