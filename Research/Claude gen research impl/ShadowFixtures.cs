using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using CharacterKit.Animation;

namespace CharacterKit.Tests;

// =====================================================================
// Fixtures that prove the shadow check FIRES.
//
// Why this file exists, stated plainly, because it is easy to delete:
//
// Every other check in the suite is exercised by the shipping content —
// clips resolve, slots fill, ramps settle. The shadow check is different.
// Once the rifle's Base rule was narrowed, every remaining shadow in the
// installed content became self-scoped, so `CheckDeclarations` correctly
// reports nothing. And a check that reports nothing looks exactly the same
// whether it is working or whether it has silently become a no-op.
//
// That is the failure mode the whole design is built to refuse. `Serves`
// exists because a missing rule was silent. `Scope` exists because a
// forgotten release was silent. A validator that cannot be observed failing
// belongs in the same category, so here it is observed failing: deliberately
// broken packs with exact expected verdicts.
//
// The load-bearing case is SelfScopingIsNotAWildcard. The exemption is the
// part of RuleOverlap most likely to be wrong, and its wrong direction is
// too-generous — which would turn the entire check into a no-op without
// changing a single reported issue anywhere.
//
// These packs are deliberately self-contained rather than built from
// Content/Packs.cs. The fixture tests the checker, not the content, and it
// must not start failing because somebody legitimately retunes a threshold.
// =====================================================================
public static class ShadowFixtures
{
    // ---- fixture vocabulary, independent of the real tag constants ----
    static class F
    {
        public static readonly StringName Air     = "loco.airborne";
        public static readonly StringName Crouch  = "stance.crouch";
        public static readonly StringName Weapon  = "weapon.fixture";
        public static readonly StringName Action  = "action.fixture";
        public static readonly StringName Other   = "action.unrelated";
    }

    static readonly StringName SlotBase  = "base";
    static readonly StringName SlotUpper = "upperbody";

    // -----------------------------------------------------------------
    // A minimal stand-in for base locomotion: airborne, crouch, moving,
    // idle. Same shape as the real one, no library and no exports.
    // -----------------------------------------------------------------
    static AnimPack Ground() => new()
    {
        Id = "ground", Order = 0,
        Rules = new[]
        {
            new AnimRule { Slot = SlotBase, Require = TagQuery.All(F.Air),
                           Conditions = new[] { Condition.Below(Field.VerticalSpeed, 0f) },
                           Clip = "ground/fall" },
            new AnimRule { Slot = SlotBase, Require = TagQuery.All(F.Air),    Clip = "ground/rise" },
            new AnimRule { Slot = SlotBase, Require = TagQuery.All(F.Crouch), Clip = "ground/crouch" },
            new AnimRule { Slot = SlotBase,
                           Conditions = new[] { Condition.Above(Field.PlanarSpeed, 0.2f, hysteresis: 0.15f) },
                           Clip = "ground/move" },
            new AnimRule { Slot = SlotBase, Clip = "ground/idle" },
        }
    };

    /// The bug that was actually shipped, reduced. A weapon rule that gates on
    /// its weapon tag and a speed threshold, sits above ground, and — because
    /// PlanarSpeed is horizontal — keeps matching through a whole fall arc.
    static AnimPack Weapon(bool narrowed, params string[] shadows) => new()
    {
        Id = "weapon", Order = 100,
        Shadows = shadows,
        Rules = new[]
        {
            new AnimRule
            {
                Slot = SlotBase,
                Require = narrowed
                    ? TagQuery.All(F.Weapon).Without(F.Air, F.Crouch)
                    : TagQuery.All(F.Weapon),
                Conditions = new[] { Condition.Above(Field.PlanarSpeed, 0.2f, hysteresis: 0.15f) },
                Clip = "weapon/move"
            },
        }
    };

    // -----------------------------------------------------------------

    public static List<Issue> Run()
    {
        var f = new List<Issue>();

        UndeclaredShadowIsCaught(f);
        NarrowingTheRuleClearsIt(f);
        DeclaringTheClipClearsIt(f);
        PackWideDeclarationStillCatchesTheRest(f);
        SelfScopedRulesAreExempt(f);
        SelfScopingIsNotAWildcard(f);
        TotalBurialIsReported(f);
        DeadOnArrivalIsReported(f);
        UnsatisfiableRulesAreCaught(f);
        RampStillDetectsThrash(f);
        RampDoesNotCountAcquisition(f);

        foreach (var i in f) GD.Print($"[fixture] {i}");
        GD.Print(f.Count == 0
            ? "[fixture] check fixtures: 11/11 pass"
            : $"[fixture] check fixtures: {f.Count} FAILURE(S)");
        return f;
    }

    // === 1. The check fires on the real bug ==========================
    static void UndeclaredShadowIsCaught(List<Issue> f)
    {
        var (table, weapon) = Install(Ground(), Weapon(narrowed: false));

        ExpectShadowed(f, "undeclared shadow is caught",
            Inspect(table, weapon, RuleOverlap.Verdict.UndeclaredShadow),
            "ground/fall", "ground/rise", "ground/crouch", "ground/move", "ground/idle");
    }

    // === 2. Narrowing the rule is a real fix, not a silenced warning ==
    static void NarrowingTheRuleClearsIt(List<Issue> f)
    {
        var (table, weapon) = Install(Ground(), Weapon(narrowed: true));

        // Airborne and crouch are gone; the two locomotion clips remain
        // captured, which is the intent and is what gets declared.
        ExpectShadowed(f, "narrowing removes the airborne and crouch captures",
            Inspect(table, weapon, RuleOverlap.Verdict.UndeclaredShadow),
            "ground/move", "ground/idle");
    }

    // === 3. Declaring the two intended clips clears the check ========
    static void DeclaringTheClipClearsIt(List<Issue> f)
    {
        var (table, weapon) = Install(Ground(),
            Weapon(narrowed: true, "ground/move", "ground/idle"));

        ExpectNone(f, "narrowed + declared is clean", Inspect(table, weapon));
    }

    // === 4. A pack-wide entry must not wave through the accident =====
    //
    // This is the granularity regression test. `Shadows = ["ground"]` is the
    // coarse opt-out and is legal — but paired with the UN-narrowed rule it
    // must still be the case that clip-level entries are what a careful pack
    // writes, so the coarse form is proven to be the blunt instrument it is.
    static void PackWideDeclarationStillCatchesTheRest(List<Issue> f)
    {
        var (coarse, wCoarse) = Install(Ground(), Weapon(narrowed: false, "ground"));
        ExpectNone(f, "pack-wide entry silences everything (documented bluntness)",
            Inspect(coarse, wCoarse, RuleOverlap.Verdict.UndeclaredShadow));

        // ...whereas naming only the two intended clips leaves the three
        // accidental captures reported. This is the pair that justifies clip
        // granularity existing at all; if it ever collapses, the check has
        // regressed to the version that passed clean on a shipped bug.
        var (fine, wFine) = Install(Ground(),
            Weapon(narrowed: false, "ground/move", "ground/idle"));
        ExpectShadowed(f, "clip-level entries still catch the accidental captures",
            Inspect(fine, wFine, RuleOverlap.Verdict.UndeclaredShadow),
            "ground/fall", "ground/rise", "ground/crouch");
    }

    // === 5. Self-scoped rules need no declaration ====================
    static void SelfScopedRulesAreExempt(List<Issue> f)
    {
        var carry = new AnimPack
        {
            Id = "carry", Order = 100,
            Rules = new[]
            {
                new AnimRule { Slot = SlotUpper, Require = TagQuery.All(F.Weapon), Clip = "carry/idle" },
            }
        };

        // Higher order, outranks carry on the same slot, gated behind a tag it
        // declares in Serves. No Shadows entry, and none should be demanded.
        var ability = new AnimPack
        {
            Id = "ability", Order = 150,
            Serves = new[] { new Claim(SlotUpper, F.Action) },
            Rules = new[]
            {
                new AnimRule { Slot = SlotUpper, Require = TagQuery.All(F.Action), Clip = "ability/act" },
            }
        };

        var (table, _) = Install(carry, ability);
        ExpectNone(f, "self-scoped ability rule needs no Shadows entry",
            Inspect(table, ability));
    }

    // === 6. THE LOAD-BEARING ONE ====================================
    //
    // The exemption must key on the tag the winning RULE requires, not on the
    // pack merely having a Serves list. If it degrades to "pack declares any
    // Serves -> exempt", every shadow in the project stops being reported and
    // nothing anywhere changes to reveal it.
    static void SelfScopingIsNotAWildcard(List<Issue> f)
    {
        var carry = new AnimPack
        {
            Id = "carry", Order = 100,
            Rules = new[]
            {
                new AnimRule { Slot = SlotUpper, Require = TagQuery.All(F.Weapon), Clip = "carry/idle" },
            }
        };

        var sloppy = new AnimPack
        {
            Id = "sloppy", Order = 150,

            // Declares a claim on one tag...
            Serves = new[] { new Claim(SlotUpper, F.Action) },
            Rules = new[]
            {
                // ...but this rule is gated on an unrelated tag, so it fires in
                // states the pack never claimed. Not self-scoped. Must report.
                new AnimRule { Slot = SlotUpper, Require = TagQuery.All(F.Other), Clip = "sloppy/act" },
            }
        };

        var (table, _) = Install(carry, sloppy);
        ExpectShadowed(f, "Serves on an unrelated tag does NOT grant exemption",
            Inspect(table, sloppy, RuleOverlap.Verdict.UndeclaredShadow),
            "carry/idle");

        // Same slot, wrong slot in the claim: a Serves entry for a different
        // slot must not exempt a rule competing in this one.
        var misfiled = new AnimPack
        {
            Id = "misfiled", Order = 150,
            Serves = new[] { new Claim(SlotBase, F.Other) },   // claim is on base
            Rules = new[]
            {
                new AnimRule { Slot = SlotUpper, Require = TagQuery.All(F.Other), Clip = "misfiled/act" },
            }
        };

        var (t2, _) = Install(carry, misfiled);
        ExpectShadowed(f, "Serves on another slot does NOT grant exemption",
            Inspect(t2, misfiled, RuleOverlap.Verdict.UndeclaredShadow),
            "carry/idle");
    }

    // === 7. Declared but total is still worth a word =================
    static void TotalBurialIsReported(List<Issue> f)
    {
        var blanket = new AnimPack
        {
            Id = "blanket", Order = 200,
            Shadows = new[] { "ground" },        // declared, so not an ERROR
            Rules = new[]
            {
                new AnimRule { Slot = SlotBase, Clip = "blanket/everything" },   // terminal
            }
        };

        var (table, _) = Install(Ground(), blanket);

        // A terminal rule above the whole stack buries every ground clip.
        ExpectShadowed(f, "total burial is reported even when declared",
            Inspect(table, blanket, RuleOverlap.Verdict.TotalBurial),
            "ground/fall", "ground/rise", "ground/crouch", "ground/move", "ground/idle");
    }

    // === 8. The symmetric hazard: installed under, arrived dead ======
    static void DeadOnArrivalIsReported(List<Issue> f)
    {
        var blanket = new AnimPack
        {
            Id = "blanket", Order = 200,
            Shadows = new[] { "ground" },
            Rules = new[] { new AnimRule { Slot = SlotBase, Clip = "blanket/everything" } }
        };

        // Installed AFTER the blanket, and underneath it. Every rule is dead.
        var late = new AnimPack
        {
            Id = "late", Order = 10,
            Rules = new[]
            {
                new AnimRule { Slot = SlotBase, Require = TagQuery.All(F.Weapon), Clip = "late/never" },
            }
        };

        var (table, _) = Install(Ground(), blanket, late);
        ExpectShadowed(f, "a pack that arrives dead is told so",
            Inspect(table, late, RuleOverlap.Verdict.DeadOnArrival),
            "late/never");
    }

    // === 9. Self-contradictory rules ================================
    static void UnsatisfiableRulesAreCaught(List<Issue> f)
    {
        var t = new RuleTable();
        t.Register("broken", 0,
            new AnimRule { Slot = SlotBase, Require = TagQuery.All(F.Air).Without(F.Air),
                           Clip = "broken/tags" },
            new AnimRule { Slot = SlotBase,
                           Conditions = new[]
                           {
                               Condition.Above(Field.PlanarSpeed, 5f),
                               Condition.Below(Field.PlanarSpeed, 2f),
                           },
                           Clip = "broken/range" });

        var got = RuleOverlap.FindUnsatisfiable(t).Count;
        if (got != 2)
            f.Add(new("ERROR", $"unsatisfiable rules are caught: expected 2, got {got}"));
    }

    // === 10 & 11. RampTest, both directions ==========================
    //
    // RampTest was counting the layer's initial acquisition — the move from
    // no clip at all to the first one — as a switch, so every clean ramp
    // reported three transitions against a limit of two and the check failed
    // on hysteresis that was working correctly. Fixing a validator's
    // sensitivity is exactly when it needs a fixture in both directions:
    // one proving it still fires, one proving it no longer cries wolf.

    /// A rule matching a BAND rather than a half-line. A monotonic ramp up and
    /// back down crosses in, out, in, out — four switches through one rule,
    /// which is thrash and must be reported.
    static void RampStillDetectsThrash(List<Issue> f)
    {
        var t = new RuleTable();
        t.Register("thrash", 0,
            new AnimRule
            {
                Slot = SlotBase, MinDwell = 0f,
                Conditions = new[]
                {
                    Condition.Above(Field.PlanarSpeed, 2f),
                    Condition.Below(Field.PlanarSpeed, 4f),
                },
                Clip = "thrash/band"
            },
            new AnimRule { Slot = SlotBase, MinDwell = 0f, Clip = "thrash/idle" });

        var got = RuleValidation.RampSpeed(t, SlotBase, Array.Empty<StringName>());
        if (got.Count == 0)
            f.Add(new("ERROR",
                "RampTest still detects thrash: a band rule ramped through twice " +
                "should exceed the switch limit, but nothing was reported"));
    }

    /// The same table with one clean threshold and hysteresis. Up once, down
    /// once — two switches plus an acquisition, which must NOT be reported.
    static void RampDoesNotCountAcquisition(List<Issue> f)
    {
        var t = new RuleTable();
        t.Register("clean", 0,
            new AnimRule
            {
                Slot = SlotBase,
                Conditions = new[] { Condition.Above(Field.PlanarSpeed, 2f, hysteresis: 0.5f) },
                Clip = "clean/move"
            },
            new AnimRule { Slot = SlotBase, Clip = "clean/idle" });

        var got = RuleValidation.RampSpeed(t, SlotBase, Array.Empty<StringName>());
        if (got.Count != 0)
            f.Add(new("ERROR",
                "RampTest does not count acquisition: a clean single-threshold ramp " +
                $"should pass, but reported — {got[0].Message}"));
    }

    // -----------------------------------------------------------------
    // Harness. Deliberately tiny — there is no test framework in this repo
    // and adding one is a bigger decision than this file should make.
    // -----------------------------------------------------------------

    /// Registers packs in the given order, returns the table and the last pack.
    /// Registration order matters: it breaks Order ties, so it is part of what
    /// is under test.
    static (RuleTable, AnimPack) Install(params AnimPack[] packs)
    {
        var t = new RuleTable();
        foreach (var p in packs) t.Register(p.Id, p.Order, p.Rules);
        return (t, packs[^1]);
    }

    static List<RuleOverlap.Violation> Inspect(
        RuleTable t, AnimPack pack, RuleOverlap.Verdict? only = null)
    {
        var all = RuleOverlap.Inspect(t, pack);
        return only is null ? all : all.Where(v => v.Kind == only).ToList();
    }

    static void ExpectShadowed(List<Issue> f, string what,
                               List<RuleOverlap.Violation> got, params string[] expected)
    {
        var actual = got.Select(v => v.Loser.Clip.ToString()).OrderBy(x => x).ToList();
        var want   = expected.OrderBy(x => x).ToList();

        if (actual.SequenceEqual(want)) return;

        f.Add(new("ERROR",
            $"{what}:\n        expected [{string.Join(", ", want)}]\n" +
            $"        got      [{string.Join(", ", actual)}]"));
    }

    static void ExpectNone(List<Issue> f, string what, List<RuleOverlap.Violation> got)
    {
        if (got.Count == 0) return;

        f.Add(new("ERROR",
            $"{what}: expected no violations, got " +
            string.Join(", ", got.Select(v => $"{v.Kind}({v.Loser.Clip})"))));
    }

    // -----------------------------------------------------------------
    /// Baseline against the real shipping content: it must be clean. Kept
    /// separate from the fixtures above because it fails for a different
    /// reason — content drift rather than a regression in the checker.
    public static List<Issue> AssertInstalledContentIsClean(params AnimPack[] packs)
    {
        var t = new RuleTable();
        foreach (var p in packs) t.Register(p.Id, p.Order, p.Rules);

        return packs
            .SelectMany(p => RuleOverlap.CheckDeclarations(t, p))
            .Where(i => i.Severity == "ERROR")
            .ToList();
    }
}
