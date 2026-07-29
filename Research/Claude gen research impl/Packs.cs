using Godot;
using CharacterKit.Animation;
using CharacterKit.Movement;

namespace CharacterKit.Content;

// =====================================================================
// 1. Base locomotion. Installed once. Owns the fallback for every slot.
// =====================================================================
public static class BaseLocomotion
{
    public static AnimPack Pack() => new()
    {
        Id = "base",
        Order = 0,
        Library = AnimPack.LoadLibrary("res://anim/base.res"),
        Layers = new[]
        {
            new LayerDef { Slot = Slots.Base,      DefaultWeight = 1f },
            new LayerDef { Slot = Slots.FullBody,  DefaultWeight = 1f, Optional = true },
            new LayerDef { Slot = Slots.AddLean,   Additive = true, BoneMask = "spine_up", DefaultWeight = 1f },
        },
        Rules = new[]
        {
            // FullBody: only claimed by requests, so it needs no fallback rule
            // beyond the ones abilities bring with them.

            // Base: ordered most-specific first. First match wins.
            new AnimRule { Slot = Slots.Base, Require = TagQuery.All(T.Airborne),
                           Conditions = new[] { Condition.Below(Field.VerticalSpeed, 0f) },
                           Clip = "base/fall" },
            new AnimRule { Slot = Slots.Base, Require = TagQuery.All(T.Airborne), Clip = "base/jump_rise" },
            new AnimRule { Slot = Slots.Base, Require = TagQuery.All(T.Crouched),  Clip = "base/crouch_space" },

            // Hysteresis of 0.15 keeps a character drifting at walking-threshold
            // speed from flipping between idle and locomotion every frame.
            new AnimRule { Slot = Slots.Base,
                           Conditions = new[] { Condition.Above(Field.PlanarSpeed, 0.2f, hysteresis: 0.15f) },
                           Clip = "base/loco_space" },

            new AnimRule { Slot = Slots.Base, Clip = "base/idle" },

            new AnimRule { Slot = Slots.AddLean, Clip = "base/lean_additive" },
        }
    };
}

// =====================================================================
// 2. A weapon pack. This is the extension story.
//    Note what it does NOT do: it never edits BaseLocomotion, never touches
//    Character.cs, and never references the dash or the injury system.
// =====================================================================
public static class RiflePack
{
    public const string Id = "rifle";

    public static AnimPack Pack() => new()
    {
        Id = Id,
        Order = 100,                       // shadows base wherever it matches
        Library = AnimPack.LoadLibrary("res://anim/rifle.res"),
        Layers = new[]
        {
            new LayerDef { Slot = Slots.UpperBody, BoneMask = "spine_up", DefaultWeight = 1f, Optional = true },
        },
        Requires = new[] { "base" },

        // The Base rule below deliberately outranks base's locomotion, so the
        // two clips it displaces are named here. Naming clips rather than the
        // pack is the point: "base" as a blanket entry would also wave
        // through the capture of base/fall that the exclusions below now
        // prevent, and the check would pass clean on the bug.
        //
        // The UpperBody rules need no entry — base owns no UpperBody rules,
        // and the reload rule is gated behind a tag in Serves, so it is
        // self-scoped.
        Shadows = new[] { "base/loco_space", "base/idle" },

        Serves = new[] { new Claim(Slots.UpperBody, T.ActionReload) },
        Rules = new[]
        {
            // Upper body, most specific first.
            new AnimRule { Slot = Slots.UpperBody, Require = TagQuery.All(T.WeaponRifle, T.ActionReload),
                           Clip = "rifle/reload" },
            new AnimRule { Slot = Slots.UpperBody, Require = TagQuery.All(T.WeaponRifle),
                           Conditions = new[] { Condition.Above(Field.PlanarSpeed, 6f, hysteresis: 0.5f) },
                           Clip = "rifle/sprint_carry" },
            new AnimRule { Slot = Slots.UpperBody, Require = TagQuery.All(T.WeaponRifle),
                           Clip = "rifle/aim_idle" },

            // Rifle-specific full-body locomotion shadows the unarmed version
            // purely by sitting at a higher Order. The base rules still exist
            // and come back the moment the rifle is unequipped.
            //
            // The exclusions are not decoration. Without them this rule sits
            // above base/fall, base/jump_rise and base/crouch_space at Order
            // 100, matches while airborne (PlanarSpeed is horizontal, so it
            // stays above 0.2 through the whole arc), and a rifle-carrying
            // character running off a ledge plays a run cycle all the way
            // down. That is exactly the class of bug the flat table gets
            // criticized for — a rule inserted for one reason quietly
            // capturing states nobody was thinking about — and it sat here
            // undetected until the shadow check named the region.
            new AnimRule { Slot = Slots.Base,
                           Require = TagQuery.All(T.WeaponRifle).Without(T.Airborne, T.Crouched),
                           Conditions = new[] { Condition.Above(Field.PlanarSpeed, 0.2f, hysteresis: 0.15f) },
                           Clip = "rifle/loco_space" },
        }
    };
}

// =====================================================================
// 3. A status effect. Additive layer, weight fades in. Zero coupling to
//    anything else — it does not know or care what the player is holding.
// =====================================================================
public static class InjuryPack
{
    public static AnimPack Pack() => new()
    {
        Id = "injury",
        Order = 50,
        Requires = new[] { "base" },
        Serves = new[] { new Claim(Slots.AddInjury, T.StatusInjured) },
        Library = AnimPack.LoadLibrary("res://anim/status.res"),
        Layers = new[]
        {
            new LayerDef { Slot = Slots.AddInjury, Additive = true, DefaultWeight = 0f, Optional = true },
        },
        Rules = new[]
        {
            new AnimRule { Slot = Slots.AddInjury, Require = TagQuery.All(T.StatusInjured),
                           Clip = "status/limp_additive" },
        }
    };
}

// =====================================================================
// USAGE
// =====================================================================

/// Equipping a weapon is one tag write plus a pack install. That is the
/// entire integration surface between equipment and animation.
public sealed partial class EquipmentSystem : Node
{
    [Export] public Character Owner;

    private StringName _currentTag;
    private string _currentPack;

    public void Equip(StringName weaponTag, AnimPack pack)
    {
        if (_currentTag is not null) Owner.State.Tags.Remove(_currentTag);
        if (_currentPack is not null) Owner.Anim.Uninstall(_currentPack);

        var result = Owner.Anim.Install(pack);
        foreach (var issue in result.Issues) GD.Print($"[anim] {issue}");
        if (!result.Ok) return;   // don't set the tag for a pack that didn't take

        Owner.State.Tags.Add(weaponTag);
        _currentTag = weaponTag;
        _currentPack = pack.Id;
    }

    // Called from an inventory UI, a pickup, a cutscene — doesn't matter.
    public void EquipRifle() => Equip(T.WeaponRifle, RiflePack.Pack());
}

/// A reload. It claims the upper body by TAG, not by clip name — so the same
/// three lines produce a rifle reload, a pistol reload, or a crossbow reload
/// depending on which pack happens to be installed.
public sealed partial class ReloadAction : Node
{
    [Export] public Character Owner;
    private AnimHandle _h;

    public async void Start(float duration)
    {
        Owner.State.Tags.Add(T.ActionReload);
        _h = Owner.Anim.Request(Slots.UpperBody, T.ActionReload, Priority.Ability, blendIn: 0.1f, owner: this);

        await ToSignal(GetTree().CreateTimer(duration), SceneTreeTimer.SignalName.Timeout);

        Owner.State.Tags.Remove(T.ActionReload);
        Owner.Anim.Release(_h);   // upper body falls back to aim_idle on its own
    }
}

/// An ability that owns movement AND animation for its duration.
public sealed partial class DashAbility : Node
{
    [Export] public Character Owner;
    private AnimHandle _h;

    public void Activate()
    {
        var motor = new DashMotor(Owner.Facing, speed: 18f, duration: 0.20f);
        Owner.Motors.Push(Owner, motor);

        Owner.State.Tags.Add(T.ActionDash);
        _h = Owner.Anim.Request(Slots.FullBody, T.ActionDash, Priority.Ability, blendIn: 0.05f, owner: this);

        // The motor pops itself; we just need to know when.
        _ = ReleaseWhenDone(motor);
    }

    private async System.Threading.Tasks.Task ReleaseWhenDone(DashMotor m)
    {
        while (!m.Finished) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        Owner.State.Tags.Remove(T.ActionDash);
        Owner.Anim.Release(_h);
    }
}

/// A status effect ticking. Note it never inspects the weapon, the dash, or
/// the locomotion state — it fades one layer weight and sets one tag.
public sealed partial class InjuryEffect : Node
{
    [Export] public Character Owner;
    [Export] public float FadeIn = 0.6f;

    private float _t;
    public bool Active;

    public override void _Process(double delta)
    {
        if (!Active) return;
        _t = Mathf.Min(_t + (float)delta, FadeIn);
        Owner.State.Tags.Add(T.StatusInjured);
        Owner.Anim.SetLayerWeight(Slots.AddInjury, _t / FadeIn);
    }
}

// ---------------------------------------------------------------------
// What happens on a frame where the player is sprinting with a rifle,
// injured, and mid-dash:
//
//   State.Tags   = { weapon.rifle, status.injured, action.dash }
//   FullBody     -> dash rule (Priority.Ability wins the slot)
//   Base         -> rifle/loco_space   (rifle Order 100 shadows base Order 0)
//   UpperBody    -> rifle/sprint_carry
//   add.injury   -> status/limp_additive at weight 1.0
//   add.lean     -> base/lean_additive, blend driven by TurnRate
//
// Four systems contributed. None of them knows the others exist, and
// Character.cs did not change to make any of it work.
// ---------------------------------------------------------------------
