using Godot;

namespace CharacterKit.Animation;

// =====================================================================
// The bit between "which rule won" and "what the player is doing".
//
// This was previously inlined in the playground, and it was written
// EDGE-TRIGGERED: it called Play() when the winning rule changed, and never
// again. Which is fine right up until a clip reaches its end. Then the
// player stops, the rule is still winning so nothing re-issues it, and the
// character stands frozen in the last pose of a walk cycle. Wiggling an
// input that resolves to the same rule changes nothing, so it reads as
// "stuck on that animation even when changing various settings".
//
// The fix is to stop treating resolution as an event. The table publishes a
// standing answer — this clip should be playing — and the driver reconciles
// against it every frame. Edge-triggered logic against a level-triggered
// source is the actual defect; the frozen character is a symptom.
//
// A one-shot is not a hole in that model, it is the other half of it:
// `Loops = false` means the clip is finished and holding is correct. In the
// real system the ability's phase clears its tag and the rule stops winning.
// A GUI holding a tag forever has no phase to end it, which is why the
// playground can sit on a completed punch — and why that is the right
// behavior rather than a punch that machine-guns.
//
// Extracted so the playground and the transition tests drive the same code.
// A copy of this logic in a test file would test the copy.
// =====================================================================
public sealed class ClipDriver
{
    /// The rule currently owning the slot.
    public AnimRule Active { get; private set; }

    /// Seconds left before MinDwell will allow another switch.
    public float DwellRemaining { get; private set; }

    /// Seconds since the clip was last issued to the player.
    public float TimeInClip { get; private set; }

    /// Set by Tick when the caller should issue Active.Clip to the player.
    public bool ShouldPlay { get; private set; }

    /// True when ShouldPlay is a re-issue of the clip already selected —
    /// the reconciliation path rather than a transition. Worth surfacing:
    /// a cycle restarting every frame means something else is wrong.
    public bool IsRestart { get; private set; }

    /// True while the active rule is a one-shot whose clip has run out.
    ///
    /// Somebody has to end a one-shot, and it is not the table: the rule keeps
    /// winning for as long as its tag is set, so a punch that has finished
    /// holds its last pose while the character is nominally jogging. In the
    /// real system the ability's phase clears its tag — but nothing told the
    /// phase the clip was over, so `WhiffPhase` and `RopeBreakPhase` count
    /// their own timers and duplicate the clip length as a magic number.
    ///
    /// This is that missing signal. Level-triggered, not an edge, so a reader
    /// that misses a frame still sees it.
    public bool Completed { get; private set; }

    /// Reason for the last decision, for debug readouts.
    public string Reason { get; private set; } = "cold";

    /// <param name="playing">
    /// Whether the player is currently running the clip this driver last
    /// asked for. This is the level signal the edge-triggered version never
    /// looked at.
    /// </param>
    public void Tick(RuleTable rules, StringName slot, CharState state, float dt, bool playing)
    {
        ShouldPlay = false;
        IsRestart  = false;
        Completed  = false;

        DwellRemaining = Mathf.Max(0f, DwellRemaining - dt);
        TimeInClip    += dt;

        var next = rules.ResolveRule(slot, state, Active);

        if (!ReferenceEquals(next, Active))
        {
            if (DwellRemaining > 0f)
            {
                Reason = $"held by MinDwell ({DwellRemaining:0.00}s left)";
                return;
            }

            Active         = next;
            DwellRemaining = next?.MinDwell ?? 0f;
            TimeInClip     = 0f;
            ShouldPlay     = next is not null;
            Reason         = next is null ? "resolved to nothing" : "switched";
            return;
        }

        // Same rule still winning. Is its clip actually running?
        if (Active is null)
        {
            Reason = "no rule matches";
            return;
        }

        if (playing)
        {
            Reason = "playing";
            return;
        }

        if (Active.Loops)
        {
            ShouldPlay = true;
            IsRestart  = true;
            TimeInClip = 0f;
            Reason     = "cycle ended, re-issued";
            return;
        }

        Completed = true;
        Reason    = "one-shot finished, holding last pose";
    }

    /// Drop all state. Used when the slot is torn down.
    public void Reset()
    {
        Active = null;
        DwellRemaining = 0f;
        TimeInClip = 0f;
        ShouldPlay = false;
        IsRestart = false;
        Completed = false;
        Reason = "cold";
    }
}
