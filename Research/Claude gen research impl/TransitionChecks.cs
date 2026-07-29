using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using CharacterKit.Animation;
using CharacterKit.Content;

namespace CharacterKit.Tests;

// =====================================================================
// State changes over time, and what should be playing after each one.
//
// StandardContentChecks asks "given this state, which rule wins" — one
// frozen frame at a time, with no player and no history. It passed
// completely while the character was visibly stuck, because the rule was
// resolving correctly the whole time. What was wrong lived in the gap
// between the winning rule and the thing playing it: a clip would reach its
// end, the player would stop, and nothing re-issued it.
//
// So this test runs a clock. It steps a simulated AnimationPlayer at 60Hz
// through a scripted sequence of input changes and asserts what is playing —
// including, crucially, at times long after the current clip's own length.
// A test that never advances past one clip length cannot see this class of
// bug at all.
//
// The player is simulated rather than real so the whole thing runs headless
// in a fraction of a second. Clip lengths and loop modes are the values
// probed out of the imported library, and AnimHarness cross-checks them
// against the real resources so the model cannot quietly drift.
// =====================================================================
public static class TransitionChecks
{
    // Measured from res://Godot/AnimationLibrary_Godot_Standard.glb.
    // Loop flags here are the DESIGN intent, taken from AnimRule.Loops.
    public static readonly Dictionary<string, float> ClipLengths = new()
    {
        ["Idle"]          = 2.50f,
        ["Walk"]          = 1.33f,
        ["Jog_Fwd"]       = 0.93f,
        ["Sprint"]        = 0.67f,
        ["Crouch_Idle"]   = 2.93f,
        ["Crouch_Fwd"]    = 2.00f,
        ["Jump"]          = 2.50f,
        ["Jump_Start"]    = 1.33f,
        ["Roll"]          = 1.47f,
        ["Punch_Jab"]     = 0.87f,
        ["Pistol_Idle"]   = 1.67f,
        ["Pistol_Reload"] = 1.67f,
        ["Sword_Idle"]    = 1.67f,
        ["Sword_Attack"]  = 1.53f,
        ["Interact"]      = 2.00f,
    };

    public static List<Issue> Run()
    {
        var f = new List<Issue>();
        var s = new Sim(f);

        // ---- the reported bug, stated first -----------------------------
        //
        // "once an animation is done playing it can get stuck on that
        // animation even when changing various settings". Idle is 2.5s; run
        // well past that and it must still be running.
        s.Advance(0.05f);
        s.Expect("cold start resolves to idle", "Idle");

        s.Advance(8f);
        s.Expect("idle still running after 3+ clip lengths", "Idle", playing: true);
        s.ExpectRestarts("a looping clip is re-issued, not left stopped", atLeast: 2);

        // ---- the exact symptom: nudging within one speed rung ------------
        s.Set(speed: 3.0f);
        s.Advance(0.1f);
        s.Expect("speed 3.0 selects the jog rung", "Jog_Fwd");

        s.Advance(5f);                       // 5+ lengths of a 0.93s clip
        s.Expect("jog still running long after its own length", "Jog_Fwd", playing: true);

        s.Set(speed: 3.4f);                  // same rung — resolution unchanged
        s.Advance(0.5f);
        s.Expect("nudging inside a rung does not stall playback", "Jog_Fwd", playing: true);

        // ---- Sword_Idle: imports with LoopMode None ----------------------
        //
        // The pack says this is a cycle. The imported resource says otherwise.
        // Honoring the rule is what keeps it moving; if ClipDriver ever stops
        // reconciling, this is the assertion that goes red.
        s.Set(speed: 0f, weapon: ST.WeaponSword);
        s.Advance(0.2f);
        s.ExpectUpper("sword equipped and standing", "Sword_Idle");
        s.Expect("base is still just idling underneath", "Idle");

        s.Advance(6f);
        s.ExpectUpper("sword idle survives past its own length", "Sword_Idle");
        s.ExpectMask("and is mixed in", visible: true);

        // ---- one-shots hold, and do not machine-gun ---------------------
        s.Set(weapon: Sim.Clear, action: ST.ActionRoll);
        s.Advance(0.2f);
        s.Expect("roll wins over everything", "Roll");
        s.ClearRestarts();                   // the looping clips above are not this segment

        s.Advance(3f);                       // Roll is 1.47s
        s.Expect("a finished one-shot holds its last pose", "Roll", playing: false);
        s.ExpectRestarts("a one-shot is never re-issued while held", atMost: 0);

        // ...and the state change still lands. This is the half of the report
        // that says "even when changing various settings".
        s.Set(action: Sim.Clear);
        s.Advance(0.2f);
        s.Expect("clearing the action leaves the held pose", "Idle", playing: true);

        // ---- a one-shot fired WHILE MOVING ------------------------------
        //
        // The reported case. Everything above triggers actions from a standstill,
        // so "locomotion is still underneath and must come back" was never
        // actually asserted.
        s.Set(speed: 3.0f, action: Sim.Clear);
        s.Advance(0.3f);
        s.Expect("jogging before the action", "Jog_Fwd");

        s.Set(action: ST.ActionAttack);
        s.Advance(0.2f);
        s.Expect("punch interrupts the jog", "Punch_Jab");

        s.Advance(2f);                       // Punch_Jab is 0.87s
        s.Expect("punch holds while the input is still held", "Punch_Jab", playing: false);

        s.Set(action: Sim.Clear);
        s.Advance(0.3f);
        s.Expect("releasing the action resumes locomotion", "Jog_Fwd", playing: true);

        // and the same at walking speed
        s.Set(speed: 1.0f);
        s.Advance(0.3f);
        s.Expect("walking", "Walk");

        s.Set(action: ST.ActionAttack);
        s.Advance(1.5f);
        s.Expect("punch from a walk, finished", "Punch_Jab", playing: false);

        s.Set(action: Sim.Clear);
        s.Advance(0.3f);
        s.Expect("releasing resumes the walk", "Walk", playing: true);

        // ---- and the same thing without an explicit release -------------
        //
        // The playground originally drove actions from a dropdown, which holds
        // its tag forever — so a finished punch kept winning and locomotion
        // never came back. Nothing in the design ends a one-shot; an ability's
        // phase does, and it needs to be told the clip is over.
        // ClipDriver.Completed is that signal.
        s.ReleaseActionOnComplete = true;

        s.Set(speed: 3.0f, action: Sim.Clear);
        s.Advance(0.3f);
        s.Expect("jogging again", "Jog_Fwd");

        s.Set(action: ST.ActionAttack);
        s.Advance(0.2f);
        s.Expect("punch fires", "Punch_Jab");

        s.Advance(2f);
        s.Expect("punch releases itself and locomotion resumes", "Jog_Fwd", playing: true);

        s.Set(speed: 1.0f, action: ST.ActionRoll);
        s.Advance(2.5f);
        s.Expect("roll releases itself and the walk resumes", "Walk", playing: true);

        s.ReleaseActionOnComplete = false;

        // ---- the full ladder, up and down -------------------------------
        s.Set(speed: 1.0f);  s.Advance(0.3f); s.Expect("ladder up: walk",   "Walk");
        s.Set(speed: 3.0f);  s.Advance(0.3f); s.Expect("ladder up: jog",    "Jog_Fwd");
        s.Set(speed: 6.0f);  s.Advance(0.3f); s.Expect("ladder up: sprint", "Sprint");
        s.Set(speed: 4.2f);  s.Advance(0.3f);
        s.Expect("hysteresis holds sprint below its entry threshold", "Sprint");
        s.Set(speed: 3.0f);  s.Advance(0.3f); s.Expect("ladder down: jog",  "Jog_Fwd");
        s.Set(speed: 0f);    s.Advance(0.3f); s.Expect("ladder down: idle", "Idle");

        // ---- air ---------------------------------------------------------
        s.Set(airborne: true, vspeed: 3f);
        s.Advance(0.3f);
        s.Expect("rising", "Jump_Start");

        s.Advance(2f);                       // Jump_Start is a 1.33s one-shot
        s.Expect("rising pose holds at the top", "Jump_Start", playing: false);

        s.Set(vspeed: -3f);
        s.Advance(0.3f);
        s.Expect("falling switches to the loop", "Jump", playing: true);

        s.Advance(4f);
        s.Expect("fall loop keeps running", "Jump", playing: true);

        s.Set(airborne: false, vspeed: 0f);
        s.Advance(0.3f);
        s.Expect("landing returns to idle", "Idle", playing: true);

        // ---- MinDwell actually defers a switch ---------------------------
        s.Set(action: ST.ActionRoll);
        s.Advance(0.05f);
        s.Expect("roll starts", "Roll");

        s.Set(action: Sim.Clear);                 // cancel almost immediately
        s.Advance(0.1f);                     // Roll's MinDwell is 0.4s
        s.Expect("MinDwell keeps the roll while the input is already gone", "Roll");

        s.Advance(0.5f);
        s.Expect("and releases it once dwell expires", "Idle");

        // ---- weapon swap mid-motion -------------------------------------
        // ---- LAYERING: the reported case --------------------------------
        //
        // The weapon pose used to live on the full-body slot behind a
        // `PlanarSpeed < 0.2` condition, so it could only ever be shown
        // standing still — the pistol vanished the instant you moved. It now
        // lives on `upperbody`, masked to spine_up, and these assert both
        // halves separately: the legs keep the locomotion cycle AND the arms
        // keep the weapon.
        s.Set(speed: 3.0f, weapon: ST.WeaponPistol);
        s.Advance(0.3f);
        s.Expect     ("jogging with a pistol — legs keep the jog", "Jog_Fwd");
        s.ExpectUpper("jogging with a pistol — arms keep the pistol", "Pistol_Idle");
        s.ExpectMask ("so the mask is mixed in while moving", visible: true);

        s.Set(speed: 6.0f);
        s.Advance(0.3f);
        s.Expect     ("sprinting — legs move to sprint", "Sprint");
        s.ExpectUpper("sprinting — arms still hold the pistol", "Pistol_Idle");

        s.Set(speed: 0f);
        s.Advance(0.3f);
        s.Expect     ("standing — base returns to idle", "Idle");
        s.ExpectUpper("standing — pistol still held", "Pistol_Idle");

        s.Set(action: ST.ActionReload);
        s.Advance(0.3f);
        s.ExpectUpper("reload runs on the upper body", "Pistol_Reload");
        s.Expect     ("while the base layer keeps idling", "Idle");

        s.Advance(2.5f);
        s.ExpectUpper("reload holds when finished", "Pistol_Reload");

        s.Set(action: Sim.Clear);
        s.Advance(0.3f);
        s.ExpectUpper("and returns to the weapon idle", "Pistol_Idle");

        // ---- LAYERING: a full-body clip suppresses the upper layer -------
        //
        // A sword swing owns the arms. Blending a carry pose over exactly the
        // bones the swing is animating is what "layered wrong" looks like.
        s.Set(speed: 0f, weapon: ST.WeaponSword, action: Sim.Clear);
        s.Advance(0.3f);
        s.ExpectUpper("sword carry pose", "Sword_Idle");
        s.ExpectMask ("mixed in while idle", visible: true);

        s.Set(action: ST.ActionAttack);
        s.Advance(0.3f);
        s.Expect     ("swing takes the whole body", "Sword_Attack");
        s.ExpectMask ("and suppresses the carry pose", visible: false);

        s.Set(action: Sim.Clear);
        s.Advance(0.4f);
        s.ExpectMask ("which comes back when the swing ends", visible: true);

        // Unarmed leaves the layer empty, so nothing is mixed in at all.
        s.Set(weapon: Sim.Clear);
        s.Advance(0.3f);
        s.ExpectMask("unarmed mixes in nothing", visible: false);

        foreach (var i in f) GD.Print($"[transitions] {i}");
        GD.Print(f.Count == 0
            ? $"[transitions] {s.Checks}/{s.Checks} transitions correct " +
              $"over {s.Elapsed:0.0}s of simulated playback"
            : $"[transitions] {f.Count} FAILURE(S)");

        return f;
    }

    // =================================================================
    // A simulated AnimationPlayer plus the real ClipDriver.
    //
    // Only the player is fake. The rule table, the resolution and the
    // driver are the same objects the playground runs, so a fix here is a
    // fix there.
    // =================================================================
    private sealed class Sim
    {
        private const float Dt = 1f / 60f;

        private readonly List<Issue> _failures;
        private readonly RuleTable   _rules = new();
        private readonly CharState   _state = new();
        private readonly ClipDriver  _driver = new();       // base
        private readonly ClipDriver  _upper  = new();       // masked upper body

        private readonly HashSet<StringName> _tags = new();
        private float _speed, _vspeed;
        private bool  _airborne;

        private string _current;         // what the base layer is running
        private float  _clipTime;
        private bool   _playing;

        private string _upperCurrent;
        private float  _upperClipTime;
        private bool   _upperPlaying;

        private int _restarts;

        /// Models what an AbilityPhase does: release the action tag once the
        /// clip it asked for has finished. Off by default so the "held
        /// forever" case above still asserts holding.
        public bool ReleaseActionOnComplete;

        public int   Checks  { get; private set; }
        public float Elapsed { get; private set; }

        public Sim(List<Issue> failures)
        {
            _failures = failures;
            foreach (var p in StandardSet.All()) _rules.Register(p.Id, p.Order, p.Rules);
        }

        /// Passed for weapon/action to mean "clear this axis". Distinct from
        /// null, which means "leave it alone" — conflating the two made every
        /// Set() that did not mention the weapon silently unequip it.
        public static readonly StringName Clear = "<clear>";

        public void Set(float? speed = null, float? vspeed = null, bool? airborne = null,
                        StringName weapon = null, StringName action = null)
        {
            if (speed    is not null) _speed    = speed.Value;
            if (vspeed   is not null) _vspeed   = vspeed.Value;
            if (airborne is not null) _airborne = airborne.Value;

            // The GUI dropdowns are one-of, so setting one replaces whatever
            // was on that axis rather than accumulating tags forever.
            Replace("weapon.", weapon);
            Replace("action.", action);
        }

        private void Replace(string prefix, StringName tag)
        {
            if (tag is null) return;                       // leave this axis alone
            _tags.RemoveWhere(t => t.ToString().StartsWith(prefix, StringComparison.Ordinal));
            if (tag != Clear) _tags.Add(tag);
        }

        public void Advance(float seconds)
        {
            int steps = Mathf.Max(1, Mathf.RoundToInt(seconds / Dt));
            for (int i = 0; i < steps; i++) Step();
            Elapsed += steps * Dt;
        }

        private void Step()
        {
            // --- publish state, exactly as the playground does ---
            foreach (var t in _state.Tags.All.ToList()) _state.Tags.Remove(t);
            if (_airborne) _state.Tags.Add(T.Airborne);
            foreach (var t in _tags) _state.Tags.Add(t);

            _state.LocalVelocity = new Vector3(0f, _vspeed, _speed);
            _state.Grounded      = !_airborne;
            _state.GroundNormal  = Vector3.Up;
            _state.Publish(Dt);

            // --- advance the simulated player ---
            if (_playing)
            {
                _clipTime += Dt;
                float len = ClipLengths.TryGetValue(_current ?? "", out var l) ? l : 1f;

                if (_clipTime >= len)
                {
                    // A looping resource would wrap here. The point of the bug
                    // is that not every resource loops — Sword_Idle imports
                    // with LoopMode None — so the sim stops and makes the
                    // driver responsible for noticing.
                    _playing  = false;
                    _clipTime = len;
                }
            }

            if (_upperPlaying)
            {
                _upperClipTime += Dt;
                float ul = ClipLengths.TryGetValue(_upperCurrent ?? "", out var u) ? u : 1f;
                if (_upperClipTime >= ul) { _upperPlaying = false; _upperClipTime = ul; }
            }

            // --- the code under test ---
            _driver.Tick(_rules, Slots.Base,      _state, Dt, _playing);
            _upper .Tick(_rules, Slots.UpperBody, _state, Dt, _upperPlaying);

            if (_driver.ShouldPlay)
            {
                if (_driver.IsRestart) _restarts++;
                _current  = _driver.Active.Clip.ToString();
                _clipTime = 0f;
                _playing  = true;
            }

            if (_upper.ShouldPlay)
            {
                _upperCurrent  = _upper.Active.Clip.ToString();
                _upperClipTime = 0f;
                _upperPlaying  = true;
            }
            else if (_upper.Active is null)
            {
                _upperCurrent = null;
                _upperPlaying = false;
            }

            if (ReleaseActionOnComplete && (_driver.Completed || _upper.Completed))
                _tags.RemoveWhere(t => t.ToString().StartsWith("action.", StringComparison.Ordinal));
        }

        /// The mask weight the playground computes: the upper layer only mixes
        /// in when it resolved to something AND the base clip is not a
        /// full-body action.
        public bool UpperVisible =>
            _upper.Active is not null && !(_driver.Active?.FullBody ?? false);

        public void ExpectUpper(string what, string clip)
        {
            Checks++;

            string actual = _upper.Active?.Clip.ToString() ?? "<none>";
            if (actual != clip)
                _failures.Add(new("ERROR",
                    $"{what}: upper layer expected '{clip}', got '{actual}' " +
                    $"(driver: {_upper.Reason})"));
        }

        public void ExpectMask(string what, bool visible)
        {
            Checks++;

            if (UpperVisible != visible)
                _failures.Add(new("ERROR",
                    $"{what}: upper layer should be {(visible ? "mixed in" : "suppressed")}, " +
                    $"but base='{_driver.Active?.Clip}' (FullBody={_driver.Active?.FullBody}) " +
                    $"and upper='{_upper.Active?.Clip}'"));
        }

        public void Expect(string what, string clip, bool? playing = null)
        {
            Checks++;

            if (_current != clip)
            {
                _failures.Add(new("ERROR",
                    $"{what}: expected '{clip}' to be playing, got '{_current ?? "<none>"}' " +
                    $"(driver: {_driver.Reason})"));
                return;
            }

            if (playing is not null && _playing != playing.Value)
                _failures.Add(new("ERROR",
                    $"{what}: '{clip}' should be {(playing.Value ? "running" : "stopped and holding")}, " +
                    $"but it is {(_playing ? "running" : "stopped")} (driver: {_driver.Reason})"));
        }

        /// Forget re-issues counted so far, so the next ExpectRestarts talks
        /// about one segment rather than everything since the last assertion.
        public void ClearRestarts() => _restarts = 0;

        /// Restart accounting since the last ExpectRestarts or ClearRestarts.
        public void ExpectRestarts(string what, int atLeast = 0, int atMost = int.MaxValue)
        {
            Checks++;

            if (_restarts < atLeast)
                _failures.Add(new("ERROR",
                    $"{what}: expected at least {atLeast} re-issue(s), saw {_restarts}"));
            else if (_restarts > atMost)
                _failures.Add(new("ERROR",
                    $"{what}: expected at most {atMost} re-issue(s), saw {_restarts}"));

            _restarts = 0;
        }
    }
}
