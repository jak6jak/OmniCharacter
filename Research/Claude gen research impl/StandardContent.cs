using Godot;
using CharacterKit.Animation;

namespace CharacterKit.Content;

// =====================================================================
// Packs authored against the clips that actually exist.
//
// Content/Packs.cs names an invented clip vocabulary — base/idle,
// rifle/reload — which is the right way to discuss a design and the wrong
// way to watch one run. These packs name the 46 animations in
// res://Godot/AnimationLibrary_Godot_Standard.glb, so the playground can put
// the rule table on screen driving a real skeleton.
//
// This is also the first honest test of the pack model: an author sitting
// down with a fixed clip list and no ability to invent one that fits.
// =====================================================================
public static class ST
{
    public static readonly StringName WeaponPistol  = "weapon.pistol";
    public static readonly StringName WeaponSword   = "weapon.sword";

    public static readonly StringName ActionReload   = "action.reload";
    public static readonly StringName ActionAttack   = "action.attack";
    public static readonly StringName ActionRoll     = "action.roll";
    public static readonly StringName ActionInteract = "action.interact";
}

// ---------------------------------------------------------------------
// Base locomotion. A four-rung speed ladder, crouch, and air.
// ---------------------------------------------------------------------
public static class StandardBase
{
    public const string Id = "std.base";

    public static AnimPack Pack() => new()
    {
        Id = Id,
        Order = 0,
        Layers = new[] { new LayerDef { Slot = Slots.Base, DefaultWeight = 1f } },
        Rules = new[]
        {
            // Air, split by whether we are still going up.
            new AnimRule { Slot = Slots.Base, Require = TagQuery.All(T.Airborne),
                           Conditions = new[] { Condition.Below(Field.VerticalSpeed, 0f) },
                           Clip = "Jump", MinDwell = 0.15f },
            new AnimRule { Slot = Slots.Base, Require = TagQuery.All(T.Airborne),
                           Clip = "Jump_Start", MinDwell = 0.15f, Loops = false },

            // Crouch owns its own little ladder.
            new AnimRule { Slot = Slots.Base, Require = TagQuery.All(T.Crouched),
                           Conditions = new[] { Condition.Above(Field.PlanarSpeed, 0.2f, hysteresis: 0.15f) },
                           Clip = "Crouch_Fwd" },
            new AnimRule { Slot = Slots.Base, Require = TagQuery.All(T.Crouched),
                           Clip = "Crouch_Idle" },

            // Speed ladder, fastest first. Each rung carries hysteresis wide
            // enough that drifting on a boundary cannot flip it every frame —
            // which is the thing RampTest exists to prove.
            new AnimRule { Slot = Slots.Base,
                           Conditions = new[] { Condition.Above(Field.PlanarSpeed, 4.5f, hysteresis: 0.5f) },
                           Clip = "Sprint" },
            new AnimRule { Slot = Slots.Base,
                           Conditions = new[] { Condition.Above(Field.PlanarSpeed, 2.0f, hysteresis: 0.3f) },
                           Clip = "Jog_Fwd" },
            new AnimRule { Slot = Slots.Base,
                           Conditions = new[] { Condition.Above(Field.PlanarSpeed, 0.2f, hysteresis: 0.15f) },
                           Clip = "Walk" },

            new AnimRule { Slot = Slots.Base, Clip = "Idle" },
        }
    };
}

// ---------------------------------------------------------------------
// A weapon pack that covers almost nothing, on purpose.
//
// It provides a standing idle and a reload. Every other state — walking,
// jogging, sprinting, crouching, airborne — falls through to base. That is
// the whole extension claim ("a pistol with only an idle and a reload still
// works") stated as content rather than as prose.
// ---------------------------------------------------------------------
public static class StandardPistol
{
    public const string Id = "std.pistol";

    public static AnimPack Pack() => new()
    {
        Id = Id,
        Order = 100,
        Requires = new[] { StandardBase.Id },
        Serves = new[] { new Claim(Slots.UpperBody, ST.ActionReload) },

        // Nothing to declare any more. These rules live on `upperbody`, which
        // base does not touch at all, so there is no cross-pack shadow to
        // shadow. The earlier `Shadows = ["Idle", "Walk"]` was the price of
        // putting a weapon pose on the full-body slot, and it came with the
        // behaviour to match: the pistol vanished the moment you moved.
        Layers = new[]
        {
            new LayerDef { Slot = Slots.UpperBody, BoneMask = "spine_up",
                           DefaultWeight = 1f, Optional = true },
        },

        Rules = new[]
        {
            new AnimRule { Slot = Slots.UpperBody,
                           Require = TagQuery.All(ST.WeaponPistol, ST.ActionReload),
                           Clip = "Pistol_Reload", MinDwell = 0.3f, Loops = false },

            // No speed condition. The carry pose rides the upper body while
            // the legs run whatever locomotion resolved underneath — which is
            // the entire point of a masked layer.
            new AnimRule { Slot = Slots.UpperBody,
                           // Excluding the OTHER weapon does not scale — with
                           // five weapons every rule carries four exclusions.
                           // The real fix is a declared exclusion group so the
                           // overlap analysis knows weapon.* is one-of. Until
                           // then this is the honest way to say it.
                           Require = TagQuery.All(ST.WeaponPistol).Without(ST.WeaponSword),
                           Clip = "Pistol_Idle" },
        }
    };
}

// ---------------------------------------------------------------------
// A second weapon, to show two partial packs coexisting.
// ---------------------------------------------------------------------
public static class StandardSword
{
    public const string Id = "std.sword";

    public static AnimPack Pack() => new()
    {
        Id = Id,
        Order = 100,
        Requires = new[] { StandardBase.Id },
        Layers = new[]
        {
            new LayerDef { Slot = Slots.UpperBody, BoneMask = "spine_up",
                           DefaultWeight = 1f, Optional = true },
        },

        Rules = new[]
        {
            new AnimRule { Slot = Slots.UpperBody,
                           Require = TagQuery.All(ST.WeaponSword).Without(ST.WeaponPistol),
                           Clip = "Sword_Idle" },
        }
    };
}

// ---------------------------------------------------------------------
// Abilities. Every rule is gated behind a tag this pack claims, so the whole
// pack is self-scoped: it outranks everything and declares nothing.
// ---------------------------------------------------------------------
public static class StandardActions
{
    public const string Id = "std.actions";

    public static AnimPack Pack() => new()
    {
        Id = Id,
        Order = 400,
        Requires = new[] { StandardBase.Id },
        Serves = new[]
        {
            new Claim(Slots.Base, ST.ActionAttack),
            new Claim(Slots.Base, ST.ActionRoll),
            new Claim(Slots.Base, ST.ActionInteract),
        },
        Rules = new[]
        {
            new AnimRule { Slot = Slots.Base, Require = TagQuery.All(ST.ActionRoll),
                           Clip = "Roll", MinDwell = 0.4f, Loops = false, FullBody = true },

            // Same tag, different weapon, different clip — the reason actions
            // name a tag rather than a clip.
            new AnimRule { Slot = Slots.Base,
                           Require = TagQuery.All(ST.ActionAttack, ST.WeaponSword),
                           Clip = "Sword_Attack", MinDwell = 0.3f, Loops = false, FullBody = true },
            new AnimRule { Slot = Slots.Base, Require = TagQuery.All(ST.ActionAttack),
                           Clip = "Punch_Jab", MinDwell = 0.25f, Loops = false, FullBody = true },

            new AnimRule { Slot = Slots.Base, Require = TagQuery.All(ST.ActionInteract),
                           Clip = "Interact", MinDwell = 0.3f, Loops = false, FullBody = true },
        }
    };
}

public static class StandardSet
{
    public static AnimPack[] All() => new[]
    {
        StandardBase.Pack(),
        StandardPistol.Pack(),
        StandardSword.Pack(),
        StandardActions.Pack(),
    };
}
