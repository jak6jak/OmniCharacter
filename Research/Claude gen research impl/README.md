# CharacterKit — worked example

A skeleton of the state → resolve → compose architecture, in Godot 4 / C#.
This is illustrative, not a library: the pose blending is stubbed against
`AnimationTree` parameters and inertialization is left as a marked TODO.

## Files

| File | Role |
|---|---|
| `Animation/AnimCore.cs` | Tags, `CharState`, `AnimRule`, `RuleTable`, requests |
| `Animation/AnimDirector.cs` | Pack install, slot arbitration, per-frame evaluate |
| `Animation/RuleOverlap.cs` | Decidable rule regions, shadow detection, the `Shadows` gate |
| `Animation/SweepDomain.cs` | Sweep coverage derived from the rule table |
| `Animation/ClipDriver.cs` | Reconciles "which clip should play" against "which clip is playing" |
| `Movement/Motors.cs` | Motor stack, ground/dash/root-motion motors |
| `Character.cs` | The per-frame loop + trajectory predictor |
| `Content/Packs.cs` | **The actual usage example** — start here |

## The flow, once per physics frame

1. `Character` reads input into an `Intent`.
2. `MotorStack` integrates velocity. The top motor owns movement; contributors add on top.
3. `Character` publishes facts into `CharState` — tags, local velocity, predicted trajectory, turn rate.
4. `AnimDirector.Evaluate` picks the winning request per slot, pushes its tag into state, resolves each layer through the rule table, and drives the blend parameters.

Steps 1, 2 and 4 are written once. Step 3 is the only contract.

## Adding a new weapon

Everything below is additive — no existing file is edited:

1. Author clips into an `AnimationLibrary` resource.
2. Write an `AnimPack` with `Order` above base and rules requiring your weapon tag.
3. Call `Anim.Install(pack)` and add the tag when equipped.

Because rules are shadowed rather than replaced, unequipping restores base
behavior for free, and a pack that only covers *some* states falls through to
base for the rest. A pistol pack with nothing but an idle and a reload still works.

## Adding a new ability

1. Write an `IMotor` if it owns movement (dash, grapple, ledge climb), or an
   `IVelocityContributor` if it just influences it (wind, slow field).
2. Post an `AnimRequest` naming a *tag* at `Priority.Ability`.
3. Add a rule mapping that tag to a clip.

## Deliberate gaps

- **Inertialization.** `SwitchClip` does a crossfade. Replace it — snapshot the
  pose delta at the transition and decay it to zero over `BlendIn`. Biggest
  single quality win available.
- **Bone masks.** `LayerDef.BoneMask` is carried through but not applied; wire it
  to `AnimationNodeBlend2` filters or a manual `Skeleton3D` write.
- **Stride and orientation warping.** Not here. Add after inertialization.
- **`Rules.DebugCapture`** is off by default. Turn it on and print
  `Anim.Explain(Slots.Base)` in a debug panel — you will need this by the third pack.

## Before you build this

Hardcode two or three abilities completely first. The slot names, the tag
vocabulary, and the priority bands in `AnimCore.cs` are the parts most likely to
be wrong if invented up front, and they are the expensive parts to change later.

## Worked extension: physics grapple swing

Added in `Movement/GrappleMotor.cs`, `Content/GrapplePack.cs`. Files that
did **not** change: `Character.cs`, `Movement/Motors.cs`, `Content/Packs.cs`,
`Animation/RuleValidation.cs`.

One core change was needed, and it was a real gap rather than a workaround:
`CharState` had a fixed field list, so a pack could not publish its own
continuous parameter. It now carries a namespaced scalar bag, and `AnimPack`
carries `ScalarBinding[]` routing those scalars to tree parameters. The grapple
uses it for rope yaw/pitch driving the swing blendspace. Any future pack that
needs a vault height or a vehicle lean now has somewhere to put it.

### Miss handling

| Case | Result |
|---|---|
| Ray hits open sky | Throw plays to max range, then whiff |
| Ray hits non-grappleable surface | Throw plays to the hit point, then whiff |
| Ray misses by a few degrees | Cone sweep snaps to the nearest valid anchor |
| Anchor destroyed during hook flight | Re-checked at attach time, falls through to whiff |
| Anchor destroyed mid-swing | Motor breaks, `rope_break` at `Priority.Reaction` |
| Reeled fully into the anchor | Motor breaks rather than orbiting a point |
| Ability freed mid-swing | `_ExitTree` runs the same `Cleanup()` |

Three properties worth preserving if you rework this:

1. **No motor is pushed on a miss.** The player never stops moving normally —
   a whiff costs an animation, not control.
2. **Whiff recovery is interruptible.** `OnFirePressed` rejects only during
   throw and swing. A miss that locks you out reads as a dropped input.
3. **`Cleanup()` is idempotent and reachable from every exit.** In a system
   where slots are claimed by handle, a leaked handle is a permanently stuck
   animation, and it will not look like an ability bug when you find it.

## Refactor: scoped ability phases

The first grapple implementation was ~290 lines, of which roughly two thirds
were a hand-rolled phase machine plus manual acquire/release pairing for tags,
animation handles, scalars, and the motor — none of it grapple-specific.

`Abilities/AbilityRuntime.cs` moves that into the framework:

- A **phase** declares what it holds. It never acquires or releases.
- A **scope** acquires on entry and releases on every exit — completion,
  branch, interrupt, abort, `_ExitTree`. There is no path that skips it.
- **Interruptibility** is a property of the phase, not a check in the caller.
- **Scalars** are sampled from a lambda while the phase lives, then cleared.

`Cleanup()` is gone. Not "shorter" — gone, because there is nothing a feature
author can forget to release.

`Abilities/AimAssist.cs` extracts forgiving targeting, since every aimed
ability wants it. The *validity policy* stays a caller-supplied predicate —
what counts as a grappleable surface is gameplay, not framework.

### Where the line is

| Belongs to the framework | Stays in the ability |
|---|---|
| Resource lifetime and pairing | Rope physics |
| Phase transitions and interrupts | What counts as an anchor |
| Slot arbitration | Flight timing, recovery windows |
| Scalar sampling | Which phases exist |

The rule: **the framework owns what is dangerous to get wrong; gameplay owns
what is interesting to get right.** Pushing the rope math or the anchor policy
into the framework would make the next ability harder, not easier.

### Cost

Trivial abilities pay ceremony — dash becomes one phase class instead of two
methods. Stack traces get an indirection. Accept both; the alternative is
every future ability re-deriving `Cleanup()` and one of them getting it wrong.

## Pack interface, current shape

```csharp
public sealed class AnimPack
{
    public string   Id;
    public int      Order;      // higher shadows lower
    public string[] Requires;   // pack ids that must already be installed
    public string[] Shadows;    // clips from other packs it may displace
    public AnimationLibrary Library;
    public LayerDef[]       Layers;
    public AnimRule[]       Rules;
    public ScalarBinding[]  Bindings;
    public Claim[]          Serves;     // (slot, tag) pairs it promises to answer
}
```

Changes from the first version, and why:

| Change | Reason |
|---|---|
| `Requires` | A pack that shadows base rules is broken if base isn't there. Checked at install rather than assumed. |
| `Serves` | The other half of the ability contract. Abilities claim; packs promise; validation compares. |
| `Shadows` | Precedence, made explicit. A rule that outranks a clip not on this list fails the install and names the region it captured. See below. |
| `Install` returns `InstallResult` | Dependency and contract failures surface at install, not as a wrong clip during play. |
| `Uninstall` is symmetric | **Bug fix.** It previously dropped only rules, so bindings accumulated across every equip cycle and layers outlived their pack. |
| `Condition[]` replaces lambdas | Plain data, so a pack can become an editor Resource. `When` survives as an escape hatch and gets flagged. |
| `Hysteresis` on conditions | Threshold widens while the rule is already winning. This is where it belonged all along. |
| `MinDwell` now honored | It was a declared field the director ignored. `ResolveRule` returns the rule so the layer can hold it. |

## Failure locality

The real complaint about first-match-wins over an ordered list: inserting a
rule in the wrong position silently changes behavior for states you weren't
thinking about. An HFSM scopes transitions to a source state, so a mistake
stays local. That is a genuine maintainability regression, and the golden
sweep only partially covered it.

Two things were being conflated. Cross-pack ordering was never positional —
`Order` is a number the author writes down, and `Register` sorts on it. What
*was* silent is which states a given `Order` actually captures.

### Overlap is decidable

`Condition` became plain data so packs could become Resources. The unclaimed
payoff is that rule overlap is now exactly decidable:

- Tags are a finite Boolean lattice. Two `TagQuery`s can both match iff
  neither's `Require` set is hit by the other's `Exclude` set.
- Conditions are interval constraints over a closed `Field` enum. Collapse
  each rule to one interval per field; both match iff every field's pair
  overlaps. Hysteresis is folded in at its widened edge, because a shadow
  that only happens while sticky is still a shadow.

So `Animation/RuleOverlap.cs` computes not just *whether* one rule shadows
another but *over which region*, and whether the shadow is total (the lower
rule is dead) or partial. That is precisely the information that went missing
at insertion time.

### `Shadows`, and why it names clips

`Requires` makes dependency explicit. `Serves` makes the ability contract
explicit. `Shadows` is the third member of the same family — each names a
relationship a pack really has to the rest of the system but that is invisible
in its own code, and each is checked at install against what is actually
there. Precedence was the one left undeclared.

It names **clips**, not packs. Pack granularity was tried first and does not
work: the rifle legitimately displaces base's locomotion, so `"base"` is a
legitimate entry — and that one word then also waves through the accidental
capture of `base/fall`, which is the exact bug the check exists to find.

That bug was real and sat in `Content/Packs.cs` undetected. `rifle/loco_space`
required `weapon.rifle` and nothing else, sat above base at `Order 100`, and
`PlanarSpeed` is horizontal — so it stayed above threshold through a whole
fall arc. A rifle-carrying character running off a ledge played a run cycle
all the way down. The check reports it as:

```
[ERROR] pack 'rifle': 'rifle/loco_space' shadows 'base/fall' from 'base'
        over [+loco.airborne +weapon.rifle, PlanarSpeed in (0.05, inf),
        VerticalSpeed in (-inf, 0)] — add "base/fall" to Shadows if that is
        intended, or narrow the rule so it stops capturing those states
```

The fix is `.Without(T.Airborne, T.Crouched)` on the rule's `TagQuery`.

### Self-scoping, or the declaration burden eats the idea

The grapple pack sits at `Order 150` and outranks every weapon pack's
upper-body rules. Taken naively it would have to declare
`Shadows = ["rifle/aim_idle", "pistol/aim_idle", "bow/draw", ...]` — every
weapon clip that will ever ship, listed by a pack with no business knowing any
of them exist.

But each grapple rule is gated behind a tag in grapple's own `Serves`. A rule
that only fires in states the pack was asked to own cannot surprise anyone, so
the check exempts it. Grapple declares nothing and raises nothing. The rifle's
Base rule gets no exemption — `weapon.rifle` is a tag it *reacts to*, not one
it *claims*.

This is the same locality an HFSM gets from a source state, expressed through
a declaration the design already had.

### The sweep now derives its own domain

`Sweep` took its tag combos from the caller and defaulted its speeds to
`{ 0, 0.5, 2.0, 4.4, 4.6, 7.5 }` — numbers straddling thresholds somebody
remembered. That is *why* it only partially covered the hazard.

`Animation/SweepDomain.cs` derives both axes from the table: every tag any
rule names, and points either side of every threshold any rule declares
including its hysteresis-widened edge. On the sample packs that is 96 tag
combos against the 11 in the old hand-written list — 86 of them absent — and
`PlanarSpeed` sampled at `{0, 0.049, 0.051, 0.199, 0.201, 5.499, 5.501,
5.999, 6.001, 7.5}` rather than at six remembered constants. Complete with
respect to the installed rule set, by construction, and it re-derives itself
when a pack adds a threshold.

Tags are grouped by namespace and assumed mutually exclusive within one — a
character holds one weapon, is in one stance. `IndependentNamespaces` is the
opt-out (`status.*`, since injured and burning co-occur). Getting that list
wrong is the one way the sweep silently under-covers, which is why it is
explicit rather than inferred.

### And it runs at install

The golden always carried `clip@origin`, so a diff could always say which pack
supplied the new answer. What was missing was timing — it only ran when
somebody ran the test file. `Install` now sweeps the derived domain before and
after, and reports what moved: `ERROR` if a state changed and the new winner
is some *other* pack, `INFO` listing what this pack took over. That recovers
in time what an HFSM gets in structure — the mistake reports at the insertion.

A pack failing validation is now also rolled back. `Ok == false` used to mean
"nothing changed except the rules, layers, bindings and library", and
`EquipmentSystem.Equip` bails on `!Ok` without recording the pack id — so a
rejected pack stayed installed with nothing tracking it.

### How the comparison row should read

> **Failure locality** — HFSM: local by default; global wherever any-state
> transitions are used, and unenforced there. Rule table: non-local by
> default; made local by decidable overlap analysis, a declared `Shadows`
> contract failing at install, and a golden sweep whose domain is derived from
> the rules themselves.

### Proving the check still works

Every other check in the suite is exercised by the shipping content. This one
is not: once the rifle's Base rule was narrowed, every remaining shadow became
self-scoped, so `CheckDeclarations` correctly reports nothing on clean content.

**A check that reports nothing looks exactly like a check that has silently
become a no-op.** That is the failure mode this whole design refuses
everywhere else — `Serves` exists because a missing rule was silent, `Scope`
exists because a forgotten release was silent — and it applies to validators
too.

`Tests/ShadowFixtures.cs` is nine deliberately-broken packs with exact expected
verdicts, asserted against `RuleOverlap.Inspect` — the structured form, so the
fixture never string-matches prose. Detection and presentation are separated in
`RuleOverlap` specifically so this is possible; a validator whose only output is
a formatted message can only be tested by matching text, which nobody
maintains, so it goes untested.

The load-bearing case is **`SelfScopingIsNotAWildcard`**. The exemption is the
part most likely to be wrong, and its wrong direction is *too generous* — which
would switch the entire check off without changing a single reported issue
anywhere in the project. Two fixtures pin it: a `Serves` claim on an unrelated
tag must not exempt, and a `Serves` claim on a different slot must not exempt.

The fixtures were themselves verified by mutation — breaking the checker three
ways and confirming the suite goes red:

| Mutation | Caught by |
|---|---|
| `IsSelfScoped` returns true whenever a pack has any `Serves` | both self-scoping fixtures |
| `Shadows` matching collapses back to pack granularity | the declared-clean and clip-level fixtures |
| `Region.Intersects` ignores `Exclude` | the narrowing fixtures |

### Cost

`RuleOverlap.Find` is O(n²) per slot and runs on every install; the table is
small and this is not a per-frame path. The `When` lambda escape hatch is
undecidable, so a rule carrying one is treated as reaching every state in its
slot and everything below it degrades to guessing — which is why lambda usage
moved from `INFO` to `WARN`. And `Shadows` is real ceremony: two entries for
the rifle, more for a pack that genuinely reworks base. Accept it; the
alternative is the airborne bug shipping.

### The validation suite

`Animation/RuleValidation.cs` and `Animation/RuleOverlap.cs` — independent
checks, none needing a scene:

| Function | Catches |
|---|---|
| `Validate` | Missing clips, slots with no fallback, unreachable rules |
| `RuleOverlap.CheckDeclarations` | A rule capturing states from a pack it never declared it would outrank |
| `ShadowFixtures.Run` | The shadow check itself having silently become a no-op |
| `RuleOverlap.FindUnsatisfiable` | Rules whose own tags and conditions contradict — can never fire at all |
| `Sweep` + `AssertNoHoles` | States that resolve to nothing (T-pose in production) |
| `SweepDomain.FromTable` | The states the hand-written combo list forgot |
| `DiffSweep` | A new pack changing a state it does not serve |
| `ToGolden` | A new pack silently changing an unrelated state |
| `RampTest` / `RampSpeed` | Thrash — hysteresis and MinDwell across frames |
| `TransitionChecks` | A clip reaching its end and nothing re-issuing it — the character freezing while resolution stays correct |
| `StandardContentChecks` | Which rule wins for a given state, against real content |
| `ContractValidation.Check` | Ability claims no installed pack answers |

`Tests/AnimationChecks.cs` wires all of it together and is the entry point.
`RuleOverlap.Explain` / `AnimDirector.ExplainTable` dump every rule with its
computed region and every shadow between them — the "why is my clip not
playing" printout when the reason lives in a pack you did not write.

`Sweep` is stateless, so it can *see* hysteresis but not exercise it, and
can't see `MinDwell` at all — both only exist across frames. `RampTest` walks
a value up and back down carrying the active rule and dwell timer exactly as
`AnimDirector.Evaluate` does, then counts switches. A clean ramp through one
threshold switches twice; more is thrash.

## Resolution is a level, not an edge

The playground froze: a clip would finish, the player would stop, and the
character stood in the last pose of a walk cycle while every check stayed
green. Resolution was correct the entire time — `StandardContentChecks` passed
19/19 throughout — because the defect was not in which rule won.

The director called `Play()` when the winning rule *changed* and never again.
That is edge-triggered logic against a level-triggered source. The table does
not emit transitions; it publishes a standing answer, and something has to keep
checking that the answer is still being obeyed. `ClipDriver` does that, and the
symptom people report is the giveaway: *"stuck even when changing settings"* —
because changing an input inside one speed rung leaves the same rule winning,
so an edge-triggered driver has nothing to fire on.

`AnimRule.Loops` is the other half. A one-shot reaching its end and holding is
correct, not a freeze; in the real system the ability's phase clears its tag and
the rule stops winning. It is strictly a clip property rather than a rule
property, but the pack is the only thing that knows whether it asked for a walk
cycle or a punch.

Two things fell out of chasing it:

- **`Sword_Idle` imports from the .glb with `LoopMode.None`.** An idle that
  plays once. Nothing in the design could see that, because loop mode is not a
  design property — so `AnimHarness.CheckClipMetadata` cross-checks every rule's
  `Loops` against the imported resource.
- **A stateless test cannot find this.** `TransitionChecks` runs a clock: it
  steps a simulated player at 60Hz through scripted input changes and asserts
  what is playing, deliberately at times well past the current clip's own
  length. Reverting `ClipDriver` to its edge-triggered form turns six of its
  assertions red, including the literal report — *"nudging inside a rung does
  not stall playback"*.

### Still open

- Inertialization in `SwitchClip` — still a crossfade.
- `LayerDef.BoneMask` is carried and never applied.
- Packs are constructed in code. `Condition` makes Resource serialization
  possible; the Godot `[Export]` plumbing isn't written.
- `RampTest` is driven by hand, one slot and one field per call. The same
  derivation `SweepDomain` does for the sweep would generate ramps across
  every threshold in the table.
