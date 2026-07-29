using Godot;
using CharacterKit.Animation;
using CharacterKit.Movement;

namespace CharacterKit.Content;

public static class GrappleTags
{
    public static readonly StringName Throw     = "action.grapple_throw";
    public static readonly StringName Swing     = "action.grapple_swing";
    public static readonly StringName Whiff     = "action.grapple_whiff";
    public static readonly StringName RopeBreak = "action.grapple_break";
}

// =====================================================================
// The pack. Pure content — no reference to Character, the motor stack,
// or any other pack. Installing it is the entire integration step.
// =====================================================================
public static class GrapplePack
{
    public const string Id = "grapple";

    public static AnimPack Pack() => new()
    {
        Id = Id,
        Order = 150,
        Requires = new[] { "base" },

        // No `Shadows` declaration, and that is the point of the self-scoping
        // exemption. At Order 150 these UpperBody rules outrank every weapon
        // pack's, so a naive check would demand Shadows = ["rifle", "pistol",
        // "bow", ...] — every weapon that will ever ship, listed by a pack
        // with no business knowing any of them exist.
        //
        // But each rule below is gated behind a tag that appears in `Serves`.
        // A rule that only fires in states this pack was asked to own cannot
        // surprise anyone, so the check exempts it. The rifle's Base rule gets
        // no such exemption — `weapon.rifle` is a tag it reacts to, not one it
        // claims — which is why that one has to declare.

        // The other half of the ability/pack contract. Each phase in
        // GrappleAbility claims one of these; ContractValidation checks that
        // the two lists agree instead of trusting the shared tag constants.
        Serves = new[]
        {
            new Claim(Slots.UpperBody, GrappleTags.Throw),
            new Claim(Slots.UpperBody, GrappleTags.Whiff),
            new Claim(Slots.FullBody,  GrappleTags.Swing),
            new Claim(Slots.FullBody,  GrappleTags.RopeBreak),
        },

        Library = AnimPack.LoadLibrary("res://anim/grapple.res"),
        Layers = new[]
        {
            // Throw rides the upper body, so the player keeps running while
            // it plays. Swing takes the full body.
            new LayerDef { Slot = Slots.UpperBody, BoneMask = "spine_up", DefaultWeight = 1f, Optional = true },
            new LayerDef { Slot = Slots.FullBody,  DefaultWeight = 1f, Optional = true },
        },
        Rules = new[]
        {
            new AnimRule { Slot = Slots.UpperBody, Require = TagQuery.All(GrappleTags.Throw),
                           Clip = "grapple/throw", MinDwell = 0.15f },

            // The miss gets its own animation. A failed input that produces no
            // visible response reads as a dropped input, and the player mashes.
            new AnimRule { Slot = Slots.UpperBody, Require = TagQuery.All(GrappleTags.Whiff),
                           Clip = "grapple/whiff_retract", MinDwell = 0.1f },

            // Rope snapping mid-swing is a different event from letting go, and
            // deserves a different read.
            new AnimRule { Slot = Slots.FullBody, Require = TagQuery.All(GrappleTags.RopeBreak),
                           Clip = "grapple/rope_break", MinDwell = 0.2f },

            // One rule for the whole swing. Direction is continuous, so it is a
            // blendspace driven by the bindings below — not eight rules.
            new AnimRule { Slot = Slots.FullBody, Require = TagQuery.All(GrappleTags.Swing),
                           Clip = "grapple/swing_space" },
        },
        Bindings = new[]
        {
            new ScalarBinding { Scalar = "grapple.rope_yaw",
                                Param = "parameters/swing_space/blend_position", Axis = Axis.X },
            new ScalarBinding { Scalar = "grapple.rope_pitch",
                                Param = "parameters/swing_space/blend_position", Axis = Axis.Y },
            new ScalarBinding { Scalar = "grapple.speed",
                                Param = "parameters/swing_rate/scale", Scale = 0.06f },
        }
    };
}

/// Marker for surfaces the hook can bite. Being a node rather than a physics
/// layer means level designers can tag a specific ledge without repainting
/// collision, and it survives the object being on a moving platform.
public sealed partial class Grappleable : Node3D { }
