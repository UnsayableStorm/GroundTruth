# Ground Truth

A Space Engineers mod: environmental instruments (radiation, weather, habitat, life) that
report what is measured and nothing more. Scripts are in `Data/Scripts/GroundTruth`. Read
`README.md` for what ships.

## Current work: tactical displays

The Rotating Antenna (`GT_RotatingRadarDish`) is being given a contact scan, and six LCD
apps will draw from it. Phase 1 (the scan and a detail-pane readout) is built; no LCD app yet.

## Where things stand (2026-10-04) — start here

**Phase 0 has passed its gate**, single player and dedicated server both (`docs/PHASE0_PROBES.md`
§7, §7.3, §7.4; logs in `probes/results/`). Nothing from it is owed.

**Phase 1 is built, compile-clean, NOT yet tested in game.** New: `Tactical.cs` (contact table
per mechanical group, scan, D1 detection, D2 identification, lifecycle, bounds),
`TacticalMath.cs` (pure geometry, tested by `tools/tactical-math-test/run.ps1` against the real
VRage.Math.dll). Changed: `Instruments.cs` (RoleRadar 5, CapRadar 128, dish rows marked
`SelfPowered`, orphan check skips those), `GroundTruthSession.cs` (Recompute role gate,
`WriteRadar`, `Seconds`), comment-only edits in `Rotation.cs` and `CubeBlocks_GroundTruth.sbc`.
**Phase 1 SP exit check PASSED 2026-10-04** (every item; log in `probes/results/p1-sp-client.txt`;
contact transitions are logged as `GT TACTICAL ...` lines in the game log). **Next: the DS pass.**
The test build is on the Workshop, unlisted, as **3813774178** (uploaded from
`Mods\GroundTruthTacticalTest`, whose `modinfo.sbmi` now carries that id - redeploy with
`/XF modinfo.sbmi`). Steps: with Torch stopped, swap 3781444888 for 3813774178 in
`Z:\Instance\Saves\Tactical Test Server` (`Sandbox_config.sbc` and `Sandbox.sbc`; mind the
`.sbsB5`), drop the two probes, then Jason joins, opens O's dish once, toggles B and deletes a
grid; read the client log and `Z:\Logs\Keen-<date>.log`.

**Test builds go in their own mod folder, never `Mods\GroundTruth`.** That AppData folder holds
the live item's `modinfo.sbmi` (3781444888); Phase 1 code there would ship to every subscriber
on the next main-branch publish. The test copy is `%APPDATA%\SpaceEngineers\Mods\GroundTruthTacticalTest`
(deployed with `deploy-local.ps1`'s excludes, destination changed). A world under test loads it
**instead of** Workshop Ground Truth, never beside it. For the DS it needs its own unlisted
Workshop item, uploaded from that folder.

**Known v1 bug found while building Phase 1, not fixed (outside this branch's scope):**
`Readings.Bearing`/`BearingFrom` turn counter-clockwise — a target off the right reads 270. The
Bio Scanner's published bearings are mirrored. `tools/tactical-math-test/MathTest.cs` holds the
executed evidence. `TacticalMath.Bearing` is correct; Phase 1 does not use the Readings ones.

Work is on the local branch **`tactical`** (tracks `origin/claude/bold-mayer-x6lepg`, with `main`
merged in; not pushed).

**The DS test bed** is a vanilla Torch instance on KORRIBAN, mapped as `Z:\` (= KORRIBAN
`C:\TorchServerCreative`, the creative server's slot, swapped out for testing). World
`Instance\Saves\Tactical Test Server` is the SP fixture copied straight across — an SP save loads
on a DS unchanged. Logs are at the Torch root, `Z:\Logs\Keen-<date>.log`. The probes are on the
Workshop, unlisted: RadarProbe **3812970493**, RadarActionProbe **3812970925**. A probe's
schedule is 10 minutes from world load; restart the server to get a fresh window.

**Probe code changes now need a re-upload by Jason** (in game, never steamcmd) before a DS run.
The AppData probe folders hold a `modinfo.sbmi` with the Workshop id and the repo copies do not,
so deploy with it excluded or `/MIR` deletes it and the next upload creates a duplicate item
(ENGINE_TRAPS 16):
`robocopy probes\RadarProbe "$env:APPDATA\SpaceEngineers\Mods\RadarProbe" /MIR /XF modinfo.sbmi`

**Done 2026-10-04 (on `main`, merged here):** the ten blocks with an empty `<MountPoints>` tag now mount on their single base face, sides taken from MTGraves' own definitions for the same models (Bottom; Back for the two wall weather stations). Also cleared the four "Possible entity type script logic collision" notices by moving the Weather Station spin into `InstrumentPower` (`SubpartSpinner` helper in `Rotation.cs`). Both came from a Workshop report of F11 noise.

Read these before doing anything, in this order:

1. `docs/PHASE0_PROBES.md` - the probe design, the test fixture, the log format and the
   pass/fail criteria. Section 8.1 lists what was verified and what was not.
2. `docs/ENGINE_TRAPS.md` - short, and cheaper than any one of its entries was to learn.
   Traps 9, 12, 18 and 19 matter most here.
3. `docs/TACTICAL_PLAN.md` - sections 2 and 3 (decisions already made), section 9.1 (rules
   for delegated work). The rest is for later phases.

## How the probes are run (reference; SP and DS runs are done)

1. Compile-check both probes from the repo root. This needs the game's `Bin64` folder, which
   the script expects at the path set in `tools/check-compile.ps1`:
   `.\tools\check-compile.ps1 -Source probes\RadarProbe\Data\Scripts\RadarProbe`
   `.\tools\check-compile.ps1 -Source probes\RadarActionProbe\Data\Scripts\RadarActionProbe`
2. Fix whatever it reports. The members most likely to be wrong are tabled in
   `docs/PHASE0_PROBES.md` 8.1 with the fix for each. Where a member does not exist, delete
   the call rather than substituting a guess. Commit every fix, so the repo matches what ran.
3. Copy each probe folder to `%APPDATA%\SpaceEngineers\Mods\`. Do not use
   `deploy-local.ps1` for this; it deliberately skips `probes/`.
4. Build the test grids in section 2 of the probe doc, on a copy of a world, never the live
   save. Run single player, then a dedicated server as a client. Both mods must be loaded on
   the server too, since its log is half the evidence.
5. Follow the procedures in each probe file's header comment, including the parts that need
   a person: the broadcast toggle, the dampener and seat exercises, the toolbar reload.
6. Extract the log lines with the command in the probe doc and keep the client and server
   extracts. **Stop there. Do not interpret the results or begin Phase 1.** Judging them is
   the gate review, and a failed check can change the design.

A whitelist rejection in the game log is a result, not a bug to route around. Record the
rejected member and the question it served, delete that call, and reload.

## Rules for any code in this repo

- **C# 6 only.** No local functions, tuples, `out var`, pattern matching or `is` with a
  declaration. The compile checker has passed a C# 7 feature once that the game then rejected.
- **Only APIs the mod already uses, or that a probe has cleared.** No `MyIni`, reflection,
  `System.IO` or threads.
- **Do not touch** `TerminalApi.cs`, `InstrumentPower.cs`, `SealSync.cs`, `Rotation.cs`, or
  the registration timing in `GroundTruthSession.cs`. If a step seems to need to, stop and ask.
- **`-1` means "no reading"; `0` means "measured zero".** An unavailable value is never
  stored or shown as zero.
- **A negative result needs proof the code ran** (trap 12). Log the mechanism, not just the
  outcome.
- Comments explain why, including the wrong turns, in the voice of the existing files.

## Repo conventions

- Develop on the branch you are given; never push to `main` without being asked.
- `deploy-local.ps1` must never mirror `modinfo.sbmi` or `metadata.mod`. Read its header
  before touching it; getting it wrong creates a duplicate Workshop item.
- `docs/`, `probes/` and `tools/` are excluded from the published mod.
- Create a pull request only when asked.
