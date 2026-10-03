# Ground Truth

A Space Engineers mod: environmental instruments (radiation, weather, habitat, life) that
report what is measured and nothing more. Scripts are in `Data/Scripts/GroundTruth`. Read
`README.md` for what ships.

## Current work: tactical displays

The Rotating Antenna (`GT_RotatingRadarDish`) is being given a contact scan, and six LCD
apps will draw from it. It is planned in full and nothing is built yet except the probes.

**Phase 0 is the first step and it gates everything else.** Do not start Phase 1 (the
contact scan, role 5, the displays) until the probe results have been reviewed.

## Where Phase 0 stands (2026-10-03) — start here

**Single player is done.** Every SP-judgeable check passed; results, findings and the probe
changes made during the run are in `docs/PHASE0_PROBES.md` section 7, logs in `probes/results/`.

Work is on the local branch **`tactical`** (tracks `origin/claude/bold-mayer-x6lepg`, with `main`
merged in; not pushed). Probe code is deployed to `%APPDATA%\SpaceEngineers\Mods\` by robocopy
from `probes/`. Once the probes are on the Workshop, any probe code change also needs Jason to
re-upload before a DS run, or the server runs the old build.

**Next: the dedicated-server pass**, on a new vanilla Torch instance Jason is setting up on
KORRIBAN ("Tactical Test Server"). A client and a server cannot share his PC. Steps:

1. **World.** A server-ready copy of the fixture is at
   `%APPDATA%\SpaceEngineers\Saves\76561197992886229\Tactical Test Server\`. It was rebuilt from
   the pre-edit backup, so it differs from the SP world on purpose:
   - Grids renamed with relationship tags (`RP-B NEUTRAL`, `RP-C NEUTRAL`, `RP-D ENEMY`,
     `RP-G NOBODY`, `RP-I NEUTRAL`, `RP-J NEUTRAL`); C/D/G/I re-owned to match.
   - B's antenna HUD text is `RP-B SIGNAL`, unique, for `0.4.text`.
   - O's dish Custom Data holds a `[Fixture]` section, so `0.8.preserve` is not vacuous.
   - **E is stationary**, override cleared: a server simulates before anyone joins, so a
     pre-moving E would be gone. Jason's character is seated in E's cockpit; on joining he aims E
     to pass O, sets dampeners off and a forward override, and hops out near O.
   - Mods list holds Ground Truth (3781444888) only. **The two probes must be uploaded to the
     Workshop (private/unlisted, by Jason, in game — never steamcmd)** and their ids added to the
     world's `<Mods>` in both `Sandbox_config.sbc` and `Sandbox.sbc`, or to the Torch instance.
2. **Server settings.** Torch instance settings can override the world's: set **sync distance
   10000** (view distance 15000 or more) in the instance itself, not only in the world.
   Experimental mode on. Check the first `CTX` line reports `syncRadius=10000`.
3. **The run**, Jason as a client. 10-minute schedule; `/radarprobe done` ends it. Hands-on:
   B's broadcast off ~4 s and on (admin "Use terminals" — GCDW owns B); seat, Z off ~5 s, Z on,
   stand up; the 0.6 toolbar steps; `/radarprobe write`, then save, **restart the server**,
   rejoin, `/radarprobe status` (that is `0.8.persist` on a DS). A second player is optional.
4. **Logs.** Client log from `%APPDATA%\SpaceEngineers\`; server log from the Torch instance (on
   KORRIBAN logs sit at the Torch root, not the Instance folder). Extract the `RADARPROBE` lines
   of both into `probes/results/ds-client.txt` and `ds-server.txt`. One SE log can span several
   world loads: split at the last `SESSION ARMED`.
5. **Gate review** — Claude's own judgment, never delegated: fill the DS columns of section 7,
   carry each FAIL's consequence from section 5 into `TACTICAL_PLAN.md`, then decide Phase 1.

**Leftover for after Phase 0:** ten blocks have an empty `<MountPoints>` tag (all Radiation
Monitors, all Weather Stations, `GT_BioScanner`, `GT_BioScanner_S`), so the engine defaults them to
full-face mounts on all six sides. Jason's decision: give each its real single base-face mount
point from the model geometry. Deleting the empty tags is not acceptable.

Read these before doing anything, in this order:

1. `docs/PHASE0_PROBES.md` - the probe design, the test fixture, the log format and the
   pass/fail criteria. Section 8.1 lists what was verified and what was not.
2. `docs/ENGINE_TRAPS.md` - short, and cheaper than any one of its entries was to learn.
   Traps 9, 12 and 18 matter most here.
3. `docs/TACTICAL_PLAN.md` - sections 2 and 3 (decisions already made), section 9.1 (rules
   for delegated work). The rest is for later phases.

## How the probes are run (reference; SP steps 1-6 are done)

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
