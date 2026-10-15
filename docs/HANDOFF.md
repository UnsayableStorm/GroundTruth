# Handoff — 2026-10-10

Read this first, then `CLAUDE.md`. Written at the end of a long session; everything below
is verified against the code or a log unless it says otherwise.

## UPDATE 2026-10-15 — the regression is fixed (read this before the rest)

Two commits on `main`, both deployed to `Mods\GroundTruthTacticalTest` and checked in game
by Jason (single player, "Tactical Test Single Player" world):

- **`869b967` power check.** Decompile confirmed the diagnosis below: the grid registers a
  block's sink only when the block joins (`MyCubeGridSystems.RegisterInSystems`), and ours
  attaches a frame later, so it is never supplied. `Instruments.SinkPowered` now asks
  `block.CubeGrid.ResourceDistributor.MaxAvailableResourceByType(ElectricityId) > 0` -
  candidate fix 3 below. **In game: the pod spins again and stops when switched off.**
  Grid power cut (Y key, whole grid) tested by Jason 2026-10-15: pods stop turning and the panes show the instruments offline.
- **`7a0374b` info pane redraw.** Found by Jason: switching an instrument off did not change
  the open terminal pane until he clicked another block and back. `RefreshCustomInfo` does
  not mark the block dirty, and the pane only redraws a dirty block.
  `GroundTruthSession.Refresh` now calls `SetDetailedInfoDirty()` too (instrument and Event
  Controller panes). **In game: the pane flips OFFLINE / back while it stays open.**

ENGINE_TRAPS 13 and 21 corrected; new traps 22 and 23 (Science copy mirrored);
reference-library `08-gotchas.md` updated.

**`cfe957a` real power draw (Jason approved 2026-10-15), NOT yet tested in game.** The sink
attaches in `InstrumentPower.Init` again - `GameLogic.Init` runs inside `MySlimBlock.Init`,
before `RegisterInSystems`, so the grid registers it. Since 2026-08-11 the instruments drew
nothing (trap 22). The old WarpDrive objection was checked against WarpDrive's source
(2640137506 `GridSystem.cs`): it listens only to grid block add/remove, split, static and
close, never to components, so sink timing cannot reach it. `EnabledChanged` now updates the
sink (off = 0 kW); the info pane shows `Power: X kW of Y kW` (MyUpgradeModule prints no power
of its own). di-review: one finding taken (guard the frame-one Update).

**WarpDrive test, owed before release** - world "Tactical Test Single Player" (loads
WarpDrive 2640137506 and the local test build): pane shows the draw and 0 when off; the grid's
power use rises by the instruments' draw; then, on a grid with a warp drive (FSD) and an
instrument, place and grind instruments, merge/split the grid (merge block, or grind a
bridging block) and save/reload; then engage the FSD. Pass = no crash, and no `WarpDrive` or
`GroundTruth` exception in the log. Claude reads the log afterwards.

`Mods\GroundTruth` (the publish folder) still holds the pre-`df29ffd` build. The release
checklist at the bottom now applies, starting at step 2 once the power-cut check is done.

## Where the release stands

`tactical` was merged into `main` (fast-forward) and **`main` is the only line now** — the
separate tactical session is retired. The mod is being renamed to **Ground Truth:
Environmental and Tactical Information Systems**, keeping the same folder
(`%APPDATA%\SpaceEngineers\Mods\GroundTruth`) and the same Workshop item (**3781444888**).

Acceptance testing passed in single player (vanilla and full modpack) and in multiplayer on
the KORRIBAN dedicated server, including: registration at load with no terminal opened on
every machine, Event Controller events firing server-side, Custom Data presets written by a
client and surviving a server restart, the tactical sensor, contact naming and tracking.

**Then a regression was found — see below. Nothing was fixed in game after it.**

### What is where

| | State |
|---|---|
| `main` | `df29ffd` — contains the offline fix that is **broken in game** |
| `Mods\GroundTruth` (publish folder) | the build from **before** `df29ffd`: everything else, but instruments never go offline. Verified identical to `main` as of the commit before `df29ffd`. `modinfo.sbmi` holds 3781444888. |
| `Mods\GroundTruthTacticalTest` (test build, Workshop 3813774178) | matches `main`, i.e. **contains the broken build** |
| GitHub (`UnsayableStorm/GroundTruth`, public) | **not pushed** — still at the pre-Tactical state. Jason has said the published docs must be accurate and exhaustive, so this push matters; ask before doing it. |

Whether Jason published the staged build to the Workshop before reporting the regression is
**not known from here** — check the item's update date before assuming either way.

## The open bug: instruments stuck OFFLINE

**Reported 2026-10-10, single player, local copy:** after `df29ffd` the instruments are
"just stuck off", and the Weather Station's rotating pod no longer spins. The Rotating
Antenna (tactical sensor) is correct and always has been.

### What `df29ffd` did

Instruments used to keep measuring on a dead grid. `UpgradeModule.IsWorking` is
enabled-and-functional only — it never consults power (ENGINE_TRAPS trap 21) — and nothing
gated readings on it anyway. So `Instruments.OfflineReason(block)` was added:

```
DAMAGED        !IsFunctional
SWITCHED OFF   !Enabled
NO POWER       !SinkPowered(block)      <-- the suspect
NOT WORKING    !IsWorking
null           operational
```

and every consumer was routed through it: `Recompute` (stops measuring and caches
`BlockState.Operational` + `OfflineReason`), the terminal properties (non-`GT_Sys` return
−1/false while offline), all four event families, every LCD app, the info pane, and
`SubpartSpinner` (which is why the pod stopped).

### Diagnosis

`SinkPowered` asks `MyResourceSinkComponent.IsPoweredByType(Electricity)`. Decompiled
2026-10-10: that flag is **only ever set in `MyResourceSinkComponent.SetInputFromDistributor`**,
which the grid's resource distributor calls for sinks it manages, and it **starts false**.

So the near-certain cause: **the grid's distributor never calls it for our sink**, so
`IsPowered` is false forever and every instrument reports `NO POWER`. Consistent with every
symptom:

- the Rotating Antenna is fine — it is a `RadioAntenna` with an engine-managed sink;
- the pod stopped — the spinner uses the same check;
- it is stuck rather than intermittent — a flag that is never written.

The likely reason the distributor ignores it: `InstrumentPower` attaches the sink in
`UpdateOnceBeforeFrame`, one frame after `Init`, **deliberately** — attaching during `Init`
mutates the grid while it is still building and tripped a crash in WarpDrive (the long note
in `InstrumentPower.cs`; do not undo that without reading it). A sink added after the block
was registered with the grid may simply never be picked up.

This also casts doubt on the older claim that the draw "enters the grid's power ledger" at
all. Treat that as unverified.

### Next step: get the data first

Do not guess at a fix. Log the sink's real numbers once per instrument, in game:

```csharp
var sink = block.Components.Get<MyResourceSinkComponent>();
// sink == null ?  RequiredInputByType / CurrentInputByType / SuppliedRatioByType /
// IsPoweredByType / MaxRequiredInputByType  for MyResourceDistributorComponent.ElectricityId
```

on a powered grid and again with the grid's power off. That says in one load whether the
sink is in the ledger at all, and which member (if any) tracks supply.

### Candidate fixes, in order of preference

1. **Use whichever sink member actually tracks supply** (`SuppliedRatioByType` or
   `CurrentInputByType`), if the log shows one does.
2. **Get the sink registered with the distributor** — but only in a way that keeps the
   WarpDrive-safe timing. Verify against that crash before shipping.
3. **Take grid power from somewhere the engine does maintain** rather than our own sink.
4. **Drop the power half.** Gate only on `Enabled` and `IsFunctional`, which demonstrably
   work, and state in the docs that an instrument keeps measuring on an unpowered grid.
   This is the honest fallback if the engine will not supply a mod-added sink on an
   `UpgradeModule`; it still fixes "switched off" and "damaged".

**To unblock play meanwhile:** in `Instruments.cs`, make `SinkPowered` `return true;`. That
restores the pre-`df29ffd` behaviour (never offline for power) while keeping the rest.

### Do not lose

Whatever the outcome, `ENGINE_TRAPS` trap 21 stands on its own evidence (the decompile of
`MyFunctionalBlock.CheckIsWorking`) and the correction to `InstrumentPower`'s August note is
right: the sink does **not** gate `IsWorking`. If option 4 is taken, trap 21 and the
INTEGRATION section "An offline instrument has no reading" both need their power claims
narrowed to match.

## Also outstanding

- **Workshop title and description.** Title → *Ground Truth: Environmental and Tactical
  Information Systems*. Description is written and ready in
  `tools/WORKSHOP_DESCRIPTION.md` (everything after the `---`); it is BBCode, paste as-is.
- **Deapri** is owed a Workshop reply: the Event Controller report (events never fired, no
  state shown) is fixed in this release. The earlier mount-point report was already
  answered and shipped.
- **Test worlds still load the test build** (3813774178), not the live item: the local
  "Tactical Test Single Player" and the KORRIBAN *Tactical Test Server*. Switch them back
  when testing ends. The KORRIBAN world also had CompleteModAssembly removed and live
  Ground Truth swapped for the test build; MES Weyland-Yutani and MES Scrapyard are absent
  **on purpose** — do not re-add them.
- **RegTimingProbe** (Workshop 3814372987) has served its purpose (runs A, B and C all
  passed) and can be removed from any world and from the Workshop whenever convenient.
- **Release logging switches** are off and should stay off for release:
  `EventSupport.Heartbeat`, `Tactical.LogContactTransitions`.

## Release checklist, once the bug is settled

1. Fix, compile (`tools\check-compile.ps1`), deploy to the test build, Jason tests in game.
2. `deploy-local.ps1` → `Mods\GroundTruth`; confirm `modinfo.sbmi` still reads 3781444888
   and the folder matches `main`.
3. Jason publishes in game, sets the title, pastes the description.
4. Push `main` to GitHub so the integration guide ships with it (ask first).
