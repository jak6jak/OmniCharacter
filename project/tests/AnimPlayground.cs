using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using CharacterKit.Animation;
using CharacterKit.Content;

namespace OmniCharacter.Tests;

// =====================================================================
// The rule table, on screen, driving a real skeleton.
//
// Everything else in this repo argues about the design on paper. This runs
// it: you move a speed slider, the table re-resolves, and the character
// changes animation — or does not, because hysteresis or MinDwell said no.
// The trace panel shows every rule that was tested and why it lost.
//
// Content comes from StandardContent.cs, which names the clips that really
// exist in the imported library rather than the invented vocabulary the
// design discussion uses.
//
// Run:
//   godot --path project res://tests/AnimPlayground.tscn
//
// The whole scene is built in code. A hand-authored .tscn would be a second
// place for the slot list and the tag vocabulary to drift out of sync.
// =====================================================================
public partial class AnimPlayground : Node3D
{
    [Export] public string CharacterScene = "res://Godot/AnimationLibrary_Godot_Standard.glb";
    [Export] public bool   DumpSceneTree  = false;

    // ---- model ----
    private AnimationPlayer _player;
    private readonly RuleTable _rules = new();
    private readonly CharState _state = new();

    private readonly ClipDriver _driver      = new();   // base / full body
    private readonly ClipDriver _upperDriver = new();   // masked upper body
    private float _maskWeight;

    // ---- input widgets ----
    private CheckButton  _airborne, _crouched;
    private OptionButton _weapon;
    private CheckButton  _holdAction;
    private StringName   _actionTag;      // momentary, cleared when its clip completes
    private Label        _actionLabel;
    private HSlider      _speed, _vspeed;
    private Label        _speedLabel, _vspeedLabel;

    // ---- readout widgets ----
    private Label           _resolved, _playing, _dwellLabel;
    private RichTextLabel   _trace;

    private static readonly StringName None = "";

    // Screenshot mode. A GUI is the one deliverable that cannot be verified by
    // its exit code, so it gets a way to prove itself:
    //   godot --path project res://tests/AnimPlayground.tscn -- --shot=C:/out.png --preset=2
    private string _shotPath;
    private int    _shotPreset;
    private int    _framesLeft = -1;
    private int    _shotFrames = 45;

    public override void _Ready()
    {
        BuildWorld();
        BuildRules();
        BuildUi();
        ReadCommandLine();
    }

    private void ReadCommandLine()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--shot="))   _shotPath   = arg["--shot=".Length..];
            if (arg.StartsWith("--preset=")) _shotPreset = arg["--preset=".Length..].ToInt();
            if (arg.StartsWith("--frames=")) _shotFrames = arg["--frames=".Length..].ToInt();
        }

        if (_shotPath is null) return;

        // Presets mirror the states StandardContentChecks asserts, so a
        // screenshot and the fixture are describing the same thing.
        switch (_shotPreset)
        {
            case 1: _speed.Value = 6.0f; break;                                  // Sprint
            case 2: _weapon.Selected = 1; break;                                 // Pistol_Idle
            case 3: _weapon.Selected = 1; _actionTag = ST.ActionReload; break;    // Pistol_Reload
            case 4: _weapon.Selected = 2; _actionTag = ST.ActionAttack; break;    // Sword_Attack
            case 5: _airborne.ButtonPressed = true; _vspeed.Value = -4f; break;  // Jump
            case 6: _weapon.Selected = 1; _speed.Value = 3.0f; break;            // falls through to Jog_Fwd
            case 7: _weapon.Selected = 2; break;                                 // Sword_Idle, imports non-looping
            case 8: _speed.Value = 3.0f; _actionTag = ST.ActionAttack; break;    // punch while jogging, must resume
        }

        _framesLeft = _shotFrames;   // let layout settle and the blend land
    }

    // =================================================================
    // Scene
    // =================================================================
    private void BuildWorld()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode      = Godot.Environment.BGMode.Color,
                BackgroundColor     = new Color("#1b1f27"),
                AmbientLightSource  = Godot.Environment.AmbientSource.Color,
                AmbientLightColor   = new Color("#8f9bb3"),
                AmbientLightEnergy  = 0.55f,
            }
        });

        var key = new DirectionalLight3D { LightEnergy = 1.6f, ShadowEnabled = true };
        key.RotationDegrees = new Vector3(-42, -125, 0);
        AddChild(key);

        var floor = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(24, 24) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("#2a3038") },
        };
        AddChild(floor);

        var cam = new Camera3D { Fov = 42f };
        cam.Position = new Vector3(2.3f, 1.45f, 2.7f);
        cam.LookAtFromPosition(cam.Position, new Vector3(0f, 0.98f, 0f), Vector3.Up);
        AddChild(cam);
        cam.Current = true;

        if (!ResourceLoader.Exists(CharacterScene))
        {
            GD.PushError($"character scene not found: {CharacterScene}");
            return;
        }

        var rig = GD.Load<PackedScene>(CharacterScene).Instantiate();
        AddChild(rig);

        _player = FindPlayer(rig);
        if (_player is null)
        {
            GD.PushError($"no AnimationPlayer under {CharacterScene}");
            return;
        }

        // Root motion would walk the character out of frame while the slider
        // says it is jogging on the spot. The playground is about which clip
        // resolves, not about locomotion, so it stays off.
        _player.RootMotionTrack = default;

        ApplyLoopIntent();
        BuildAnimationTree(rig);

        if (DumpSceneTree) Dump(rig, 0);
        GD.Print($"[playground] AnimationPlayer at {rig.GetPathTo(_player)} " +
                 $"with {_player.GetAnimationList().Length} clips");
    }

    // =================================================================
    // Layering.
    //
    // `LayerDef.BoneMask` has been carried through the design since the first
    // draft and never applied — the long-standing gap. Without it a weapon
    // pose has nowhere to go but the full-body slot, which is why the pistol
    // and sword idles used to carry `PlanarSpeed < 0.2` conditions: they could
    // only be shown standing still, and the weapon vanished the moment you
    // moved.
    //
    // Applying it needs an AnimationTree rather than an AnimationPlayer,
    // because the mask is a filter on a blend node:
    //
    //     base  -> [AnimationNodeTransition] --\
    //                                           [Blend2, filtered to spine_up] -> out
    //     upper -> [AnimationNodeTransition] --/
    //
    // A Transition node per layer rather than a bare AnimationNodeAnimation,
    // because Transition has an xfade and AnimationNodeAnimation does not —
    // switching clips would otherwise snap. Inputs are generated from the
    // clips the packs actually name, so adding a rule adds an input.
    // =================================================================
    private AnimationTree            _tree;
    private AnimationNodeTransition  _baseTrans, _upperTrans;
    private readonly Dictionary<StringName, int> _baseInputs  = new();
    private readonly Dictionary<StringName, int> _upperInputs = new();

    private void BuildAnimationTree(Node rig)
    {
        var skeleton = FindSkeleton(rig);
        if (skeleton is null) { GD.PushError("no Skeleton3D in rig"); return; }

        var blend = new AnimationNodeBlendTree();

        _baseTrans  = AddLayer(blend, "base",  Slots.Base,      _baseInputs,  new Vector2(-400, -100));
        _upperTrans = AddLayer(blend, "upper", Slots.UpperBody, _upperInputs, new Vector2(-400,  120));

        // The mask itself. Filtered paths take their pose from input 1 (the
        // upper layer); everything else passes through from input 0.
        var mask = new AnimationNodeBlend2 { FilterEnabled = true };
        foreach (var path in MaskPaths(rig, skeleton, "spine_up"))
            mask.SetFilterPath(path, true);

        blend.AddNode("mask", mask, new Vector2(-100, 0));
        blend.ConnectNode("mask", 0, "base");
        blend.ConnectNode("mask", 1, "upper");
        blend.ConnectNode("output", 0, "mask");

        _tree = new AnimationTree { TreeRoot = blend, RootNode = new NodePath("..") };
        rig.AddChild(_tree);

        foreach (var libName in _player.GetAnimationLibraryList())
            _tree.AddAnimationLibrary(libName, _player.GetAnimationLibrary(libName));

        // The player and the tree cannot both drive the same skeleton.
        _player.Stop();
        _tree.Active = true;

        GD.Print($"[playground] AnimationTree built — base inputs {_baseInputs.Count}, " +
                 $"upper inputs {_upperInputs.Count}, mask covers " +
                 $"{MaskPaths(rig, skeleton, "spine_up").Count} bones");
    }

    /// One Transition node fed by an AnimationNodeAnimation per distinct clip
    /// the packs name for this slot.
    private AnimationNodeTransition AddLayer(AnimationNodeBlendTree blend, string id,
                                             StringName slot,
                                             Dictionary<StringName, int> inputs, Vector2 at)
    {
        var clips = StandardSet.All()
            .SelectMany(p => p.Rules)
            .Where(r => r.Slot == slot)
            .Select(r => r.Clip)
            .Distinct()
            .ToList();

        var trans = new AnimationNodeTransition { XfadeTime = 0.18f };
        trans.Set("input_count", clips.Count);

        // Added before anything connects to it — ConnectNode resolves both
        // endpoints by name and fails silently-ish on a node that is not in
        // the tree yet.
        blend.AddNode(id, trans, at);

        for (int i = 0; i < clips.Count; i++)
        {
            var node = new AnimationNodeAnimation { Animation = clips[i] };
            string nodeName = $"{id}_{i}";
            blend.AddNode(nodeName, node, at + new Vector2(-260, i * 34));

            trans.Set($"input_{i}/name", clips[i].ToString());
            blend.ConnectNode(id, i, nodeName);

            inputs[clips[i]] = i;
        }

        return trans;
    }

    /// Every animation-track path under the masked bone, in the form the
    /// filter expects: "Rig/Skeleton3D:DEF-spine.002".
    ///
    /// The mask name is content, not framework — "spine_up" means nothing
    /// without a rig to resolve it against. Resolving it here rather than in
    /// the design keeps rig knowledge out of the pack.
    private static List<NodePath> MaskPaths(Node rig, Skeleton3D skeleton, string mask)
    {
        string rootBone = mask switch
        {
            "spine_up" => "DEF-spine.002",   // hips and lower spine stay with locomotion
            _          => null,
        };

        var paths = new List<NodePath>();
        if (rootBone is null) return paths;

        int start = skeleton.FindBone(rootBone);
        if (start < 0) { GD.PushError($"mask '{mask}': no bone '{rootBone}'"); return paths; }

        string prefix = rig.GetPathTo(skeleton);

        for (int i = 0; i < skeleton.GetBoneCount(); i++)
        {
            // Walk up to see whether this bone descends from the mask root.
            for (int b = i; b >= 0; b = skeleton.GetBoneParent(b))
                if (b == start) { paths.Add(new NodePath($"{prefix}:{skeleton.GetBoneName(i)}")); break; }
        }

        return paths;
    }

    private static Skeleton3D FindSkeleton(Node n)
    {
        if (n is Skeleton3D s) return s;
        foreach (var c in n.GetChildren())
            if (FindSkeleton(c) is Skeleton3D found) return found;
        return null;
    }

    /// Push each rule's `Loops` intent onto the imported Animation resource.
    ///
    /// `Sword_Idle` imports from the .glb with LoopMode.None — an idle that
    /// plays once and stops. Nothing in the import pipeline knows it is a
    /// cycle; only the pack that named it does. Without this the character
    /// freezes after 1.67s of standing there with a sword.
    ///
    /// Doing it at runtime is a playground convenience. Shipping content
    /// should fix the import instead, which is why AnimHarness reports the
    /// mismatch rather than letting this quietly paper over it.
    private void ApplyLoopIntent()
    {
        foreach (var pack in StandardSet.All())
        foreach (var rule in pack.Rules)
        {
            if (!_player.HasAnimation(rule.Clip)) continue;

            var anim = _player.GetAnimation(rule.Clip);
            var want = rule.Loops ? Godot.Animation.LoopModeEnum.Linear
                                  : Godot.Animation.LoopModeEnum.None;

            if (anim.LoopMode != want) anim.LoopMode = want;
        }
    }

    /// Resolve one layer and push the result into its Transition node.
    ///
    /// "Is it still playing" used to come from AnimationPlayer.IsPlaying(). An
    /// AnimationTree has no per-layer equivalent, so it is derived from the
    /// clip's own length — which is more honest anyway: the driver was always
    /// asking "has this clip run out", and IsPlaying() was a proxy for it.
    private void DriveLayer(ClipDriver driver, StringName slot,
                            AnimationNodeTransition trans,
                            Dictionary<StringName, int> inputs, float dt)
    {
        bool running = driver.Active is null
                    || driver.Active.Loops
                    || driver.TimeInClip < ClipLength(driver.Active.Clip);

        driver.Tick(_rules, slot, _state, dt, running);

        if (driver.ShouldPlay && inputs.ContainsKey(driver.Active.Clip))
            // .ToString() matters: transition_request is a String property and
            // handing it a StringName trips a type-mismatch every switch.
            _tree.Set($"parameters/{(slot == Slots.Base ? "base" : "upper")}/transition_request",
                      driver.Active.Clip.ToString());
    }

    private float ClipLength(StringName clip) =>
        _player.HasAnimation(clip) ? (float)_player.GetAnimation(clip).Length : 1f;

    private static AnimationPlayer FindPlayer(Node n)
    {
        if (n is AnimationPlayer p) return p;
        foreach (var c in n.GetChildren())
            if (FindPlayer(c) is AnimationPlayer found) return found;
        return null;
    }

    private static void Dump(Node n, int depth)
    {
        GD.Print($"{new string(' ', depth * 2)}{n.Name} : {n.GetType().Name}");
        foreach (var c in n.GetChildren()) Dump(c, depth + 1);
    }

    // =================================================================
    // Rules
    // =================================================================
    private void BuildRules()
    {
        _rules.DebugCapture = true;

        foreach (var pack in StandardSet.All())
            _rules.Register(pack.Id, pack.Order, pack.Rules);

        // Same gate the director runs at install. If the content is wrong the
        // playground should say so rather than quietly play the wrong clip.
        foreach (var pack in StandardSet.All())
            foreach (var issue in RuleOverlap.CheckDeclarations(_rules, pack))
                GD.Print($"[playground] {issue}");
    }

    // =================================================================
    // UI
    // =================================================================
    private void BuildUi()
    {
        var canvas = new CanvasLayer();
        AddChild(canvas);

        // Both panels live in one full-rect container with a spacer between
        // them. Anchoring them by hand worked for the left one and silently
        // put the right one off-screen — a container cannot make that mistake.
        var root = new MarginContainer();
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            root.AddThemeConstantOverride($"margin_{side}", 16);
        canvas.AddChild(root);

        var row = new HBoxContainer();
        root.AddChild(row);

        // ---- inputs, top left ----
        var left = Panel(row, 300);
        Heading(left, "STATE");

        _airborne = Toggle(left, "Airborne");
        _crouched = Toggle(left, "Crouched");

        _weapon = Choice(left, "Weapon", "none", "pistol", "sword");

        // Actions are momentary, not a mode. A dropdown holds its tag forever,
        // which pins a finished one-shot in its last pose while the speed
        // slider says you are jogging — an ability would have cleared the tag
        // when its phase ended. These fire and clear themselves on completion.
        Note(left, "Action  (fires once)");
        ActionButton(left, "reload",   ST.ActionReload);
        ActionButton(left, "attack",   ST.ActionAttack);
        ActionButton(left, "roll",     ST.ActionRoll);
        ActionButton(left, "interact", ST.ActionInteract);

        _holdAction = Toggle(left, "Hold action (don't auto-clear)");
        _actionLabel = Body(left, "action: none");

        (_speed,  _speedLabel)  = Slider(left, "Planar speed", 0f, 8f, 0.05f, 0f);
        (_vspeed, _vspeedLabel) = Slider(left, "Vertical speed", -8f, 8f, 0.1f, 0f);

        Note(left,
            "Speed rungs sit at 0.2 / 2.0 / 4.5 with hysteresis, so the clip " +
            "changes later coming down than going up. That gap is the point.");

        // ---- spacer ----
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        // ---- readout, top right ----
        var right = Panel(row, 380);
        Heading(right, "RESOLUTION");

        _resolved   = Body(right, "");
        _playing    = Body(right, "");
        _dwellLabel = Body(right, "");

        Heading(right, "FIRST MATCH WINS");
        _trace = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            CustomMinimumSize = new Vector2(0, 340),
            ScrollActive = false,
        };
        right.AddChild(_trace);

        Note(right, "Every rule tested this frame, in precedence order.");
    }

    // ---- widget helpers -------------------------------------------------

    private static VBoxContainer Panel(BoxContainer parent, float width)
    {
        var panel = new PanelContainer
        {
            CustomMinimumSize  = new Vector2(width, 0),
            SizeFlagsVertical  = Control.SizeFlags.ShrinkBegin,   // hug the top
        };
        parent.AddChild(panel);

        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 14);
        panel.AddChild(margin);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 7);
        margin.AddChild(box);
        return box;
    }

    private static void Heading(VBoxContainer parent, string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 11);
        l.AddThemeColorOverride("font_color", new Color("#7ec8d2"));
        parent.AddChild(l);
    }

    private static Label Body(VBoxContainer parent, string text)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeFontSizeOverride("font_size", 13);
        parent.AddChild(l);
        return l;
    }

    private static void Note(VBoxContainer parent, string text)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeFontSizeOverride("font_size", 10);
        l.AddThemeColorOverride("font_color", new Color("#8b93a4"));
        parent.AddChild(l);
    }

    private static CheckButton Toggle(VBoxContainer parent, string text)
    {
        var b = new CheckButton { Text = text };
        parent.AddChild(b);
        return b;
    }

    private void ActionButton(VBoxContainer parent, string text, StringName tag)
    {
        var b = new Button { Text = text };
        b.Pressed += () => _actionTag = tag;
        parent.AddChild(b);
    }

    private static OptionButton Choice(VBoxContainer parent, string label, params string[] items)
    {
        Note(parent, label);
        var o = new OptionButton();
        for (int i = 0; i < items.Length; i++) o.AddItem(items[i], i);
        o.Selected = 0;
        parent.AddChild(o);
        return o;
    }

    private static (HSlider, Label) Slider(VBoxContainer parent, string label,
                                           float min, float max, float step, float value)
    {
        var caption = new Label { Text = $"{label}   {value:0.00}" };
        caption.AddThemeFontSizeOverride("font_size", 11);
        caption.AddThemeColorOverride("font_color", new Color("#8b93a4"));
        parent.AddChild(caption);

        var s = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value };
        s.CustomMinimumSize = new Vector2(0, 18);
        parent.AddChild(s);

        caption.SetMeta("label", label);
        return (s, caption);
    }

    // =================================================================
    // Per frame — the same order AnimDirector.Evaluate uses.
    // =================================================================
    public override void _Process(double delta)
    {
        if (_player is null) return;
        float dt = (float)delta;

        PublishState(dt);

        // The driver is the shared logic — TransitionChecks steps this same
        // class against a simulated player. It reconciles rather than reacting
        // to transitions, so a clip that reaches its end gets re-issued instead
        // of leaving the character frozen in its last pose.
        // Each layer resolves independently. That is fine here and stops being
        // fine the moment a third layer's weight depends on what a second one
        // resolved to — see the FullBody suppression below, which is the crude
        // version of the coverage ordering this will eventually need.
        DriveLayer(_driver,      Slots.Base,      _baseTrans,  _baseInputs,  dt);
        DriveLayer(_upperDriver, Slots.UpperBody, _upperTrans, _upperInputs, dt);

        // Mask weight. The upper layer is only mixed in when it resolved to
        // something AND the base clip is not a full-body action — a sword
        // swing owns the arms, and blending a carry pose over exactly the
        // bones it is animating is what "layered wrong" looks like.
        bool suppressed = _driver.Active?.FullBody ?? false;
        float target    = _upperDriver.Active is not null && !suppressed ? 1f : 0f;

        _maskWeight = Mathf.MoveToward(_maskWeight, target, dt / 0.15f);
        _tree.Set("parameters/mask/blend_amount", _maskWeight);

        // What an ability's phase would do: the clip it asked for is finished,
        // so it releases its tag and whatever was underneath resolves again.
        // Without this the punch holds forever and locomotion never resumes.
        if ((_driver.Completed || _upperDriver.Completed) && !_holdAction.ButtonPressed)
            _actionTag = null;

        Refresh();
        MaybeShoot();
    }

    private void MaybeShoot()
    {
        if (_framesLeft < 0) return;
        if (--_framesLeft > 0) return;

        var img = GetViewport().GetTexture().GetImage();
        var err = img.SavePng(_shotPath);
        GD.Print(err == Error.Ok
            ? $"[playground] wrote {_shotPath}  (base -> {_driver.Active?.Clip}, upper -> {_upperDriver.Active?.Clip}, mask {_maskWeight:0.00})"
            : $"[playground] screenshot failed: {err}");

        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }

    private void PublishState(float dt)
    {
        foreach (var t in _state.Tags.All.ToList()) _state.Tags.Remove(t);

        if (_airborne.ButtonPressed) _state.Tags.Add(T.Airborne);
        if (_crouched.ButtonPressed) _state.Tags.Add(T.Crouched);

        var weapon = _weapon.Selected switch
        {
            1 => ST.WeaponPistol,
            2 => ST.WeaponSword,
            _ => None,
        };
        if (weapon != None) _state.Tags.Add(weapon);

        if (_actionTag is not null) _state.Tags.Add(_actionTag);

        _state.LocalVelocity = new Vector3(0f, (float)_vspeed.Value, (float)_speed.Value);
        _state.Grounded      = !_airborne.ButtonPressed;
        _state.GroundNormal  = Vector3.Up;

        _state.Publish(dt);
    }

    private void Refresh()
    {
        _speedLabel.Text  = $"Planar speed   {_state.PlanarSpeed:0.00}";
        _vspeedLabel.Text = $"Vertical speed   {_state.VerticalSpeed:0.00}";

        var active = _driver.Active;

        var upper = _upperDriver.Active;

        _resolved.Text =
            (active is null
                ? "base    (nothing)"
                : $"base    {active.Clip}   {(active.Loops ? "cycle" : "one-shot")}   [{active.Origin}]")
            + "\n" +
            (upper is null
                ? "upper   (nothing)"
                : $"upper   {upper.Clip}   [{upper.Origin}]");

        _playing.Text =
            $"mask weight  {_maskWeight:0.00}" +
            ((active?.FullBody ?? false) ? "   — upper suppressed, full-body clip" : "") +
            (active is null ? "" : $"\nbase clip  {_driver.TimeInClip:0.00} / {ClipLength(active.Clip):0.00}s");

        _actionLabel.Text = _actionTag is null
            ? "action: none"
            : $"action: {_actionTag}" + (_driver.Completed ? "  (finished)" : "");

        _dwellLabel.Text = _driver.DwellRemaining > 0f
            ? $"held by MinDwell for {_driver.DwellRemaining:0.00}s more"
            : $"{_driver.Reason}  (in clip {_driver.TimeInClip:0.0}s)";

        _trace.Text = FormatTrace();
    }

    /// The table's own decision log, coloured. `RuleTable.DebugCapture` records
    /// every rule tested for the slot and whether it won.
    private string FormatTrace()
    {
        var sb = new System.Text.StringBuilder();

        // Both slots. Each resolves through the same table independently, and
        // seeing them side by side is the clearest statement of what a slot
        // actually is.
        foreach (var slot in new[] { Slots.Base, Slots.UpperBody })
        {
            if (!_rules.LastDecision.TryGetValue(slot, out var raw)) continue;

            sb.AppendLine($"[color=#7ec8d2]{slot}[/color]");
            foreach (var line in raw.Split('\n'))
                sb.AppendLine(line.StartsWith("WIN")
                    ? $"  [color=#7ec8d2][b]{line}[/b][/color]"
                    : $"  [color=#6d7484]{line}[/color]");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
