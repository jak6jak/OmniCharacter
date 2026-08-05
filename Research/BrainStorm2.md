# NEW GOALS

I want to make it possible to easily have AA-AAA animations for humanoids in Godot. The current brainstorm was built on the idea that the OmniCharacter would be only one node with connecting resources. I think the next approach would be to have multiple nodes that build upon Godot's CharacterBody3D and AnimationPlayer nodes.

# PILLARS OF DESIGN
- testable
- easy to use (intuitive)
- easy to extend
- good defaults
- Strings should NOT point to resources or properties
- Each tag should be a type 
# NODES

- OmniCharacter : CharacterBody3D
- OmniAnimationPlayer : AnimationPlayer it only consumes state.
- TagDatabase: A database of valid tags and values. Atttached to the OmniCharacter???
- AbilityRuntime: This is the controller/state machine for the character's abilities. This is the main entry point for the user to define and control the character.

Overall the design could be seen as a different way of modeling multiple concurrent state machines.

# Definitions
Slot: A slot is a group of bones.
CharacterState: A defined set of values that represent the state of the character.
Tag: Specifies an action or occurance that would affect the animation playing or how is should be played. It is the higher order of "state"
	- Example: "Run" and "reload" in the tag set should mean that the reloading and running animations should be played.
	- Tags are applied to the state and queried off animation clips
	- Abilities push tags onto the tag set.
Events: One off state that is short-lived and is removed after it is triggered.
Rule: The slot and a discrete set of tags and/or a condition on continous values to help narrow the exact animation to play.
	- Example: "Run" with a tag of "run" and a movement vector of non zero.
- AbilityPhase: This is one step of an ability. It declares what it holds, advances it, or branches out of it. Ability can contain multiple phases. It should also declare what state it modifies.
- AnimationBlendDefs: A way to pull in the characters state for blendspaces.
## Claude idea from previous brainstorm
Cluade defined the following terms: requires, serves, and shadows.
This is how packs work:
 - packs are installed/uninstalled through gameplay
 - packs claim slots through their tags.
 - Packs work based off a layering and shadowing system. Packs higher in the stack shadow packs lower in the stack.
 - 

## Ability:
- An ability is an action that the character can perform over a period of time.
- More than one ability can be active at the same time.
- Abilities modify the character's state.
- It defines the state properties it modifies.
- If another state has the potential to modify the same property, the ability can define how it interacts with that state.
- It can push tags onto the tag set.

# Ability Stacking & Layering Architecture


-- Gemnini GENERATED -- 
## Four Modalities of Ability Stacking

Ability layering operates across four distinct technical dimensions:

┌─────────────────────────────────────────────────────────────────────────┐
│                     1. Channel-Isolated Stacking                       │
│  LocomotionAbility (LowerBody) + ReloadAbility (UpperBody) + Aim (Head) │
└─────────────────────────────────────────────────────────────────────────┘
                                     │
┌─────────────────────────────────────────────────────────────────────────┐
│                     2. Priority Override Stacking                      │
│     MeleeSlash (Priority 100) temporarily suppresses Reload (Priority 50)│
└─────────────────────────────────────────────────────────────────────────┘
                                     │
┌─────────────────────────────────────────────────────────────────────────┐
│                     3. Additive Transform Stacking                     │
│    Recoil Offset + Hit Reaction layered on top of Base Skeleton Pose   │
└─────────────────────────────────────────────────────────────────────────┘
                                     │
┌─────────────────────────────────────────────────────────────────────────┐
│                     4. Instance / Stack Multiplicity                    │
│      Poison DoT (3 stacks active) or Rapid-Fire Spell instances          │
└─────────────────────────────────────────────────────────────────────────┘


1. Channel-Isolated Concurrent Stacking

Mechanism: Abilities target completely non-overlapping state tags and skeletal bone slots.

Example: LocomotionAbility writes velocity parameters to update running animations on Slot_LowerBody, while ReloadAbility runs simultaneously and writes "Action.Reloading" to drive Slot_UpperBody.

Conflict Level: Zero. Both abilities execute fully and push independent tags.

2. Priority Override Stacking (State Interruption)

Mechanism: Two active abilities attempt to modify the same parameter or compete for the same bone slot.

Example: Player is running and reloading (ReloadAbility active). Player triggers MeleeSlashAbility.

Resolution Rule: MeleeSlashAbility declares higher execution priority (e.g., Priority 100 vs. Priority 50). AbilityRuntime suspends or cancels ReloadAbility's tags or allows MeleeSlashPack to shadow ReloadPack on Slot_UpperBody.

3. Additive Stacking (Modifier Layers)

Mechanism: Secondary abilities apply additive pose offsets or parameter multipliers rather than replacing base values.

Example: While running and shooting, character receives a DamageHitReaction ability.

Animation Resolution: The hit reaction plays on an Additive Animation Slot, applying local bone differential matrices $(\Delta T)$ on top of the base running/shooting skeleton pose without interrupting the active abilities.

4. Instance Stacking (Multiplicity)

Mechanism: The same OmniAbility class executing in multiple parallel instances (e.g., 3 active stacks of a SpeedBuffAbility or simultaneous weapon recoil impulses).

Resolution: Managed via an InstancingPolicy enum on OmniAbility:

SINGLE_INSTANCE (Default): Triggering again resets duration or phase.

STACKING_INSTANCES: Creates distinct runtime instances with capped max stacks.

REJECT_NEW: Fails activation if already active.

B. Solving Parameter Contention (Attribute Modifier Stacks)

When multiple abilities stack concurrently (e.g., SprintAbility increasing movement speed by $+50\%$, while SlowDebuffAbility reduces speed by $-30\%$), directly overwriting a property (state.set_param("move_speed", 10.0)) causes Last-Writer-Wins bugs.

Solution: Stackable Attribute Modifiers

Instead of raw continuous values, scalar attributes in GatheredState are calculated dynamically using an Attribute Modifier Stack:

$$\text{FinalValue} = \left(\text{BaseValue} + \sum \text{AdditiveModifiers}\right) \times \left(1.0 + \sum \text{PercentModifiers}\right)$$

class_name EvaluatedAttribute extends RefCounted

var base_value: float = 1.0
var additive_modifiers: Dictionary = {} # ability_id -> float
var percent_modifiers: Dictionary = {}  # ability_id -> float

func add_modifier(source_id: StringName, amount: float, is_percent: bool) -> void:
	if is_percent:
		percent_modifiers[source_id] = amount
	else:
		additive_modifiers[source_id] = amount

func remove_modifier(source_id: StringName) -> void:
	additive_modifiers.erase(source_id)
	percent_modifiers.erase(source_id)

func get_value() -> float:
	var add_sum: float = 0.0
	for val in additive_modifiers.values():
		add_sum += val
		
	var pct_sum: float = 0.0
	for val in percent_modifiers.values():
		pct_sum += val
		
	return (base_value + add_sum) * (1.0 + pct_sum)


C. Ability Cancellation & Conflict Resolution Matrix

To prevent invalid state combinations when abilities stack, each OmniAbility defines four tag filtering arrays:

required_tags: Tags that MUST be present in GatheredState to activate.

blocking_tags: Tags that PREVENT activation if present.

cancel_abilities_with_tags: Active abilities pushing these tags are FORCE-CANCELLED when this ability activates.

block_ability_tags: Prevents new abilities with these tags from starting while this ability is running.

Conflict Resolution Pipeline (AbilityRuntime.try_activate_ability)

[ Activation Request ]
          │
          ▼
 Does GatheredState have all required_tags? ───(No)───► [ REJECT ]
          │ (Yes)
          ▼
 Does GatheredState contain any blocking_tags? ─(Yes)──► [ REJECT ]
          │ (No)
          ▼
 Are there higher-priority active abilities blocking this tag? ─(Yes)──► [ REJECT ]
          │ (No)
          ▼
 [ CANCEL CONFLICTS ] ──► Cancel active abilities with matching tags
          │
          ▼
 [ ACTIVATE ] ──► Register granted_tags in GatheredState & start physics tick


# Examples node structure

- OmniCharacter
  - Rig
  - Camera
  - OmniAnimationPlayer

# OmniCharacter Resources:

- Animation Packs: A pack represents a collection of animations and rules for playing them.
- AbilityDefs
- GatheredState: The state represents the values the AnimationDirector should watch for to determine the appropriate animation to play.
- TagSet
- Events


# Open design questions:
- how root motion would work
- 
