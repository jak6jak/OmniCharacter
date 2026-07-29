using Godot;

namespace CharacterKit.Movement;

/// Pendulum swing on a rope constraint. Pushed onto the motor stack only
/// once the hook has actually attached — everything before that is normal
/// movement with a throw animation on the upper body.
///
/// Invariant that matters more than the physics: Finished MUST become true
/// on every path. A motor that can hang is a soft-lock, and in a stack-based
/// controller it takes the whole character with it.
public sealed class GrappleMotor : IMotor
{
    private const float Gravity        = 24f;
    private const float PumpAccel      = 22f;   // player steering into the swing
    private const float ReelSpeed      = 12f;
    private const float MinRope        = 2.0f;
    private const float Drag           = 0.35f;
    private const float PositionFix    = 8f;    // Baumgarte-style constraint pull
    private const float MaxDuration    = 20f;   // hard ceiling; nothing swings forever
    private const float ReleaseBoost   = 2.5f;

    private readonly Node3D _anchorNode;   // null for static world geometry
    private readonly Vector3 _anchorLocal;
    private readonly Vector3 _anchorWorld;

    private float _ropeLength;
    private float _t;
    private bool  _released;
    private bool  _broken;

    /// Published for the animation pack. The swing pose is a blendspace on
    /// rope direction — a continuous quantity, so it is a blend parameter,
    /// not a set of competing rules.
    public float RopePitch  { get; private set; }
    public float RopeYaw    { get; private set; }
    public float SwingSpeed { get; private set; }

    /// True when the rope ended for a reason other than the player letting go.
    /// The ability uses this to pick recovery vs. a clean dismount.
    public bool BrokeUnexpectedly => _broken;

    public GrappleMotor(Vector3 anchorWorld, Node3D anchorNode, float ropeLength)
    {
        _anchorWorld = anchorWorld;
        _anchorNode  = anchorNode;
        _anchorLocal = anchorNode is not null
            ? anchorNode.GlobalTransform.AffineInverse() * anchorWorld
            : Vector3.Zero;
        _ropeLength  = ropeLength;
    }

    public bool Finished => _released || _broken || _t >= MaxDuration;

    public Vector3 Anchor =>
        _anchorNode is not null && GodotObject.IsInstanceValid(_anchorNode)
            ? _anchorNode.GlobalTransform * _anchorLocal
            : _anchorWorld;

    /// Player let go. Clean exit — momentum carries.
    public void Release() => _released = true;

    /// Scope teardown: interrupt, abort, or the owner leaving the tree.
    public void Cancel() => _released = true;

    public void SetLength(float length) => _ropeLength = Mathf.Max(MinRope, length);

    public Vector3 Integrate(Character c, Intent intent, float dt)
    {
        _t += dt;
        var v = c.Velocity;

        // Anchor validity. A destructible ledge, a despawned platform, or a
        // freed node all land here. Break the rope, don't dereference.
        if (_anchorNode is not null && !GodotObject.IsInstanceValid(_anchorNode))
        {
            _broken = true;
            return v;
        }

        var toAnchor = Anchor - c.GlobalPosition;
        float dist = toAnchor.Length();

        if (dist < 0.01f || float.IsNaN(dist))
        {
            _broken = true;
            return v;
        }

        var dir = toAnchor / dist;

        // Gravity drives the pendulum.
        v.Y -= Gravity * dt;

        // Steering: acceleration projected onto the plane perpendicular to the
        // rope, so input can pump the swing but can never stretch it.
        var wish = c.CameraBasis * new Vector3(intent.Move.X, 0, intent.Move.Y);
        var tangent = wish - dir * wish.Dot(dir);
        v += tangent * PumpAccel * dt;

        // Reel in.
        if (intent.Jump)
            _ropeLength = Mathf.Max(MinRope, _ropeLength - ReelSpeed * dt);

        // Rope constraint: kill outward radial velocity, then correct residual
        // stretch. Only acts when taut, so slack rope is free-fall.
        if (dist > _ropeLength)
        {
            float outward = v.Dot(-dir);
            if (outward > 0f) v += dir * outward;

            v += dir * ((dist - _ropeLength) * PositionFix);
        }

        v *= 1f - Mathf.Min(Drag * dt, 0.5f);

        // Reeled all the way into the anchor — end rather than orbit a point.
        if (dist <= MinRope * 0.5f) _broken = true;

        // Landed. Clean exit, not a break.
        if (c.IsOnFloor() && v.Y <= 0f) _released = true;

        var local = c.GlobalTransform.Basis.Inverse() * dir;
        RopePitch  = Mathf.Asin(Mathf.Clamp(local.Y, -1f, 1f));
        RopeYaw    = Mathf.Atan2(local.X, -local.Z);
        SwingSpeed = v.Length();

        return v;
    }

    public void OnPop(Character c)
    {
        // Small upward kick on a deliberate release near the bottom of the arc,
        // where the velocity is mostly horizontal. Nothing on a broken rope —
        // a snapped anchor should feel like losing the swing, not gaining a jump.
        if (_released && !_broken && c.Velocity.Y > -1f)
            c.Velocity += Vector3.Up * ReleaseBoost;
    }
}
