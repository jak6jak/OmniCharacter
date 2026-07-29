using Godot;
using CharacterKit.Abilities;
using CharacterKit.Animation;
using CharacterKit.Movement;

namespace CharacterKit.Content;

/// Marker for surfaces the hook can bite.
public sealed partial class Grappleable : Node3D { }

/// Input surface and configuration. Everything that used to be lifecycle
/// bookkeeping now lives in the phases below, and the phases declare rather
/// than acquire — so there is no Cleanup() and nothing to leak.
public sealed partial class GrappleAbility : Node
{
    [Export] public Character Owner;
    [Export] public Camera3D Camera;
    [Export] public AbilityRunner Runner;

    [Export] public float MaxRange = 30f;
    [Export] public float HookSpeed = 70f;
    [Export] public float WhiffRecovery = 0.25f;

    internal AimAssist Aim;

    public override void _Ready()
    {
        Aim = new AimAssist
        {
            MaxRange = MaxRange,
            ConeDegrees = 4f,
            // The only gameplay policy in the targeting path: what bites, and
            // whether it needs tracking. Static geometry can't move or die,
            // so it needs no node.
            Accept = collider =>
            {
                for (Node n = collider; n is not null; n = n.GetParent())
                    if (n is Grappleable)
                        return (true, collider is StaticBody3D ? null : collider);
                return (false, null);
            }
        };
    }

    public void OnFirePressed()
    {
        // Runner enforces interruptibility from the phase itself.
        if (Runner.Busy && !Runner.CanInterrupt) return;
        Runner.Begin(new ThrowPhase(this));
    }

    public void OnFireReleased() => Runner.ReleaseHeld();
}

// =====================================================================
// Phase 1 — throw. The animation commits before the outcome is known,
// which is the entire reason the miss needs a branch rather than a guard.
// =====================================================================
internal sealed class ThrowPhase : AbilityPhase
{
    private readonly GrappleAbility _a;
    private float _flight;
    private bool _hit;
    private Vector3 _anchor;
    private Node3D _track;

    public ThrowPhase(GrappleAbility a) => _a = a;

    public override void Declare(Scope s) =>
        s.Tag(GrappleTags.Throw)
         .Anim(Slots.UpperBody, GrappleTags.Throw, Priority.Ability, blendIn: 0.06f);

    public override void Enter()
    {
        _hit = _a.Aim.Find(_a.Camera, Owner.GetRid(), out _anchor, out _track);
        float dist = _hit ? Owner.GlobalPosition.DistanceTo(_anchor) : _a.MaxRange;
        _flight = dist / _a.HookSpeed;
    }

    public override AbilityPhase Advance(float dt)
    {
        _flight -= dt;
        if (_flight > 0f) return null;

        // Re-checked at attach, not at click: the ledge may have been destroyed
        // while the hook was in the air.
        bool stillThere = _hit && (_track is null || GodotObject.IsInstanceValid(_track));
        return stillThere
            ? new SwingPhase(_a, _anchor, _track)
            : new WhiffPhase(_a);
    }
}

// =====================================================================
// Phase 2 — swing.
// =====================================================================
internal sealed class SwingPhase : AbilityPhase, IHoldablePhase
{
    private readonly GrappleAbility _a;
    private readonly GrappleMotor _motor;

    public SwingPhase(GrappleAbility a, Vector3 anchor, Node3D track)
    {
        _a = a;
        _motor = new GrappleMotor(anchor, track, 0f);
    }

    public override void Enter() => _motor.SetLength(Owner.GlobalPosition.DistanceTo(_motor.Anchor));

    public override void Declare(Scope s) =>
        s.Tag(GrappleTags.Swing)
         .Anim(Slots.FullBody, GrappleTags.Swing, Priority.Ability, blendIn: 0.1f)
         .Motor(_motor)
         .Scalar("grapple.rope_yaw",   () => _motor.RopeYaw)
         .Scalar("grapple.rope_pitch", () => _motor.RopePitch)
         .Scalar("grapple.speed",      () => _motor.SwingSpeed);

    public override AbilityPhase Advance(float dt)
    {
        if (!_motor.Finished) return null;
        return _motor.BrokeUnexpectedly ? new RopeBreakPhase(_a) : Done;
    }

    public void Release() => _motor.Release();
}

// =====================================================================
// Phase 3a — miss. No motor: the player never left normal movement.
// =====================================================================
internal sealed class WhiffPhase : AbilityPhase
{
    private readonly GrappleAbility _a;
    private float _t;

    public WhiffPhase(GrappleAbility a) { _a = a; _t = a.WhiffRecovery; }

    public override bool Interruptible => true;

    public override void Declare(Scope s) =>
        s.Tag(GrappleTags.Whiff)
         .Anim(Slots.UpperBody, GrappleTags.Whiff, Priority.Ability, blendIn: 0.08f);

    public override AbilityPhase Advance(float dt) => (_t -= dt) <= 0f ? Done : null;
}

// =====================================================================
// Phase 3b — anchor died mid-swing. Reads differently from letting go.
// =====================================================================
internal sealed class RopeBreakPhase : AbilityPhase
{
    private float _t = 0.35f;

    public RopeBreakPhase(GrappleAbility a) { }

    public override bool Interruptible => true;

    public override void Declare(Scope s) =>
        s.Tag(GrappleTags.RopeBreak)
         .Anim(Slots.FullBody, GrappleTags.RopeBreak, Priority.Reaction, blendIn: 0.05f);

    public override AbilityPhase Advance(float dt) => (_t -= dt) <= 0f ? Done : null;
}
