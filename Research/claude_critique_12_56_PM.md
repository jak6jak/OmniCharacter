Let me be straight with you, including about my own contributions here.

## It isn't novel, and that's the good news

What we built is close to a re-derivation of Unreal Lyra's architecture. Lyra uses GameplayTags for composable state, Linked Anim Layers so an equipped weapon supplies layer implementations the character requests by name, and slot-based overrides for one-shot actions. Swap "pack" for "linked anim layer" and "rule table" for "chooser" and the shapes line up. Unreal shipped the Chooser plugin in 5.3 as literally a data table of predicates mapping state to animation assets.

Unity's Animancer makes the same bet from the other direction — its whole pitch is that the Animator Controller graph doesn't scale and you should drive clips from code with named states.

Three independent teams converging on this is decent evidence it's sound. It also means you should be stealing from them rather than deriving from scratch — particularly Lyra's layer-interface pattern, which solves the pack/ability contract problem more cleanly than the `Serves`/`Claim` validation we bolted on.

## The structural weakness I've been underselling

**Our design has no concept of an edge.**

In an HFSM, `idle → walk` is a first-class object. You attach things to it: a specific transition clip, a duration, an interruption policy, a curve. Our rule table only ever answers "what clip now" — it has states and no transitions. So transition-specific content has nowhere to live. A start-up step before the walk loop, a plant-and-turn on a 180, a weapon-swap flourish: to express any of those you'd add a tag whose only purpose is to mark "we are transitioning," plus a timer to clear it. That is an edge, reimplemented badly and non-locally.

This is not a gap I can patch with another framework feature. It's a consequence of the model being stateless by design, which is the same property that makes it extensible.

## Head to head

| | HFSM | What we built |
|---|---|---|
| Combinatorial states | Explodes: armed × crouched × injured × swinging | Composes via tags |
| Transitions | First-class, authorable | Absent |
| Runtime extension | Edit the graph asset | Install a pack |
| Failure locality | Scoped to a source state | Global — a misordered row changes unrelated states |
| Testability | Very hard to unit-test | Sweep test, milliseconds, no scene |
| Tooling | Live graph debugger, weights, timings | `GD.Print` |
| Legibility to non-programmers | Good | Poor |

The tooling row is worth more than it looks. Unreal's AnimGraph debugger showing live blend weights per node is genuinely transformative for diagnosing "why does it look wrong," and we have nothing close.

The failure-locality row is the one that will actually hurt you. First-match-wins over a global ordered list means inserting a rule in the wrong position silently changes behavior for states you weren't thinking about. HFSM transitions are scoped to their source state, so a mistake stays local. That's a real regression in maintainability that the sweep test partially — not fully — covers.

## The synthesis that actually matters

These aren't competitors at the same level. Our rule table replaces the *selection* half of an HFSM and not the *transition* half. Lyra uses both: tag-driven layer selection at the top, state machines inside layers where start/stop/pivot semantics matter.

That's where I'd take this. Keep the pack/tag/slot layer. Then let a slot resolve to a small state machine rather than a clip, and put locomotion in one — idle/start/loop/stop/pivot has real edge semantics and wants an FSM. The rule table picks *which* machine (unarmed vs rifle), the machine handles the transitions. You lose nothing and recover the thing you're currently missing.

## The Godot-specific calculus

This is more justified in Godot than it would be in Unreal, because there's less to give up. `AnimationNodeStateMachine` is editor-authored, doesn't support runtime graph construction well, has no layer-interface equivalent, and no inertialization. In Unreal I'd tell you to stop and use Lyra's pattern. In Godot the built-in genuinely doesn't do layered, runtime-extensible content, so building it is defensible.

## An honest pattern I noticed

Three of the last four messages were the framework fixing itself. The scalar bag existed because grapple needed it. `MinDwell` sat dangling for two messages. `Uninstall` leaked bindings because I added bindings and forgot the other side. None of that produced a single frame of gameplay.

That's the failure mode to watch, and it's more dangerous than any architectural flaw here. You have roughly 1,100 lines of framework and zero shipped movement. Meanwhile the two things that determine whether any of this *looks* good — inertialization and bone masking — are both still stubs with TODO comments.

## Verdict

Worth continuing, with three conditions:

**Stop adding framework.** The next thing you write should be inertialization, then bone masks. Architecture with a crossfade in `SwitchClip` will look worse than a naive Animator Controller with good blending.

**Add FSMs inside slots** rather than treating them as the enemy. That's the real answer to the transition gap, and it's what the mature systems do.

**Set falsification triggers.** If rule count per slot passes ~15, ordering fragility will bite and you need scoping. If you find yourself adding tags that exist only to trigger a transition clip, you need edges now. If you ever need networked prediction, stop entirely — this design has no answer and GAS-equivalent work is enormous.

The thing I'd genuinely defend as better than the alternatives is the sweep test. Being able to enumerate your state space and assert on every resolution, in milliseconds, outside a scene tree, has no equivalent in any graph-based system I know of. If you keep one idea from all of this, keep that one.
