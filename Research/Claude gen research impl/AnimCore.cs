using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace CharacterKit.Animation;

public static class Slots
{
    public static readonly StringName Base      = "base";
    public static readonly StringName FullBody  = "fullbody";
    public static readonly StringName UpperBody = "upperbody";
    public static readonly StringName AddInjury = "add.injury";
    public static readonly StringName AddLean   = "add.lean";
}

public static class T
{
    public static readonly StringName Airborne      = "loco.airborne";
    public static readonly StringName Crouched      = "stance.crouch";
    public static readonly StringName WeaponUnarmed = "weapon.unarmed";
    public static readonly StringName WeaponRifle   = "weapon.rifle";
    public static readonly StringName ActionReload  = "action.reload";
    public static readonly StringName ActionDash    = "action.dash";
    public static readonly StringName StatusInjured = "status.injured";
}

public enum Priority
{
    Ambient    = 100,
    Locomotion = 200,
    Equipment  = 300,
    Ability    = 400,
    Reaction   = 800,
    Cinematic  = 1200,
}

/// A (slot, tag) pair. Abilities claim these; packs promise to serve them.
/// Validation checks the two sides agree.
public readonly record struct Claim(StringName Slot, StringName Tag);

public sealed class TagSet
{
    private readonly HashSet<StringName> _tags = new();

    public void Add(StringName t) => _tags.Add(t);
    public void Remove(StringName t) => _tags.Remove(t);
    public bool Has(StringName t) => _tags.Contains(t);
    public void Set(StringName t, bool on) { if (on) Add(t); else Remove(t); }
    public IEnumerable<StringName> All => _tags;
}

public sealed class TagQuery
{
    public StringName[] Require = Array.Empty<StringName>();
    public StringName[] Exclude = Array.Empty<StringName>();

    public static TagQuery All(params StringName[] tags) => new() { Require = tags };
    public static TagQuery None(params StringName[] tags) => new() { Exclude = tags };
    public TagQuery Without(params StringName[] tags) { Exclude = tags; return this; }

    public bool Matches(TagSet s)
    {
        foreach (var t in Require) if (!s.Has(t)) return false;
        foreach (var t in Exclude) if (s.Has(t)) return false;
        return true;
    }
}

public struct TrajectorySample
{
    public Vector3 Position;
    public float   Facing;
}

public sealed class CharState
{
    public readonly TagSet Tags = new();

    public Vector3 LocalVelocity;
    public Vector3 LocalAcceleration;
    public float   PlanarSpeed;
    public float   VerticalSpeed;
    public float   StrafeAngle;

    public readonly TrajectorySample[] Future = new TrajectorySample[3];
    public readonly TrajectorySample[] Past   = new TrajectorySample[3];
    public float TurnRate;

    public bool    Grounded;
    public float   TimeGrounded;
    public float   TimeAirborne;
    public Vector3 GroundNormal = Vector3.Up;
    public float   SlopeAngle;

    public float AimYaw;
    public float AimPitch;

    public float LegLength = 0.9f;
    public float ScaleFactor = 1f;

    private readonly Dictionary<StringName, float> _scalars = new();

    public void SetScalar(StringName key, float v) => _scalars[key] = v;
    public float Scalar(StringName key, float fallback = 0f) =>
        _scalars.TryGetValue(key, out var v) ? v : fallback;
    public void ClearScalar(StringName key) => _scalars.Remove(key);

    public bool RootMotionDrivesMovement;

    public void Publish(float dt)
    {
        var planar = new Vector2(LocalVelocity.X, LocalVelocity.Z);
        PlanarSpeed   = planar.Length();
        VerticalSpeed = LocalVelocity.Y;
        StrafeAngle   = PlanarSpeed > 0.05f ? Mathf.Atan2(planar.X, planar.Y) : 0f;
        SlopeAngle    = Mathf.Acos(Mathf.Clamp(GroundNormal.Dot(Vector3.Up), -1f, 1f));

        if (Grounded) { TimeGrounded += dt; TimeAirborne = 0f; }
        else          { TimeAirborne += dt; TimeGrounded = 0f; }
    }
}

// =====================================================================
// Conditions
//
// A rule's numeric test used to be a C# lambda, which meant a pack could
// never be anything but code. This shape is plain data, so a pack can be
// authored as an editor Resource. The lambda survives as an escape hatch
// for tests this can't express; ContractValidation flags packs that use it.
//
// Hysteresis lives here because it has nowhere better to be. A threshold
// that widens while the rule is already winning is the fix for a predicate
// parked on its boundary and re-resolving every frame.
// =====================================================================
public enum Field
{
    PlanarSpeed, VerticalSpeed, StrafeAngle, TurnRate,
    SlopeAngle, TimeGrounded, TimeAirborne, Scalar
}

public enum Cmp { Less, LessOrEqual, Greater, GreaterOrEqual }

public struct Condition
{
    public Field      Field;
    public StringName ScalarKey;      // used when Field == Field.Scalar
    public Cmp        Op;
    public float      Value;

    /// Slack applied only while the owning rule is already active.
    /// Enter at Value, leave at Value minus/plus Hysteresis.
    public float Hysteresis;

    public static Condition Above(Field f, float v, float hysteresis = 0f) =>
        new() { Field = f, Op = Cmp.Greater, Value = v, Hysteresis = hysteresis };

    public static Condition Below(Field f, float v, float hysteresis = 0f) =>
        new() { Field = f, Op = Cmp.Less, Value = v, Hysteresis = hysteresis };

    public static Condition ScalarAbove(StringName key, float v, float hysteresis = 0f) =>
        new() { Field = Field.Scalar, ScalarKey = key, Op = Cmp.Greater,
                Value = v, Hysteresis = hysteresis };

    public readonly bool Eval(CharState s, bool active)
    {
        float x = Field switch
        {
            Field.PlanarSpeed   => s.PlanarSpeed,
            Field.VerticalSpeed => s.VerticalSpeed,
            Field.StrafeAngle   => s.StrafeAngle,
            Field.TurnRate      => s.TurnRate,
            Field.SlopeAngle    => s.SlopeAngle,
            Field.TimeGrounded  => s.TimeGrounded,
            Field.TimeAirborne  => s.TimeAirborne,
            Field.Scalar        => s.Scalar(ScalarKey),
            _ => 0f
        };

        float threshold = Op is Cmp.Greater or Cmp.GreaterOrEqual
            ? Value - (active ? Hysteresis : 0f)
            : Value + (active ? Hysteresis : 0f);

        return Op switch
        {
            Cmp.Less           => x <  threshold,
            Cmp.LessOrEqual    => x <= threshold,
            Cmp.Greater        => x >  threshold,
            Cmp.GreaterOrEqual => x >= threshold,
            _ => false
        };
    }
}

public sealed class AnimRule
{
    public StringName  Slot = Slots.Base;
    public TagQuery    Require = new();
    public Condition[] Conditions = Array.Empty<Condition>();

    /// Escape hatch. A pack using this cannot be serialized as a Resource.
    public Func<CharState, bool> When;

    public StringName Clip;

    /// Minimum time this rule holds a slot once it wins. Catches thrash that
    /// hysteresis doesn't, such as a tag toggling on alternating frames.
    public float MinDwell = 0.1f;

    /// Whether this clip is a cycle that should keep running, or a one-shot
    /// that plays through and holds its last pose.
    ///
    /// It exists because resolution alone is not enough to keep a character
    /// animating. The table says which clip *should* be playing; nothing was
    /// checking that it still *is*, so any clip that reached its end left the
    /// character frozen while the rule kept winning. See ClipDriver.
    ///
    /// Strictly this is a property of the clip, not of the rule — but the pack
    /// is where authorship happens, and the pack is the only thing that knows
    /// whether it asked for a walk cycle or a punch. `ClipDriver` reconciles
    /// against it, and the engine harness cross-checks it against the imported
    /// resource's actual LoopMode.
    public bool Loops = true;

    /// This clip drives the whole body, so narrower layers must get out of the
    /// way while it plays.
    ///
    /// The first cut at layering had a masked upper body permanently blended
    /// over locomotion, which is right for a weapon carry pose and wrong for
    /// a sword swing — the swing owns the arms, and a `Sword_Idle` masked on
    /// top of it corrupted exactly the bones the swing was animating.
    ///
    /// This is the crude form of a coverage lattice: a real one would let each
    /// layer declare the region it drives and derive suppression from the
    /// overlap. One bool is enough while there are two layers, and it is
    /// honest about being a placeholder rather than pretending pairwise
    /// `Suppresses = [slot...]` lists would scale.
    public bool FullBody;

    internal string Source;
    internal int    Order;
    internal int    Index;

    public string Origin => Source;

    public bool Matches(CharState s, bool active)
    {
        if (!Require.Matches(s.Tags)) return false;
        foreach (var c in Conditions) if (!c.Eval(s, active)) return false;
        return When is null || When(s);
    }

    /// Can serve as a slot's unconditional fallback.
    public bool IsTerminal =>
        When is null && Conditions.Length == 0 &&
        Require.Require.Length == 0 && Require.Exclude.Length == 0;
}

public sealed class RuleTable
{
    private readonly List<AnimRule> _rules = new();
    private int _seq;

    public bool DebugCapture;
    public readonly Dictionary<StringName, string> LastDecision = new();

    public IReadOnlyList<AnimRule> All => _rules;

    public void Register(string source, int order, params AnimRule[] rules)
    {
        foreach (var r in rules)
        {
            r.Source = source;
            r.Order  = order;
            r.Index  = _seq++;
            _rules.Add(r);
        }
        _rules.Sort((a, b) => b.Order != a.Order ? b.Order - a.Order : a.Index - b.Index);
    }

    public void Unregister(string source) => _rules.RemoveAll(r => r.Source == source);

    /// Returns the winning rule, not just its clip, so the caller can honor
    /// MinDwell and report which pack supplied the answer.
    public AnimRule ResolveRule(StringName slot, CharState s, AnimRule active = null)
    {
        List<string> trace = DebugCapture ? new() : null;

        foreach (var r in _rules)
        {
            if (r.Slot != slot) continue;

            if (r.Matches(s, ReferenceEquals(r, active)))
            {
                trace?.Add($"WIN  [{r.Source}] {r.Clip}");
                if (DebugCapture) LastDecision[slot] = string.Join('\n', trace);
                return r;
            }
            trace?.Add($"skip [{r.Source}] {r.Clip}");
        }

        if (DebugCapture)
            LastDecision[slot] = string.Join('\n', trace ?? new List<string>()) + "\n(no rule matched)";
        return null;
    }

    public StringName Resolve(StringName slot, CharState s) => ResolveRule(slot, s)?.Clip;
}

public sealed class AnimRequest
{
    public StringName Slot;
    public StringName Tag;
    public Priority   Priority;
    public float      BlendIn = 0.12f;
    public object     Owner;
    internal int Id;
}

public readonly struct AnimHandle
{
    public readonly int Id;
    public AnimHandle(int id) => Id = id;
    public bool IsValid => Id != 0;
}
