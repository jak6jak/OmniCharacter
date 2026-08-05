System Design Document: OmniCharacter & Tag-Driven Animation SystemTarget Engine: Godot 4.xGoal: Modular, AA/AAA-grade humanoid animation system based on concurrent tag-driven animation layering, modular slot-based bone masking, and priority-driven state evaluation.1. System Overview & PhilosophyThe OmniCharacter architecture replaces rigid single-state animation trees with a centralized state, tag-driven, concurrent multi-layered state machine.Rather than building a single monolithic AnimationTree with hundreds of manual transitions, the system decomposes animation playback into independent Animation Packs operating on specific groups of bones called Slots. A central engine evaluates runtime state tags against active rules to pick, blend, and mask animation clips dynamically.Key Architectural PrinciplesCentralized State at the Top: Single source of truth for runtime character parameters (tags, velocity, inputs, combat status).Modular Bone Slots: Skeleton is partitioned into logical bone masks (e.g., FullBody, UpperBody, RightArm, Locomotion).Tag-Driven Rule Engine: Clips are selected via set-matching logic (Discrete Tags + Continuous Parameters) rather than explicit hardcoded transitions.Layering & Shadowing: Modular packs claim bone slots based on priority. High-priority packs (e.g., HitReaction, Reload) shadow lower-priority packs (e.g., BaseLocomotion).Decoupled Transition Evaluation: Slot takeover transitions (crossfading and inertialization) are handled per-slot by dynamic blending buffers rather than hardcoded edge connections.2. Architecture & Node HierarchyOmniCharacter (CharacterBody3D)
│
├── Skeleton3D / Rig (Model Visuals)
├── Camera3D / SpringArm3D
│
├── OmniMotor (Node or Resource)
├── OmniState (Resource / Sub-node)
│
└── OmniAnimationPlayer (Node)
└── (Internal) Godot AnimationPlayer / AnimationTree Bridge
Core Components1. OmniCharacter (CharacterBody3D)Role: High-level orchestrator and physics body.Responsibilities:Owns reference to the Skeleton3D, OmniState, OmniMotor, and OmniAnimationPlayer.Serves as the main API endpoint for gameplay scripts (e.g., character.apply_tag("Reloading")).2. OmniAnimationPlayer (AnimationPlayer Wrapper / Node)Role: Central evaluation runtime and animation coordinator.Responsibilities:Subscribes to changes in OmniState.Evaluates registered Animation Packs every frame or tick.Resolves slot ownership conflicts (shadowing/blending).Manages per-slot crossfading buffers and transition states.Drives underlying bone transforms or configures Godot’s native AnimationTree / AnimationNodeBlendTree.3. TagDatabase (Resource)Role: Project-wide registry of valid tags, hierarchy, and metadata.Responsibilities:Prevents typo bugs by enforcing validated StringName tags or bitmask flags.Supports hierarchical tag relationships (e.g., State.Movement.Run inherits from State.Movement).4. OmniState (Resource / Object)Role: Centralized runtime state repository.Responsibilities:Stores discrete active Tags (e.g., ["Locomotion", "Running", "Armed", "Rifle"]).Stores continuous Parameters (e.g., speed: float, move_vector: Vector2, aim_pitch: float).Dispatches state_changed signals when tags are added, removed, or parameter thresholds are crossed.5. OmniMotor (Node / Resource)Role: Handles physics movement and velocity processing for CharacterBody3D.Responsibilities:Updates velocity, ground status, friction, and slope handling.Writes motion metrics (ground_speed, is_on_floor, turn_rate) directly into OmniState.3. Core Data Structures & ConceptsA. Bone Slots (OmniSlot)A Slot represents a logical grouping of bones defined on the target Skeleton3D.Examples:Slot_FullBody: All bones in the skeleton.Slot_UpperBody: Spine01 upward, including arms and head.Slot_RightArm: Clavicle_R through Hand_R.Slot_LowerBody: Pelvis and leg bones.Implementation: Defines a BoneAttachment3D group or a SkeletonProfile / BoneMap filter mask.B. Tags & QueriesTag: A unique identifier representing an action, stance, condition, or equipment status.Tag Query: A logical set condition required for a rule to match (e.g., Requires: ["Moving", "Armed"], Excludes: ["Crouching"]).C. Rules (OmniAnimationRule)A Rule maps a discrete state to a specific clip or blendspace, along with transition parameters for entering and exiting this rule.class_name OmniAnimationRule extends Resource

@export var name: StringName
@export var slot: OmniSlot
@export var required_tags: Array[StringName]
@export var excluded_tags: Array[StringName]
@export var priority: int = 0

# Transition settings

@export var fade_in_time: float = 0.2
@export var fade_out_time: float = 0.2
@export var ease_type: Tween.EaseType = Tween.EASE_IN_OUT

# Animation sources

@export var blend_space: AnimationNode # e.g., BlendSpace2D for directional run
@export var animation_clip: StringName # Single clip fallback
D. Animation Packs (OmniAnimationPack)An Animation Pack is a self-contained module of animations and rules (e.g., RifleLocomotionPack, UnarmedMovementPack, MeleeAttackPack).Pack Properties:Serves: List of slots this pack can control (e.g., UpperBody, RightArm).Requires: Base tags required for this pack to activate (e.g., Weapon.Rifle).Priority: Integer priority defining shadow hierarchy.Rules: Array of OmniAnimationRule objects contained in the pack.4. Layering, Shadowing, and Dynamic Priority PipelineWhen multiple state machines/packs attempt to influence the same bones, OmniAnimationPlayer resolves conflicts using a Priority & Shadowing pipeline. [ OmniState ] (Active Tags & Continuous Parameters)
│
▼
[ Animation Pack Stack Evaluation ]
├── Pack A (Priority 100): "Melee Slash" ---> Claims: UpperBody (Active)
├── Pack B (Priority 50): "Rifle Aim" ---> Claims: UpperBody (Shadowed)
└── Pack C (Priority 10): "Base Locomotion" ---> Claims: LowerBody (Active)
│
▼
[ Conflict Resolution Engine ]
UpperBody -> Handled by Pack A ("Melee Slash")
LowerBody -> Handled by Pack C ("Base Locomotion")
│
▼
[ Slot Takeover & Transition Buffers ]
(Generates crossfade weights W_A, W_B per slot)
│
▼
[ Bone Masking & Output Generation ]
Apply final skeletal transforms per slot to Skeleton3D
Resolution RulesFull Override (Shadowing): If Pack A (Priority 100) and Pack B (Priority 50) both claim Slot_UpperBody and both have active rules matching the current OmniState, Pack A completely shadows Pack B on Slot_UpperBody.Partial Blending (Additive / Layered): If a pack is configured as an Additive Layer (e.g., HitReaction or AimOffset), its output is mathematically added onto lower layers rather than shadowing them.Yielding Control: When Pack A finishes its execution or removes its active tag (e.g., attack finishes), Pack A relinquishes Slot_UpperBody, triggering a yield transition back to Pack B.5. Slot Takeover & Transition MechanicsTransitions occur when slot ownership changes. Rather than connecting static transition arrows between states, transitions are calculated dynamically per slot using Slot Evaluator Buffers.A. Transition ScenariosTakeover (Higher Priority Shadowing):Trigger: A higher-priority pack activates (e.g., ReloadPack with priority 50 takes over UpperBody from RifleAimPack with priority 10).Action: The slot evaluator marks the incoming rule as Target and the current active rule as Outgoing. The blending weight ramps from $0.0 \to 1.0$ over Target.fade_in_time.Yielding (Priority Relinquishment):Trigger: A higher-priority pack deactivates or clears its tags (e.g., ReloadPack completes and removes tag "Reloading").Action: Slot ownership yields back to the highest active underlying pack (RifleAimPack). The system crossfades out of ReloadPack over Outgoing.fade_out_time.Intra-Pack Rule Mutation:Trigger: The active pack remains the same, but internal tags change matching rules (e.g., state shifts from Walk to Run within BaseLocomotionPack).Action: Slot evaluator crossfades between rules within the same pack using the incoming rule's fade_in_time.B. Dynamic Transition ImplementationsMethod 1: Dual-Source Slot Buffering (Traditional Crossfade)Each active OmniSlot manages an internal state buffer tracking two sources during transitions:Source_A (Outgoing): Previous rule clip/blendspace.Source_B (Incoming): New winning rule clip/blendspace.Weight ($W \in [0, 1]$): Computed per frame:$$W(t) = \text{clamp}\left(\frac{t - t_0}{T_{\text{transition}}}, 0.0, 1.0\right)$$Godot Bridge: Implemented via a dynamic AnimationNodeBlend2 per slot inside the underlying AnimationNodeBlendTree. Once $W = 1.0$, Source_A is freed and Source_B becomes the sole active node.Method 2: Inertialization (Inertial Blending)For AAA motion quality without the performance overhead of running dual animation layers simultaneously:At Takeover Instant ($t_0$): Record current bone transform positions $P_0$, velocities $V_0$, and rotational velocities $\Omega_0$ of outgoing animation on affected slot.Immediate Swap: Slot instantly switches evaluation to the incoming animation clip at $t_0$.Inertial Offset Decay: Difference between outgoing pose and incoming pose is stored as decay offset $\Delta P(t)$, decaying to zero via a critically damped spring equation over duration $T_{\text{inertial}}$:$$\text{FinalTransform}(t) = \text{IncomingTransform}(t) + \Delta P(t) \cdot e^{-\gamma t}$$C. Transition Configuration Matrix (OmniTransitionRule)class_name OmniTransitionRule extends Resource

@export var from_tag: StringName
@export var to_tag: StringName
@export var transition_clip: StringName # Optional bridge clip (e.g., "Stand_To_Crouch")
@export var duration: float = 0.25
@export var force_interruptible: bool = true 6. Tag Mutators & Lifecycle Management (Who Modifies OmniState?)In OmniCharacter, OmniState does not generate tags itself; it acts purely as a synchronized data container. Tags are pushed to and removed from OmniState by five primary system categories:[ Input Controller ] ---> Adds "Input.Aim", "Input.Sprint"
[ OmniMotor ] ---> Updates "State.Grounded", "State.Moving", "State.InAir"
[ Inventory / Gear ] ---> Applies "Weapon.Rifle", "Stance.TwoHanded"
[ Combat / Health ] ---> Applies "Status.Stunned", "Status.Dead"
[ Animation Tracks ] ---> Clears "Action.MeleeSwing" on Animation End Marker
Tag Categories & Their MutatorsMutator SystemTag CategoryLifecycle / DurationExamplesOmniMotor (Physics Engine)State.*Automatic / Frame-Evaluated: Updated every _physics_process based on raycasts and CharacterBody3D velocity.State.Grounded, State.InAir, State.Moving, State.SlidingInput / AI ControllerInput.*Hold / Intent-Based: Active while button is held or AI goal is active.Input.Aim, Input.Crouch, Input.SprintEquipment / InventoryEquipment.*Persistent / Item-Bound: Applied when equipping an item, removed on unequip.Equipment.Weapon.Rifle, Equipment.Shield, Stance.TwoHandedCombat & Buff SystemsStatus.*Timer / Gameplay-Bound: Added on hit/buff trigger, removed via duration timer or status removal script.Status.Stunned, Status.Knockback, Status.DeadAnimation Events (Method Tracks / Markers)Action.*Transient / Event-Bound: Added when an ability key is pressed; removed explicitly by animation event markers or completion signals.Action.MeleeAttack, Action.Reloading, Window.ComboHandling Action Tag Lifecycle (Avoiding Stuck State)Action tags (like "Action.Reloading" or "Action.MeleeAttack") present a classic bug source where an animation gets interrupted or fails to clean up its tag, locking the player forever.OmniCharacter handles action tag cleanup through three safeguards:Animation Callables / Method Tracks (Standard Path):An event marker placed near the end frame of Anim_Reload calls OmniCharacter.remove_tag("Action.Reloading").Timeout Safety Backup (Fail-Safe):When apply_transient_tag("Action.Reloading", duration=1.5) is called, a SceneTreeTimer guarantees tag removal even if the animation fails to play or gets skipped.Interrupt Override (Takeover Path):If a high-priority interrupt tag (e.g., "Status.Stunned") is applied, any active transient tags associated with lower-priority actions are forcibly cleared.7. Execution Pipeline (Per Frame / Event Ticked)State Update Phase:Input/Gameplay drivers update OmniState tags and variables.OmniMotor applies movement physics and updates kinetic state tags (State.Grounded, State.Moving).Pack Filtering Phase:OmniAnimationPlayer iterates over all registered OmniAnimationPacks.Filters out packs whose Requires tag conditions are not met.Rule Matching & Slot Assignment:Active packs evaluate their internal OmniAnimationRules against OmniState.Highest-matching rules claim their assigned OmniSlot.Higher-priority claims shadow lower-priority claims for overlapping bone sets.Slot Transition & Blend Evaluation Phase:Evaluates slot ownership changes against previous frame assignments.For slots experiencing ownership changes or rule shifts, initializes transition state (Crossfade or Inertial offset).Ramps blend weights or computes inertial decay factors for active transitions.Output Generation & Bone Masking:Updates continuous blendspace parameters (e.g., velocity, aim angles).Applies weighted skeletal transforms per slot to Godot's native Skeleton3D or AnimationTree.8. Data Model / GDScript Draft SpecificationsOmniState.gdclass_name OmniState extends Resource

signal state_changed

@export var active_tags: Array[StringName] = []
var parameters: Dictionary = {} # e.g., {"speed": 4.5, "move_dir": Vector2.UP}

func add_tag(tag: StringName) -> void:
if not active_tags.has(tag):
active_tags.append(tag)
state_changed.emit()

func remove_tag(tag: StringName) -> void:
if active_tags.has(tag):
active_tags.erase(tag)
state_changed.emit()

func has_tag(tag: StringName) -> bool:
return active_tags.has(tag)

func set_param(key: StringName, value: Variant) -> void:
parameters[key] = value

func add_transient_tag(tag: StringName, duration: float, tree: SceneTree) -> void:
add_tag(tag)
tree.create_timer(duration).timeout.connect(func(): remove_tag(tag))
OmniSlotEvaluator.gd (Runtime Slot Transition Buffer)class_name OmniSlotEvaluator extends RefCounted

var slot: OmniSlot
var active_rule: OmniAnimationRule
var outgoing_rule: OmniAnimationRule

var transition_time: float = 0.0
var transition_duration: float = 0.2
var blend_weight: float = 1.0

func set_target_rule(new_rule: OmniAnimationRule) -> void:
if active_rule == new_rule:
return

    outgoing_rule = active_rule
    active_rule = new_rule
    transition_time = 0.0
    transition_duration = new_rule.fade_in_time if new_rule else 0.2
    blend_weight = 0.0

func update(delta: float) -> void:
if blend_weight < 1.0 and transition_duration > 0.0:
transition_time += delta
blend_weight = clamp(transition_time / transition_duration, 0.0, 1.0)
if blend_weight >= 1.0:
outgoing_rule = null
OmniSlot.gdclass_name OmniSlot extends Resource

@export var slot_name: StringName
@export var bone_names: Array[StringName]
@export var root_bone: StringName
OmniAnimationPack.gdclass_name OmniAnimationPack extends Resource

@export var pack_name: StringName
@export var priority: int = 0
@export var required_tags: Array[StringName]
@export var serves_slots: Array[OmniSlot]
@export var rules: Array[OmniAnimationRule] 9. Strategic Implementation Roadmap[x] Phase 1: Foundation Data StructuresImplement TagDatabase, OmniState, OmniSlot, and OmniAnimationRule Resources.[x] Phase 2: Single-Slot Rule Matching & Transition EngineBuild OmniAnimationPlayer and OmniSlotEvaluator to evaluate state rules and perform dynamic slot crossfading.[ ] Phase 3: Slot Masking & Priority Shadowing PipelineIntegrate skeleton bone masks to allow simultaneous Upper/Lower body rule playback with priority conflict resolution.[ ] Phase 4: Continuous Parameter Blending IntegrationConnect Godot AnimationNodeBlendSpace2D outputs to rule evaluators for smooth directional movement.[ ] Phase 5: Additive Layering & Inertialization EngineAdd support for additive tracks (hit reactions, aim offsets) and pose offset inertial decay for AAA crossfading.
