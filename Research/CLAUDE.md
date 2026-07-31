# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this directory is

`Research/` is the design-exploration folder for **OmniCharacter**, a GDExtension (C++, Godot 4.5+) that provides a drag-and-drop bipedal character animation system. Nothing in this folder ships. It exists to settle the architecture *before* it is written in C++.

The parent repo root is one level up (`../`) and currently still holds the unmodified `godot-cpp-template` skeleton (`ExampleClass`, `example_library_init` → renamed to `OmniCharacter_library_init`). None of the real system exists in C++ yet.

## Hard constraints

- **Do not write C++ for this project.** The author's explicit rule: no actual C++ is to be authored by a bot. Design, prototype in C#, critique, document, and answer questions — but the `../src/*.cpp` / `*.h` implementation is written by hand by the author. If a task seems to require emitting C++, stop and ask.
- **Do not edit `BrainStorm`** (the extensionless file in this folder — the author refers to it as "Brainstorm.md"). It is the author's own headspace dump. Read it for intent; never modify it.
- C# files here are **design prototypes, not shipping code.** They encode the shape of the design in a language that reads faster than C++, and the C++ implementation is still written by hand from them. They *do* now compile and run: `../project/OmniCharacter.csproj` compiles this folder in place (see "Running the harness"), so the files the design discussion refers to are the files the harness actually executes. Keep them runnable — a design check that cannot be run is a design check nobody trusts.

## Layout

| Path | What it is |
|---|---|
| `BrainStorm` | Author's raw design notes. Read-only. Duplicated near-verbatim at the top of `../README.md`. |
| `Claude gen research impl/` | The current worked prototype (C#). Its `README.md` is the design rationale document — **read it first**. |
| `Abilityruntime.cs`, `Rulevalidation.cs` | Earlier standalone drafts of two files that now live inside `Claude gen research impl/` in fuller form. Superseded; prefer the versions in the subfolder. |

## The architecture under design

The whole system is one pipeline, run once per physics frame:

```
Input → Intent → MotorStack (velocity) → CharState (facts) → AnimDirector (resolve) → pose
```

Concepts, and the files that define them (all under `Claude gen research impl/`):

- **Tags** (`AnimCore.cs`) — discrete facts about the character: equipped weapon, active action, status, locomotion. Deliberately *never* continuous. Continuous values live in `CharState`'s namespaced scalar bag instead.
- **CharState** (`AnimCore.cs`) — the single published fact set: tags, local velocity, predicted trajectory, turn rate, plus pack-authored scalars. This is the only contract feature authors have to satisfy.
- **Rules / RuleTable / Condition** (`AnimCore.cs`) — data-driven filters mapping state to clips. `Condition` is plain data (not lambdas) specifically so packs can later become editor-authored Godot `Resource`s. `Hysteresis` and `MinDwell` prevent thrash.
- **AnimPack** (`AnimDirector.cs`) — the extensibility unit: `Id`, `Order`, `Requires`, library, layers, rules, scalar bindings, `Serves` claims. Higher `Order` *shadows* lower rather than replacing, so a partial pack (a pistol with only an idle and a reload) falls through to base for everything else, and uninstalling restores base for free.
- **AnimDirector** (`AnimDirector.cs`) — slot arbitration by priority, then per-layer rule resolution, then drives blend parameters.
- **Motors** (`Motors.cs`) — `IMotor` (owns movement: ground, dash, grapple) stacked with `IVelocityContributor` (influences it: wind, slow field). Top motor wins.
- **Abilities** (`AbilityRuntime.cs`) — scoped phases. A phase *declares* what it holds; the `Scope` acquires on entry and releases on every exit including `_ExitTree`. This exists so there is no `Cleanup()` a feature author can forget — a leaked slot handle is a permanently stuck animation.
- **Validation** (`RuleValidation.cs`, `AnimationChecks.cs`) — scene-free checks: `Validate` (missing clips, no-fallback slots, unreachable rules), `Sweep`/`AssertNoHoles` (states resolving to nothing → T-pose), `ToGolden` (a new pack silently changing an unrelated state), `RampTest` (thrash across frames — the only check that can exercise hysteresis and `MinDwell`).

The governing principle, stated in the prototype README: **the framework owns what is dangerous to get wrong; gameplay owns what is interesting to get right.** Resource lifetime, phase transitions, slot arbitration → framework. Rope math, what counts as an anchor, recovery windows → the ability.

Known-open gaps, carried deliberately: inertialization (`SwitchClip` is still a crossfade — flagged as the biggest single quality win), `LayerDef.BoneMask` carried but never applied, stride/orientation warping absent, packs constructed in code rather than as `[Export]`ed Resources.

## Parent project commands

Run from the repo root (`..`), not from `Research/`.

```shell
git submodule update --init --recursive   # required once; godot-cpp is pinned to branch 4.3
scons                                     # build; auto-copies the lib into project/bin/<platform>/
scons target=template_release
scons compiledb=yes compile_commands.json # regenerate the clangd DB without building
```

CMake is an alternative to SCons (`CMakeLists.txt`); both produce the same `OmniCharacter` shared library and both copy it into `project/bin/<platform>/`.
Scons is preferd when building project.

## Running the harness

The design prototypes compile into the Godot project as C# and run headless.
`project/OmniCharacter.csproj` pulls `Research/Claude gen research impl/**/*.cs`
in from where it sits, so there is one source of truth.

```shell
dotnet build project/OmniCharacter.csproj

# Godot must be a .NET (mono) build. 4.6.2 is what this has been run against.
GODOT="/c/Users/Jacob/Documents/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe"
"$GODOT" --headless --path project --import                       # once, or after adding assets
"$GODOT" --headless --path project res://tests/AnimHarness.tscn   # exit code = error count
```

`project/tests/AnimHarness.cs` runs the fixtures, the design checks, and the
one check that needs an engine: resolving pack clip names against the animation
libraries actually imported into the project.

### The playground

```shell
"$GODOT" --path project res://tests/AnimPlayground.tscn
```

A windowed scene: the mannequin from `Godot/AnimationLibrary_Godot_Standard.glb`
driven by the rule table, with GUI controls for tags (airborne, crouched,
weapon, action) and the two continuous fields rules key on (planar and vertical
speed). The right panel shows the winning clip, its pack, the MinDwell hold, and
the full first-match trace for the frame.

Content comes from `Research/Claude gen research impl/StandardContent.cs`,
authored against clips that exist. `StandardContentChecks.cs` asserts 19 states
resolve as expected, so the GUI is not the only thing verifying it.

Screenshot mode, for checking it without a display:

```shell
"$GODOT" --path project --resolution 1280x720 res://tests/AnimPlayground.tscn \
  -- --shot=C:/absolute/out.png --preset=6      # presets 0-6, see ReadCommandLine
```

Notes:
- Node-derived prototype classes compile but are **not** attachable in a scene — Godot can only bind a script it can name with a `res://` path, and these live outside `project/`. The harness drives the plain classes directly.
- `project.godot` declares feature `4.7` while the available mono build is 4.6.2. It opens and runs; if that stops being true, the feature list is the thing to look at.
- Adding `[dotnet]` to `project.godot` is what enables C#. The GDExtension C++ library and the C# assembly coexist in the same project.
Notes that bite:
- `libname` is set in `SConstruct` and `LIBNAME` in `CMakeLists.txt`; the entry symbol `OmniCharacter_library_init` must stay in sync with `project/bin/OmniCharacter.gdextension` and `src/register_types.cpp`.
- `compatibility_minimum = "4.5"` in the `.gdextension`, but the godot-cpp submodule tracks branch `4.3`. `project/project.godot` declares feature `4.7`.
- New classes must be added to `GDREGISTER_CLASS` in `src/register_types.cpp`; docs come from `doc_classes/*.xml` and are only compiled into `editor`/`template_debug` targets.
- There is no C++ test suite. CI (`.github/workflows/ci.yml`) only verifies that the extension builds across platforms; the clang-format lint step is commented out. `.clang-format` is present and is the style of record. The C# design harness above is separate and is not in CI yet.
