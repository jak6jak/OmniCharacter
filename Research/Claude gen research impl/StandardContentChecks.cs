using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using CharacterKit.Animation;
using CharacterKit.Content;

namespace CharacterKit.Tests;

// =====================================================================
// What the playground will actually show.
//
// The playground is a GUI, so the temptation is to verify it by looking at
// it. Looking at it proves one state at a time and proves nothing the next
// time the content changes. These are the states somebody would drag the
// sliders to, asserted.
//
// Pure rule resolution — no engine needed, so this runs in the headless
// harness alongside everything else.
// =====================================================================
public static class StandardContentChecks
{
    public static List<Issue> Run()
    {
        var t = new RuleTable();
        foreach (var p in StandardSet.All()) t.Register(p.Id, p.Order, p.Rules);

        var f = new List<Issue>();

        // ---- the speed ladder -------------------------------------------
        Expect(f, t, "standing",            "Idle",        speed: 0f);
        Expect(f, t, "walking",             "Walk",        speed: 1.0f);
        Expect(f, t, "jogging",             "Jog_Fwd",     speed: 3.0f);
        Expect(f, t, "sprinting",           "Sprint",      speed: 6.0f);

        // ---- stance and air ---------------------------------------------
        Expect(f, t, "crouched still",      "Crouch_Idle", speed: 0f,    tags: new[] { T.Crouched });
        Expect(f, t, "crouched moving",     "Crouch_Fwd",  speed: 1.0f,  tags: new[] { T.Crouched });
        Expect(f, t, "rising",              "Jump_Start",  vspeed: 3f,   tags: new[] { T.Airborne });
        Expect(f, t, "falling",             "Jump",        vspeed: -3f,  tags: new[] { T.Airborne });

        // ---- a partial weapon pack --------------------------------------
        // ---- layering -----------------------------------------------------
        //
        // Weapon poses live on `upperbody`, masked to spine_up. Every one of
        // these asserts BOTH layers, because "the pistol is showing" and "the
        // legs are still running the right cycle" are two separate claims and
        // the interesting bugs live in the gap between them.
        var upper = Slots.UpperBody;

        Expect(f, t, "pistol, still — upper",  "Pistol_Idle", tags: new[] { ST.WeaponPistol }, slot: upper);
        Expect(f, t, "pistol, still — base",   "Idle",        tags: new[] { ST.WeaponPistol });

        Expect(f, t, "pistol, reloading — upper", "Pistol_Reload",
               tags: new[] { ST.WeaponPistol, ST.ActionReload }, slot: upper);

        Expect(f, t, "sword, still — upper",   "Sword_Idle",  tags: new[] { ST.WeaponSword }, slot: upper);
        Expect(f, t, "sword, still — base",    "Idle",        tags: new[] { ST.WeaponSword });

        // The layering claim proper: the carry pose survives movement, which
        // it could not when it lived on the full-body slot behind a
        // `PlanarSpeed < 0.2` condition.
        Expect(f, t, "pistol while jogging — base keeps the jog",
               "Jog_Fwd", speed: 3.0f, tags: new[] { ST.WeaponPistol });
        Expect(f, t, "pistol while jogging — upper keeps the pistol",
               "Pistol_Idle", speed: 3.0f, tags: new[] { ST.WeaponPistol }, slot: upper);

        Expect(f, t, "pistol while sprinting — upper still holds it",
               "Pistol_Idle", speed: 6.0f, tags: new[] { ST.WeaponPistol }, slot: upper);

        // Falling still falls through on the base layer.
        Expect(f, t, "pistol, falling falls through to base",
               "Jump", vspeed: -3f, tags: new[] { ST.WeaponPistol, T.Airborne });
        Expect(f, t, "pistol, falling — upper still holds it",
               "Pistol_Idle", vspeed: -3f, tags: new[] { ST.WeaponPistol, T.Airborne }, slot: upper);

        // Unarmed leaves the upper layer empty, so the mask stays at zero.
        Expect(f, t, "unarmed — upper resolves to nothing", "<NULL>", slot: upper);

        // ---- abilities outrank everything, by tag not by clip ------------
        Expect(f, t, "attack, unarmed",     "Punch_Jab",   tags: new[] { ST.ActionAttack });
        Expect(f, t, "attack, sword",       "Sword_Attack",
               tags: new[] { ST.ActionAttack, ST.WeaponSword });
        Expect(f, t, "roll beats sprint",   "Roll",        speed: 6.0f, tags: new[] { ST.ActionRoll });
        Expect(f, t, "interact",            "Interact",    tags: new[] { ST.ActionInteract });

        // ---- hysteresis is directional ----------------------------------
        // Entering the jog rung needs 2.0; holding it only needs 1.7. Same
        // speed, two answers, depending on what was already playing.
        var cold = Resolve(t, 1.85f, 0f, Array.Empty<StringName>(), null);
        var held = Resolve(t, 1.85f, 0f, Array.Empty<StringName>(),
                           FindRule(t, "Jog_Fwd"));

        if (cold?.Clip != "Walk")
            f.Add(new("ERROR", $"hysteresis (cold at 1.85): expected Walk, got {cold?.Clip}"));
        if (held?.Clip != "Jog_Fwd")
            f.Add(new("ERROR", $"hysteresis (already jogging at 1.85): expected Jog_Fwd, got {held?.Clip}"));

        // ---- every clip the packs name must exist in the vocabulary ------
        // Not a substitute for the engine check, which compares against the
        // imported library. This one catches a typo against the pack set.
        foreach (var i in RuleOverlap.FindUnsatisfiable(t)) f.Add(i);

        foreach (var i in f) GD.Print($"[std] {i}");
        GD.Print(f.Count == 0
            ? $"[std] standard content: all states resolve as expected"
            : $"[std] standard content: {f.Count} FAILURE(S)");

        return f;
    }

    // -----------------------------------------------------------------

    private static void Expect(List<Issue> f, RuleTable t, string what, string expected,
                               float speed = 0f, float vspeed = 0f, StringName[] tags = null,
                               StringName slot = null)
    {
        var got = Resolve(t, speed, vspeed, tags ?? Array.Empty<StringName>(), null,
                          slot ?? Slots.Base);
        string actual = got?.Clip.ToString() ?? "<NULL>";

        if (actual != expected)
            f.Add(new("ERROR",
                $"{what}: expected '{expected}', got '{actual}'" +
                (got is null ? "" : $" from {got.Origin}")));
    }

    private static AnimRule Resolve(RuleTable t, float speed, float vspeed,
                                    StringName[] tags, AnimRule active, StringName slot = null)
    {
        var s = new CharState();
        foreach (var tag in tags) s.Tags.Add(tag);

        s.LocalVelocity = new Vector3(0f, vspeed, speed);
        s.Grounded      = !tags.Contains(T.Airborne);
        s.GroundNormal  = Vector3.Up;
        s.Publish(1f / 60f);

        return t.ResolveRule(slot ?? Slots.Base, s, active);
    }

    private static AnimRule FindRule(RuleTable t, string clip) =>
        t.All.FirstOrDefault(r => r.Clip == clip);
}
