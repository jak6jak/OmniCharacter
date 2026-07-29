using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using CharacterKit.Animation;
using CharacterKit.Content;
using CharacterKit.Tests;

namespace OmniCharacter.Tests;

// =====================================================================
// In-engine harness.
//
// Most of the validation suite is scene-free on purpose and runs fine in a
// bare .NET process. Three things do not, and they are the reason this exists:
//
//   1. Clip names. `Validate` checks rule clips against a list of known clips
//      — and every caller so far built that list FROM THE PACKS THEMSELVES,
//      which makes the check tautological. Only the engine can load a real
//      AnimationLibrary and say what actually exists.
//   2. Resource loading. GD.Load paths in the packs are unverified strings
//      until something resolves them.
//   3. It proves the prototype compiles and runs against the real Godot API
//      rather than against a reading of it.
//
// Run headless:
//   godot --headless --path project res://tests/AnimHarness.tscn
//
// Exit code is the number of ERROR-severity issues, capped at 125, so this
// can gate CI without parsing stdout.
// =====================================================================
public partial class AnimHarness : Node
{
    /// Scenes or libraries to harvest real clip names from. A .glb imports as
    /// a PackedScene whose AnimationPlayer holds the libraries.
    [Export] public string[] ClipSources = { "res://Godot/AnimationLibrary_Godot_Standard.glb" };

    /// When false, missing clips are reported but do not fail the run. The
    /// packs currently name clips that no imported library provides, which is
    /// a content gap rather than a design defect.
    [Export] public bool FailOnMissingClips = false;

    public override void _Ready()
    {
        int errors = 0;

        Line();
        GD.Print("OmniCharacter — animation design harness");
        GD.Print($"engine {Engine.GetVersionInfo()["string"]}");
        Line();

        // AnimationChecks.Run already runs ShadowFixtures — every caller
        // should get the proof that the shadow check still works, not just
        // this one. Calling it here too only printed the banner twice.
        errors += Section("design checks", AnimationChecks.Run());
        errors += Section("standard content", StandardContentChecks.Run());
        errors += Section("state transitions", TransitionChecks.Run());
        errors += Section("clip metadata", CheckClipMetadata());

        var real = HarvestClips();

        // The design packs name an invented clip vocabulary and are expected
        // to miss. The standard packs are authored against this library and
        // must not, so they fail the run.
        errors += Section("clip resolution — design packs",
            CheckClipsExist(real, fail: false,
                BaseLocomotion.Pack(), RiflePack.Pack(), InjuryPack.Pack(), GrapplePack.Pack()));

        errors += Section("clip resolution — playground packs",
            CheckClipsExist(real, fail: true, StandardSet.All()));

        Line();
        GD.Print(errors == 0
            ? "PASS — no errors"
            : $"FAIL — {errors} error(s)");
        Line();

        GetTree().Quit(Math.Min(errors, 125));
    }

    // -----------------------------------------------------------------

    private static void Line() => GD.Print(new string('-', 68));

    private static int Section(string name, List<Issue> issues)
    {
        int errors = issues.Count(i => i.Severity == "ERROR");
        int warns  = issues.Count(i => i.Severity == "WARN");

        GD.Print($"\n[{name}] {errors} error(s), {warns} warning(s)");
        foreach (var i in issues.Where(i => i.Severity is "ERROR" or "WARN"))
            GD.Print($"    {i}");

        return errors;
    }

    // -----------------------------------------------------------------
    // The engine-only half.
    // -----------------------------------------------------------------

    /// Every animation name reachable from the configured sources, qualified
    /// the way AnimationPlayer names them: "library/clip", or bare for the
    /// default library.
    private Dictionary<string, string> HarvestClips()
    {
        var found = new Dictionary<string, string>();   // clip -> where it came from

        foreach (var path in ClipSources)
        {
            if (!ResourceLoader.Exists(path))
            {
                GD.PrintErr($"clip source not found: {path}");
                continue;
            }

            var res = GD.Load(path);

            switch (res)
            {
                case AnimationLibrary lib:
                    foreach (var name in lib.GetAnimationList())
                        found[name.ToString()] = path;
                    break;

                case PackedScene scene:
                    var root = scene.Instantiate();
                    foreach (var player in FindPlayers(root))
                    foreach (var libName in player.GetAnimationLibraryList())
                    {
                        var lib2 = player.GetAnimationLibrary(libName);
                        foreach (var clip in lib2.GetAnimationList())
                        {
                            string qualified = string.IsNullOrEmpty(libName.ToString())
                                ? clip.ToString()
                                : $"{libName}/{clip}";
                            found[qualified] = path;
                        }
                    }
                    root.QueueFree();
                    break;

                default:
                    GD.PrintErr($"clip source is neither AnimationLibrary nor PackedScene: {path}");
                    break;
            }
        }

        GD.Print($"\n[clip inventory] {found.Count} animation(s) across {ClipSources.Length} source(s)");
        foreach (var name in found.Keys.OrderBy(k => k))
            GD.Print($"    {name}");

        return found;
    }

    // -----------------------------------------------------------------
    // Loop mode and clip length, against the real imported resources.
    //
    // This is the check that would have caught the freeze. A pack declares
    // whether a clip is a cycle; the .glb import decides independently, and
    // for `Sword_Idle` the two disagree — it imports with LoopMode.None, so
    // standing still with a sword played once and stopped dead. Nothing in
    // the design could see that, because loop mode is not a design property.
    //
    // Also verifies the clip lengths TransitionChecks simulates against, so
    // that model cannot silently drift away from the content.
    // -----------------------------------------------------------------
    private List<Issue> CheckClipMetadata()
    {
        var issues = new List<Issue>();

        var scene = GD.Load<PackedScene>(ClipSources[0]);
        if (scene is null) return issues;

        var root = scene.Instantiate();
        var player = FindPlayers(root).FirstOrDefault();
        if (player is null) { root.QueueFree(); return issues; }

        foreach (var pack in StandardSet.All())
        foreach (var rule in pack.Rules)
        {
            string clip = rule.Clip.ToString();
            if (!player.HasAnimation(rule.Clip)) continue;

            var anim = player.GetAnimation(rule.Clip);
            bool importLoops = anim.LoopMode != Godot.Animation.LoopModeEnum.None;

            if (importLoops != rule.Loops)
                issues.Add(new("WARN",
                    $"pack '{pack.Id}': rule for '{clip}' declares Loops={rule.Loops}, " +
                    $"but the imported resource has LoopMode={anim.LoopMode}. " +
                    $"The playground overrides this at runtime; fix it in the .glb " +
                    $"import settings so shipping content does not depend on that."));

            if (TransitionChecks.ClipLengths.TryGetValue(clip, out var modelled)
                && Mathf.Abs(modelled - (float)anim.Length) > 0.02f)
                issues.Add(new("ERROR",
                    $"TransitionChecks models '{clip}' as {modelled:0.00}s, " +
                    $"but the imported clip is {anim.Length:0.00}s — the simulated " +
                    $"player is testing against the wrong content."));
        }

        root.QueueFree();
        return issues;
    }

    private static IEnumerable<AnimationPlayer> FindPlayers(Node root)
    {
        if (root is AnimationPlayer p) yield return p;
        foreach (var child in root.GetChildren())
        foreach (var nested in FindPlayers(child))
            yield return nested;
    }

    /// The check that could not be written outside the engine: do the clips
    /// the packs name actually exist anywhere?
    ///
    /// Every previous caller passed `knownClips` built by scraping the packs'
    /// own rules, so the check could only ever pass. This compares against
    /// what an imported library really contains.
    private List<Issue> CheckClipsExist(Dictionary<string, string> real, bool fail,
                                        params AnimPack[] packs)
    {
        var issues = new List<Issue>();
        string severity = fail ? "ERROR" : "WARN";

        foreach (var pack in packs)
        foreach (var clip in pack.Rules.Select(r => r.Clip.ToString()).Distinct().OrderBy(c => c))
            if (!real.ContainsKey(clip))
                issues.Add(new(severity,
                    $"pack '{pack.Id}': clip '{clip}' is not provided by any imported library"));

        if (issues.Count > 0 && !fail)
            issues.Add(new("INFO",
                "reported as warnings — these packs name a clip vocabulary that " +
                "has not been authored yet."));

        return issues;
    }
}
