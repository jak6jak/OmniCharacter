using System.Collections.Generic;
using System.Linq;
using Godot;
using CharacterKit.Animation;
using CharacterKit.Content;

namespace CharacterKit.Tests;

/// Attach to any scene and run, or call Run() from a headless test harness.
/// Nothing here needs a Character, a physics world, or a rendered frame —
/// it builds a RuleTable directly from packs and interrogates it.
public static class AnimationChecks
{
    private static AnimPack[] Packs() => new[]
    {
        BaseLocomotion.Pack(), RiflePack.Pack(), InjuryPack.Pack(), GrapplePack.Pack()
    };

    public static List<Issue> Run()
    {
        var issues = new List<Issue>();
        var packs = Packs();

        // 1. Build the table the same way AnimDirector would, without a
        //    director. Registration order matters — it breaks Order ties — so
        //    this must match the order the game installs in.
        var table = new RuleTable();
        foreach (var pack in packs) table.Register(pack.Id, pack.Order, pack.Rules);

        // Slots come from the installed layers, not a hand-written list. The
        // hand-written one named all four gameplay slots as *required*, which
        // demanded an unconditional fallback for `upperbody`, `fullbody` and
        // `add.injury` — slots that are claimed by request and are supposed to
        // be empty most of the time. The suite reported three errors against
        // content behaving exactly as designed.
        var layers = packs.SelectMany(p => p.Layers).ToList();

        var slots    = layers.Select(l => l.Slot).Distinct().ToList();
        var optional = layers.Where(l => l.Optional).Select(l => l.Slot).ToHashSet();
        var required = slots.Where(s => !optional.Contains(s)).ToList();

        // 2. Structural checks.
        var clips = packs.SelectMany(p => p.Rules.Select(r => r.Clip)).Distinct().ToList();
        issues.AddRange(RuleValidation.Validate(table, clips, required));

        // 3. Precedence. Every rule that outranks a rule from another pack,
        //    checked against what that pack declared it would outrank. This is
        //    the check that makes an insertion mistake local: it reports the
        //    captured region, not just that something changed.
        //
        //    On clean content this reports nothing — every remaining shadow is
        //    self-scoped. That silence is why ShadowFixtures runs first: a
        //    quiet check and a broken check look identical from here.
        issues.AddRange(ShadowFixtures.Run());
        issues.AddRange(ShadowFixtures.AssertInstalledContentIsClean(packs));

        foreach (var pack in packs)
            issues.AddRange(RuleOverlap.CheckDeclarations(table, pack));

        // 4. State-space sweep over a domain DERIVED FROM THE TABLE. Every tag
        //    any rule mentions, and points either side of every threshold any
        //    rule declares — including its hysteresis-widened edge. The old
        //    hand-written combo list covered whatever was remembered when it
        //    was written, which is why a golden built on it could miss a pack
        //    that introduced a threshold of its own.
        //    No manual axes. An earlier version added StrafeAngle and the two
        //    grapple scalars "for coverage" — but the sweep records which rule
        //    won, and no rule keys on any of them, so every added sample
        //    produced a byte-identical row. It multiplied the state space 16x
        //    for zero discrimination and pushed the run over its own budget.
        //    Blendspace coverage is a different question from rule coverage
        //    and wants a different test.
        var domain = SweepDomain.FromTable(table);
        issues.AddRange(domain.Issues);

        var sweep = RuleValidation.Sweep(table, slots, domain, optional);
        issues.AddRange(RuleValidation.AssertNoHoles(sweep));

        // 5. Thrash. One ramp through the walk threshold should switch twice.
        issues.AddRange(RuleValidation.RampSpeed(table, Slots.Base, new StringName[] { }));
        issues.AddRange(RuleValidation.RampSpeed(table, Slots.Base, new[] { T.WeaponRifle }));
        issues.AddRange(RuleValidation.RampSpeed(table, Slots.UpperBody, new[] { T.WeaponRifle }));

        // 6. Golden file. Diff this in CI — it is what catches a new pack
        //    silently changing an unrelated state. AnimDirector runs the same
        //    comparison inside Install so the report lands at the edit rather
        //    than at whenever somebody next runs the tests.
        var golden = RuleValidation.ToGolden(sweep);
        GD.Print($"[anim] sweep covered {sweep.Count} states " +
                 $"({domain.TagCombos.Count} tag combos x " +
                 $"{string.Join(" x ", domain.Axes.Select(a => $"{a.Value.Length}"))} samples)");

        foreach (var i in issues) GD.Print($"[anim] {i}");
        return issues;
    }

    /// Incremental view: what does adding one pack to the others actually do?
    /// Answers the question the flat table is accused of being unable to
    /// answer — "which states did this rule just capture" — for a whole pack.
    public static List<Issue> RunInstallDelta(string packId)
    {
        var packs = Packs();
        var slots = new[] { Slots.Base, Slots.UpperBody, Slots.FullBody, Slots.AddInjury };

        var with = new RuleTable();
        var without = new RuleTable();
        foreach (var p in packs)
        {
            with.Register(p.Id, p.Order, p.Rules);
            if (p.Id != packId) without.Register(p.Id, p.Order, p.Rules);
        }

        // Domain from the fuller table so both halves share keys.
        var domain = SweepDomain.FromTable(with);

        var issues = RuleValidation.DiffSweep(
            packId,
            RuleValidation.Sweep(without, slots, domain),
            RuleValidation.Sweep(with, slots, domain));

        foreach (var i in issues) GD.Print($"[anim] {i}");
        return issues;
    }

    /// Print when a clip is "not playing" and the reason lives in a pack you
    /// did not write.
    public static void DumpTable()
    {
        var table = new RuleTable();
        foreach (var p in Packs()) table.Register(p.Id, p.Order, p.Rules);
        GD.Print(RuleOverlap.Explain(table));
    }
}
