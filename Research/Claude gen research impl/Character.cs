using Godot;
using CharacterKit.Animation;
using CharacterKit.Movement;

namespace CharacterKit;

/// The whole point of the architecture: this file is written once and does
/// not grow when you add weapons, abilities, status effects, or traversal.
public sealed partial class Character : CharacterBody3D
{
    [Export] public AnimDirector Anim;
    [Export] public Node3D CameraRig;

    public readonly CharState  State  = new();
    public readonly MotorStack Motors = new();

    private readonly TrajectoryPredictor _trajectory = new();

    public Basis CameraBasis => CameraRig.GlobalTransform.Basis;
    public Vector3 Facing => -GlobalTransform.Basis.Z;

    public override void _Ready()
    {
        Motors.Push(this, new GroundMotor());

        // Content is installed, not hardcoded. Order matters: higher shadows lower.
        // Install returns issues rather than throwing. Dependency failures and
        // unserved claims surface here instead of as a wrong clip in play.
        foreach (var pack in new[] { Content.BaseLocomotion.Pack(), Content.InjuryPack.Pack() })
            foreach (var issue in Anim.Install(pack).Issues)
                GD.Print($"[anim] {issue}");
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // 1. Read intent.
        var intent = new Intent(
            Input.GetVector("left", "right", "forward", "back"),
            Input.IsActionJustPressed("jump"),
            Input.IsActionPressed("sprint"));

        // 2. Motors own velocity.
        Velocity = Motors.Integrate(this, intent, dt);
        MoveAndSlide();

        // 3. Publish facts. This is the only contract the animation system has.
        bool grounded = IsOnFloor();
        State.Grounded = grounded;
        State.Tags.Set(T.Airborne, !grounded);
        State.LocalVelocity = GlobalTransform.Basis.Inverse() * Velocity;
        _trajectory.Predict(this, intent, dt, State);

        // The ground/air timers used to be accumulated here against a
        // CharState field that no longer exists. Publish() below owns them now
        // — it advances TimeGrounded and TimeAirborne from Grounded and dt.

        State.Publish(dt);

        // 4. Animation resolves itself. Nothing above named a single clip.
        Anim.Evaluate(State, delta);
    }
}

/// Spring-damped prediction of where the character will be ~1s from now,
/// in character space. Blendspaces read this instead of raw stick input —
/// it is the one idea worth stealing from motion matching.
public sealed class TrajectoryPredictor
{
    private Vector3 _predicted;
    private readonly Vector3[] _history = new Vector3[3];
    private float _lastYaw;

    private static readonly float[] Horizons = { 0.2f, 0.4f, 1.0f };

    public void Predict(Character c, Intent intent, float dt, CharState state)
    {
        var wish = c.CameraBasis * new Vector3(intent.Move.X, 0, intent.Move.Y);
        _predicted = _predicted.Lerp(wish * 7.5f, 1f - Mathf.Exp(-8f * dt));

        float yaw = c.GlobalRotation.Y;
        state.TurnRate = Mathf.Wrap(yaw - _lastYaw, -Mathf.Pi, Mathf.Pi) / Mathf.Max(dt, 1e-4f);
        _lastYaw = yaw;

        var inv = c.GlobalTransform.Basis.Inverse();
        var localPredicted = inv * _predicted;
        var localVel = inv * c.Velocity;

        for (int i = 0; i < Horizons.Length; i++)
        {
            state.Future[i] = new TrajectorySample { Position = localPredicted * Horizons[i] };
            _history[i] = _history[i].Lerp(localVel * -Horizons[i], 1f - Mathf.Exp(-6f * dt));
            state.Past[i] = new TrajectorySample { Position = _history[i] };
        }
    }
}
