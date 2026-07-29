using System;
using System.Collections.Generic;
using Godot;

namespace CharacterKit.Movement;

public readonly struct Intent
{
    public readonly Vector2 Move;    // -1..1, camera-relative
    public readonly bool    Jump;
    public readonly bool    Sprint;
    public Intent(Vector2 move, bool jump, bool sprint) { Move = move; Jump = jump; Sprint = sprint; }
}

/// A motor owns velocity while it is on top of the stack. It pops itself
/// when done. Adding traversal never means editing walking.
public interface IMotor
{
    bool Finished { get; }
    Vector3 Integrate(Character c, Intent intent, float dt);
    void OnPush(Character c) { }
    void OnPop(Character c) { }

    /// Called when the owning scope ends for any reason, including interrupt
    /// and abort. Must make Finished true. Default is a no-op for motors that
    /// are never scoped, like the base ground motor.
    void Cancel() { }
}

/// Non-exclusive influences (wind, conveyor, slow field) just add velocity.
public interface IVelocityContributor
{
    Vector3 Contribute(Character c, float dt);
    bool Expired { get; }
}

public sealed class MotorStack
{
    private readonly List<IMotor> _stack = new();
    private readonly List<IVelocityContributor> _contributors = new();

    public IMotor Top => _stack[^1];

    public void Push(Character c, IMotor m) { _stack.Add(m); m.OnPush(c); }
    public void Add(IVelocityContributor v) => _contributors.Add(v);

    public Vector3 Integrate(Character c, Intent intent, float dt)
    {
        while (_stack.Count > 1 && Top.Finished)
        {
            Top.OnPop(c);
            _stack.RemoveAt(_stack.Count - 1);
        }

        var v = Top.Integrate(c, intent, dt);

        for (int i = _contributors.Count - 1; i >= 0; i--)
        {
            if (_contributors[i].Expired) { _contributors.RemoveAt(i); continue; }
            v += _contributors[i].Contribute(c, dt);
        }
        return v;
    }
}

public sealed class GroundMotor : IMotor
{
    public bool Finished => false;   // the base motor never pops

    private const float Accel = 60f, Friction = 40f, Gravity = 24f;

    public Vector3 Integrate(Character c, Intent intent, float dt)
    {
        var v = c.Velocity;
        var wish = c.CameraBasis * new Vector3(intent.Move.X, 0, intent.Move.Y);
        var target = wish.Normalized() * (intent.Sprint ? 7.5f : 4.0f) * wish.Length();

        var planar = new Vector3(v.X, 0, v.Z);
        planar = planar.MoveToward(target, (target.LengthSquared() > 0 ? Accel : Friction) * dt);

        float y = c.IsOnFloor() ? (intent.Jump ? 9f : -0.1f) : v.Y - Gravity * dt;
        return new Vector3(planar.X, y, planar.Z);
    }
}

public sealed class DashMotor : IMotor
{
    private readonly Vector3 _dir;
    private readonly float _speed, _duration;
    private float _t;

    public DashMotor(Vector3 dir, float speed, float duration)
    { _dir = dir.Normalized(); _speed = speed; _duration = duration; }

    public bool Finished => _t >= _duration;

    public Vector3 Integrate(Character c, Intent intent, float dt)
    {
        _t += dt;
        float falloff = 1f - Mathf.Pow(_t / _duration, 2f);
        return _dir * _speed * falloff;   // no gravity, no input: the dash owns movement
    }
}

/// Root-motion actions are just a motor that reads the animation delta.
/// Making this a motor keeps the "animation drives the controller" direction
/// explicit instead of letting it happen silently.
public sealed class RootMotionMotor : IMotor
{
    private readonly AnimationTree _tree;
    private readonly float _duration;
    private float _t;

    public RootMotionMotor(AnimationTree tree, float duration) { _tree = tree; _duration = duration; }
    public bool Finished => _t >= _duration;

    public Vector3 Integrate(Character c, Intent intent, float dt)
    {
        _t += dt;
        return c.GlobalTransform.Basis * _tree.GetRootMotionPosition() / dt;
    }
}
