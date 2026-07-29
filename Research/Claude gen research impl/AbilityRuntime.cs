using System;
using System.Collections.Generic;
using Godot;
using CharacterKit.Animation;
using CharacterKit.Movement;

namespace CharacterKit.Abilities;

/// Everything a phase holds while it is active. Declared once on entry,
/// released automatically on exit — on completion, on interrupt, on abort,
/// on the node leaving the tree. There is no path that skips it.
///
/// This is the whole point of the refactor: resource pairing stops being
/// something a feature author has to get right.
public sealed class Scope : IDisposable
{
    private readonly Character _c;
    private readonly List<StringName> _tags = new();
    private readonly List<AnimHandle> _handles = new();
    private readonly List<(StringName Key, Func<float> Sample)> _scalars = new();
    private IMotor _motor;
    private bool _disposed;
    private readonly bool _dry;

    /// (slot, tag) pairs this scope claims. Recorded in both modes so a dry
    /// run can be checked against the installed packs.
    public readonly List<(StringName Slot, StringName Tag)> Claims = new();

    internal Scope(Character c, bool dry = false) { _c = c; _dry = dry; }

    /// Records what a phase would take without taking it. Lets validation
    /// instantiate a phase chain outside a scene tree.
    public static Scope DryRun(AbilityPhase phase)
    {
        var s = new Scope(null, dry: true);
        phase.Declare(s);
        return s;
    }

    public Scope Tag(StringName t)
    {
        _tags.Add(t);
        if (!_dry) _c.State.Tags.Add(t);
        return this;
    }

    public Scope Anim(StringName slot, StringName tag, Priority priority, float blendIn = 0.1f)
    {
        Claims.Add((slot, tag));
        if (!_dry) _handles.Add(_c.Anim.Request(slot, tag, priority, blendIn, owner: this));
        return this;
    }

    /// Sampled every frame while the phase is active, cleared when it ends.
    /// Replaces hand-written publish/clear pairs.
    public Scope Scalar(StringName key, Func<float> source)
    {
        _scalars.Add((key, source));
        return this;
    }

    public Scope Motor(IMotor m)
    {
        if (_dry) return this;
        _c.Motors.Push(_c, m);
        _motor = m;
        return this;
    }

    internal void Sample()
    {
        foreach (var (key, source) in _scalars)
            _c.State.SetScalar(key, source());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // A dry-run scope has no character to release against. It recorded
        // claims and took nothing, so there is nothing to unwind.
        if (_dry) return;

        foreach (var h in _handles) _c.Anim.Release(h);
        foreach (var t in _tags) _c.State.Tags.Remove(t);
        foreach (var (key, _) in _scalars) _c.State.ClearScalar(key);
        _motor?.Cancel();
    }
}

/// Implemented by phases that end early when a held input is released.
public interface IHoldablePhase
{
    void Release();
}

/// One step of an ability. Declare what it holds, advance it, branch out of it.
public abstract class AbilityPhase
{
    /// Sentinel returned from Advance to end the ability cleanly.
    public static readonly AbilityPhase Done = new DonePhase();

    protected Character Owner { get; private set; }

    /// Can a new input cut this phase short? Recovery phases should say yes —
    /// a miss that locks the player out reads as a dropped input.
    public virtual bool Interruptible => false;

    public virtual void Declare(Scope scope) { }
    public virtual void Enter() { }

    /// Return null to stay, another phase to transition, Done to finish.
    public virtual AbilityPhase Advance(float dt) => null;

    internal void Bind(Character c) => Owner = c;

    private sealed class DonePhase : AbilityPhase { }
}

/// Drives one ability's phase chain. Owns the scope lifetime so no feature
/// has to. Attach one per ability, or share one to make abilities mutually
/// exclusive by construction.
public sealed partial class AbilityRunner : Node
{
    [Export] public Character Owner;

    private AbilityPhase _phase;
    private Scope _scope;

    public bool Busy => _phase is not null;
    public bool CanInterrupt => _phase is null || _phase.Interruptible;

    public void Begin(AbilityPhase first)
    {
        if (!CanInterrupt) return;
        Abort();
        Transition(first);
    }

    /// Forwards a held-input release to the current phase if it cares.
    /// Phases opt in by implementing IHoldablePhase.
    public void ReleaseHeld() => (_phase as IHoldablePhase)?.Release();

    public void Abort()
    {
        _scope?.Dispose();
        _scope = null;
        _phase = null;
    }

    private void Transition(AbilityPhase next)
    {
        _scope?.Dispose();
        _scope = null;
        _phase = null;

        if (next is null || ReferenceEquals(next, AbilityPhase.Done)) return;

        _phase = next;
        _phase.Bind(Owner);

        _scope = new Scope(Owner);
        _phase.Declare(_scope);
        _phase.Enter();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_phase is null) return;

        _scope.Sample();

        var next = _phase.Advance((float)delta);
        if (next is not null) Transition(next);
    }

    public override void _ExitTree() => Abort();
}
