# Phase 0 — Probe Design

Status: **SP pass complete 2026-10-03 (section 7); DS pass next** (see the end of section 8). This is the specification the probe code was written from
(`TACTICAL_PLAN.md` §9.3: probe design is Opus, probe code is Sonnet). It defines what each
probe logs, what counts as a pass, and what each result changes in the plan.

Two traps shape every line of it:

- **Trap 12** — a test that never ran your code is not a negative result. Every probe logs
  the *mechanism* running, separately from the *outcome*, so "the API returned nothing" and
  "the probe never got that far" can never look the same.
- **Trap 9** — compiling is not the same as being delivered. A whitelisted API that returns
  empty, zero or stale data is a failure, and a probe that only checks "no exception" cannot
  see it. Every value is compared against something independent.

---

## 1. Shape

Two probe mods, split by risk rather than by topic.

| Mod | Probes | Touches the world? |
|---|---|---|
| `probes/RadarProbe` | 0.1 detection & velocity, 0.2 grid groups, 0.3 ownership, 0.4 broadcasters, 0.5 class & size, 0.7 ship controller | **No.** Read-only. |
| `probes/RadarActionProbe` | 0.6 toolbar actions, 0.8 Custom Data sync | Yes: injects one action onto dishes, writes a `[RadarProbe]` section into dish Custom Data |

The split exists so the read-only probe can run on any world without a second thought, and
so a failure in the terminal-touching probe can be removed without losing the others.

**Run both on a copy of a world, never the live save.** Nothing here is expected to damage
anything, but 0.6 injects into terminal action lists, and this mod's history with terminal
lists (`TerminalApi.cs`) is reason enough.

### 1.1 Whitelist rejections are results, not blockers

In SE a mod's scripts compile as one unit, so one prohibited member stops the whole mod. The
game log names the member. **Procedure:** record the rejected member in §7 as a FAIL for the
question it served, delete that call, reload. Two cheap filters come first:

- `tools/check-compile.ps1` catches members that **do not exist** (wrong name, wrong
  signature). It cannot catch members that exist but are **prohibited** — only the game does.
- The compile checker currently has its source folder hard-coded to
  `Data/Scripts/GroundTruth`. It needs a `-Source` parameter to check a probe. That is a
  one-line tooling change and the first task of the build (§8).

### 1.2 Where the probes must run

| Pass | Why | Logs to collect |
|---|---|---|
| **SP** — single player | The client is the server; the easiest environment, and the control for the others | client log |
| **DS** — dedicated server, you joined as a client | Streaming, sync and client-side physics are where this plan can actually fail | client log **and** the server's log |
| **DS + second player** *(optional)* | Only 0.3 and 0.8 benefit; skip if not practical | the second client's log too |

Every log line carries the side it came from, so the three logs can be merged and read as
one timeline.

---

## 2. The test fixture

One world, built once, reused by every Phase 0 probe and later by the Phase 1 exit check.

**Grid names are the fixture's declaration.** Every test grid is named `RP-<letter> ` followed
by tags saying what it is supposed to be. The probe parses the tags and checks the engine's
answer against them. That does two things: the probe can print its own verdicts, which is
what makes the logs cheap to read, and a mis-built fixture shows up as a disagreement instead
of silently skewing a result.

| Grid | Name | What it is |
|---|---|---|
| Observer | `RP-O OWN LARGE ROTOR` | Your large ship. Working, powered `GT_RotatingRadarDish`, a cockpit, a rotor with a small subgrid on it. Parked. |
| A | `RP-A OWN LARGE ROTOR SILENT` | A second ship of yours with a rotor-mounted subgrid, no antenna broadcasting, no beacon. **About 3 km from O**, so only the ownership rule can make it visible. |
| B | `RP-B OTHER SMALL BCAST` | A small ship **not owned by you**, with a working antenna, broadcast **on**, radius large enough to reach O. |
| C | `RP-C OTHER LARGE SILENT` | A large ship not owned by you. Antenna present, broadcast **off**, no beacon. **About 4 km from O** — outside the dark-return range, so Phase 1 must not see it. |
| D | `RP-D OTHER STATIC` | A station (static grid) not owned by you. |
| E | `RP-E OWN SMALL MOVING` | A small ship in space with dampeners off and a forward thruster override, so it is accelerating or at max speed throughout the run. Place it so it passes O rather than leaving range within the first minute. |
| F | `RP-F PROJ` | A projector on O or A projecting any blueprint. The projection is the thing under test. |
| G | `RP-G OTHER SMALL DOCKED` | A small ship docked by **connector** to D. |
| H | `RP-H OWN FAR` | Any grid placed **2 km beyond** the world's sync distance from O. |
| I | `RP-I OTHER SMALL BEACON` | A small ship not owned by you, with a working **beacon** and no antenna, more than 2 km from O. |
| J | `RP-J OTHER SMALL SILENT NEAR` | A small ship not owned by you, nothing broadcasting, **within 1 km of O** — the dark-return case. |

**Ownership for "OTHER".** Use whatever is easiest in your world — an NPC faction grid
(pirate or trader encounter), a grid owned by a second account, or unowned debris. Add the
relationship you expect as a tag: `ENEMY`, `NEUTRAL`, `FRIEND` or `NOBODY`. If you do not know
it, leave the tag off; that check reports INCONCLUSIVE instead of guessing.

Keep the whole fixture except H inside sync distance of O and outside every grid's own
rotor reach, so nothing collides during a run. The distances on A, C, I and J matter: they
separate the three detection rules of D1 so each one is tested alone.

---

## 3. Log format

Every line starts with `RADARPROBE`, so one search extracts everything and nothing else.

```
RADARPROBE <side> <probe> <key=value ...>
RADARPROBE <side> VERDICT <probe>.<check> PASS|FAIL|INCONCLUSIVE <reason>
RADARPROBE <side> ALIVE <probe> sample=<n>
```

- `<side>` is `SP`, `SERVER` or `CLIENT`, decided once at load:
  `SP` when multiplayer is not active; otherwise `SERVER` when `Multiplayer.IsServer`, else
  `CLIENT`.
- `ALIVE` is written **every sample, before anything that could throw**. A probe with no
  `ALIVE` lines proved nothing (trap 12).
- Every probe body is wrapped so an exception logs
  `RADARPROBE <side> <probe> THREW <type>: <message>` and the other probes still run.
- `VERDICT` lines are conveniences, not evidence. The raw lines they were computed from are
  always logged beside them, and the gate review reads both.

**Reading the logs** needs no model. On Windows:

```powershell
Select-String -Path "$env:APPDATA\SpaceEngineers\SpaceEngineers_*.log" -Pattern 'RADARPROBE' |
  ForEach-Object { $_.Line } | Set-Content radarprobe-client.txt
```

and the same against the dedicated server's log folder. Only those extracts go in front of
a model.

---

## 4. Schedule

- First sample 10 s after load (600 ticks), past the loading screen — the pattern
  `HudMarkerProbe` uses.
- Then **every 2 s for 30 samples** (one minute), then stop. A bounded run keeps the log
  readable and the probe harmless if forgotten in a mod list.
- A chat command, `/radarprobe`, re-runs one sample on demand, on the machine where it is
  typed. It cannot drive the server; the server runs its schedule regardless.

---

## 5. The probes

Each probe states its question, what it logs, how it decides, and what each outcome does to
the plan. The pass criteria are the part that must not be softened during the build.

### 0.1 — Remote grids and their velocity, as a client sees them

**Question.** Does `MyAPIGateway.Entities.GetEntitiesInSphere` return remote grids on a DS
client, and is their `Physics.LinearVelocity` real there — not zero, not stale?

**Logs**, per sample:
- the sphere radius used (the world's sync distance) and the total entity count returned
- the returned entities counted by kind: grids, characters, voxels, floating objects, other
- per grid: `EntityId`, name, `Physics == null`, `LinearVelocity` length, distance to O
- per grid, from the **previous** sample: measured speed = distance moved ÷ time elapsed.
  This is the independent check. `LinearVelocity` is what the engine claims; the position
  delta is what actually happened.
- the query's cost in milliseconds (`System.Diagnostics.Stopwatch`; if the whitelist rejects
  it, apply §1.1 and drop the timing — it informs §6 of the plan but decides nothing)

**Verdicts**

| Check | PASS | FAIL | INCONCLUSIVE |
|---|---|---|---|
| `0.1.grids` | every fixture grid inside sync distance is returned | any is missing | fixture grid not found by name |
| `0.1.velocity` | E's `LinearVelocity` within 10% of its measured speed, on every side | reported ≈ 0 or differs > 10% while measured speed > 5 m/s | E not moving (measured < 5 m/s) |
| `0.1.subgrids` | *(recorded, not judged)* whether rotor subgrids come back as separate entities | — | — |
| `0.1.streaming` | *(recorded, not judged)* whether H is returned, per side | — | — |

**What each outcome changes**
- `velocity` FAIL on CLIENT only → contacts take velocity from position deltas between
  scans, and Target Track states that closing rate is derived and lags by one scan interval.
- `grids` FAIL on CLIENT → the whole client-side model in plan §7 is wrong; stop and redesign
  around a server scan sent to clients before anything else is built.
- `streaming`: H present on the server and absent on the client confirms the stated range
  limit. H present on the client would mean the limit is looser than stated — note it.

### 0.2 — Grid groups

**Question.** Can mod code resolve a grid's mechanical group, and does it exclude connector
links?

**Logs**, per fixture grid and per link type (`Mechanical`, `Physical`), using **both**
available routes so one whitelist rejection does not end the question:
- `IMyCubeGrid.GetGridGroup(GridLinkTypeEnum.X).GetGrids(list)`
- `MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.X, list)`

For each: member `EntityId`s, member count, and each member's block count (to decide what
the contact's stable id should be — see below).

**Verdicts**

| Check | PASS | FAIL |
|---|---|---|
| `0.2.rotor` | A resolves to 2 grids under `Mechanical`, from either member | 1 grid, or rejected |
| `0.2.connector` | G is **not** in D's `Mechanical` group but **is** in its `Physical` group | G in D's `Mechanical` group |
| `0.2.control` | B (no subgrids) resolves to exactly 1 grid | anything else |
| `0.2.routes` | *(recorded)* which of the two routes compiled and agreed | — |

**Also decided here:** the contact id. The plan says "root grid `EntityId`", and a group has
no root. Proposed rule: the member with the **most blocks**, ties to the lowest `EntityId`.
The probe logs block counts so the gate can confirm the largest member is the hull, not a
turret on a rotor.

**What each outcome changes**
- Both routes rejected → collapse groups by walking mechanical connection blocks (rotor,
  hinge, piston tops) on each grid by hand. More code in `Tactical.cs`; same behaviour.
- `connector` FAIL → docked ships are merged into one contact, and plan §10 changes to say so.

### 0.3 — Ownership and relationship

**Question.** Can the client determine a target's owner and its relationship to the dish's
owner?

**Logs**, per fixture grid:
- `BigOwners` (all ids), the dish's `OwnerId`, and the local player's identity id
- the relation from the dish owner to one functional block on the target, via
  `IMyCubeBlock.GetUserRelationToOwner(dishOwnerId)`
- the factions route: each side's faction tag via `Session.Factions.TryGetPlayerFaction`,
  and `GetRelationBetweenFactions` where both have one

**Verdicts**

| Check | PASS | FAIL | INCONCLUSIVE |
|---|---|---|---|
| `0.3.own` | O, A and E report `Owner` / `FactionShare` | anything else | — |
| `0.3.other` | each `OTHER` grid's relation matches its tag | mismatch | no relation tag in the name |
| `0.3.sides` | CLIENT and SERVER agree for every grid | they disagree | server log not supplied |

**What each outcome changes**
- The block route passes and the faction route disagrees → use the block route only; it
  already handles unowned grids and players without a faction.
- `sides` FAIL → the client lacks ownership data; relationship becomes a server-sent field
  or `UNKNOWN` everywhere on clients. That is a real cost to D2 and goes back to you before
  Phase 1.

### 0.4 — Broadcasters

**Question.** Can the client read whether a target is broadcasting, and how far?

**Logs**, per fixture grid, for every antenna **and every beacon** on it: `IsWorking`,
`Enabled`, `EnableBroadcasting` (antennas), `Radius`, `HudText`, `ShowShipName`, and the
distance from that block to O. `HudText` and `ShowShipName` are what D2 names a broadcasting
contact from, so they must read correctly on the client. Plus `IsBroadcasting` if that member exists — `check-compile.ps1` answers that
before the game is started; if it does not exist, remove it.

**Procedure:** during the DS run, toggle B's broadcast off in its terminal (from your
client), wait 4 s, toggle it on again. The toggle is part of the test.

**Verdicts**

| Check | PASS | FAIL |
|---|---|---|
| `0.4.state` | B broadcasting and in range; C and J not broadcasting | any wrong |
| `0.4.text` | B's `HudText` on the CLIENT matches what the vanilla HUD shows for B | differs or empty |
| `0.4.toggle` | the SERVER log shows B's broadcast flip within 4 s of the toggle | no flip on the server |
| `0.4.beacon` | I's beacon reads as working, with its radius, on every side | unreadable, or unreadable on the client |

**What each outcome changes**
This probe carries the detection model: under D1, broadcast state decides whether most
contacts exist at all.

- `state` or `toggle` FAIL on the CLIENT → clients cannot see broadcast state reliably, so
  they cannot apply D1. Stop. The likely redesign is a server-side broadcast check sent to
  clients; it goes back to you before Phase 1.
- `text` FAIL → broadcasting contacts are named by grid name instead, which reveals more
  than the HUD does. That is a D2 decision for you, not a silent fallback.
- `beacon` FAIL → beacons cannot count on clients, and the beacon decision comes back to you.

### 0.5 — Class and size

**Question.** Are static/dynamic, grid size and real dimensions readable, and does the
projection exclusion rule work?

**Logs**, per fixture grid: `IsStatic`, `GridSizeEnum`, `LocalAABB` extents, `WorldAABB`
extents, `Min`/`Max` cell bounds with the size they imply — `(Max − Min + 1) × GridSize` —
and `Physics == null`.

**Verdicts**

| Check | PASS | FAIL |
|---|---|---|
| `0.5.class` | D static; B, E, G, I small; the rest large — all matching their tags | any mismatch |
| `0.5.size` | `LocalAABB` extents within one block of the cell-bound size on every axis | worse |
| `0.5.projection` | F (the projection) has `Physics == null` **or** is not returned by the sphere at all | F returned with physics |
| `0.5.aabb` | *(recorded)* how much larger `WorldAABB` is than `LocalAABB` on a rotated ship | — |

**What each outcome changes**
- `projection` FAIL → a different discriminator is needed before Phase 1; projections would
  otherwise appear as contacts.
- `aabb` decides the Dimensions source. The expectation is `LocalAABB`: `WorldAABB` grows as
  a ship rotates, and a contact must not appear to change size because it turned.

### 0.6 — Toolbar actions on one block *(RadarActionProbe)*

**Question.** Can `CustomActionGetter` put *Next / Previous / Clear* actions on the dish
alone, without registering them against every antenna, and do they survive a reload?

**Mechanism**, mirroring how `PanelControls` injects controls: subscribe to
`MyAPIGateway.TerminalControls.CustomActionGetter`; when the block is a
`GT_RotatingRadarDish`, create the actions with `CreateAction<IMyTerminalBlock>` and add them
**to the list the handler was given**. Never call `AddAction`.

**Logs**
- every handler call: block subtype, action count before and after, whether ours were added
- a **baseline**: on the first call for a *vanilla* antenna, the full list of its action ids;
  on every later vanilla call, whether that list is still identical
- inside each action's delegate: `RADARPROBE <side> 0.6 EXECUTED <action> on <entityId>`.
  This is the mechanism line; an action that "did nothing" with no EXECUTED line was never
  called

**Procedure**
1. In a cockpit on O, open the toolbar config. Find the dish; check the three probe actions
   are listed. Find a vanilla antenna; check they are **not**.
2. Put *Next* on the toolbar. Press it twice.
3. Save, exit to menu, reload. Press it again **before opening any terminal or toolbar
   screen**.
4. Repeat 1–3 as a client on the DS.

**Verdicts**

| Check | PASS | FAIL |
|---|---|---|
| `0.6.scoped` | actions on the dish, absent on the vanilla antenna | present on vanilla |
| `0.6.execute` | an EXECUTED line per press | presses with no line |
| `0.6.reload` | the toolbar slot still works after reload, before any terminal is opened | slot empty, greyed or dead until a terminal is opened |
| `0.6.vanilla` | the vanilla antenna's action list never changes from baseline | any change — **stop, remove the probe, record it** |
| `0.6.side` | *(recorded)* which side the delegate executes on during the DS run | — |

**What each outcome changes**
- `vanilla` FAIL → toolbar actions are dropped from the plan permanently, and the result is
  written up as an ENGINE_TRAPS entry.
- `reload` FAIL → toolbar actions are dropped for now; D4 ships with the dropdown only.
- `side` decides where selection writes happen, together with 0.8.

### 0.7 — Ship controller telemetry *(RadarProbe)*

**Question.** Do the ship-controller members the Navigation app needs exist, and do they
agree with the vanilla HUD?

**Logs**, from the cockpit on O, every sample:
`TryGetPlanetElevation` for both `Sealevel` and `Surface` (value and return flag),
`GetNaturalGravity` and `GetArtificialGravity` (length in m/s² and in g),
`GetShipSpeed`, `GetShipVelocities` linear and angular, `DampenersOverride`, and whether the
seat is occupied.

The same numbers are also shown on screen with `ShowNotification` for 2 s, so a single
screenshot holds both the probe's values and the HUD's. That screenshot is the independent
check.

**Procedure:** sample on a planet surface, then flying, then in space. Toggle dampeners
once. Leave the seat once while it samples.

**Verdicts**

| Check | PASS | FAIL |
|---|---|---|
| `0.7.altitude` | agrees with the HUD altitude to the metre; returns false in space | disagrees, or true in space |
| `0.7.gravity` | agrees with the HUD gravity reading | disagrees |
| `0.7.dampeners` | flips when toggled | does not |
| `0.7.unoccupied` | values still update with nobody seated | zeros or frozen |

`0.7.altitude` and `0.7.gravity` are judged from the screenshot, so they are written up by
hand at the gate, not by the probe.

**What each outcome changes**
- `unoccupied` FAIL → the Navigation app says "NO PILOT" rather than showing frozen values.

### 0.8 — Custom Data written by mod code, on a client *(RadarActionProbe)*

**Question.** When mod code on a DS client sets a dish's `CustomData`, does it reach the
server and other clients, and does it survive a save?

**Mechanism.** Typing `/radarprobe write` on a client writes a fresh stamp —
`[RadarProbe]` / `Stamp = <random>` — into every dish on O, **preserving everything outside
that section**. The server **polls** dish Custom Data every second and logs the first sample
in which each new stamp appears, with the delay. It does **not** rely on
`CustomDataChanged` — trap 18 records that event not reaching a mod handler on a dedicated
server.

**Logs**
- client: `WROTE stamp=<x>` with the Custom Data length before and after
- server and any second client: `SEEN stamp=<x> after=<seconds>`
- both: whether text outside the section survived byte for byte

**Procedure:** write from your client. Optionally write from a second player who has
access to O, and from one who does not. Save the server, restart it, rejoin, and check the
stamp is still there.

**Verdicts**

| Check | PASS | FAIL |
|---|---|---|
| `0.8.reach` | server SEEN within 2 s | never seen |
| `0.8.preserve` | text outside the section unchanged | changed or lost |
| `0.8.persist` | stamp present after server restart | gone |
| `0.8.access` | *(recorded)* what happens when a player without access writes | — |

**What each outcome changes**
- `reach` FAIL → selection travels as a mod network message to the server, which writes the
  Custom Data itself. `SealSync.cs` already runs this mod's networking and is the pattern.
  Not free, but understood.
- `access`: if a player without access can change the selection, the selection write must
  check terminal access itself.

---

## 6. What this design changed in D1 and D2

Designing 0.4 exposed that the vanilla HUD reveals **beacons** as well as broadcasting
antennas, while D2 named only antennas. **Settled 2026-09-29: a working beacon identifies a
contact.** Check `0.4.beacon` is therefore a Phase 1 dependency, not a curiosity: if beacons
cannot be read on a client, D2 goes back for a decision.

Vanilla also accepts a broadcaster in range of **any** antenna in your network. The plan
measures range to the observing dish only — a stated limit for Phase 1 (plan §10).

**Then detection itself changed (2026-09-29):** the sensor now sees only broadcasters, our
own ships, and anything within a fixed 2 km. That made 0.4 the probe the whole detection
model rests on, not just identification, and added fixture grid J and the distance
requirements on A, C and I.

---

## 7. Results

To be filled in at the Phase 0 gate: one row per check, with the side it was measured on,
the verdict, and the log line it rests on. Anything FAIL or INCONCLUSIVE carries its
consequence from §5 into `TACTICAL_PLAN.md` before Phase 1 starts.

SP measured 2026-10-03 on the vanilla world `tactical testing world` (Ground Truth from the
Workshop, both probes local, sync distance 10 km). Extracts are in `probes/results/`: `sp-run4.txt`
is the full fixture run, `sp-run7-moon.txt` the controller run, `sp-run8-actions.txt` 0.6 and 0.8.
The DS columns are the next session's work. Nothing below is a gate verdict yet: the gate reads SP
and DS together.

| Check | SP | DS client | DS server | Evidence | Consequence applied |
|---|---|---|---|---|---|
| 0.1.grids | PASS 1328/1328 | | | every fixture grid returned inside 10 km; E inconclusive only after it flew out past 10 km | |
| 0.1.velocity | PASS 55/55 | | | E reported 40.00 vs measured 39.99–40.00 m/s | |
| 0.1.subgrids | rotor tops are separate entities, in the sphere | | | `RECORD subgrids` | |
| 0.1.streaming | H returned, outside the sphere (SP loads everything) | | | `RECORD streaming H found dist=12456 inSphere=False` | |
| 0.2.rotor / connector / control | PASS / PASS / PASS | | | both routes compiled and agreed on every grid | |
| 0.2 contact id | largest member was the named hull on every grid | | | `idPick` lines; D's group also holds a 6-block "Small Grid 984" mechanically, correctly not picked | |
| 0.3.own / other | PASS / PASS | | | Owner; Neutral (GCDW, CLEX, SATC); Enemies (FCTM); NoOwnership (G) | |
| 0.3.sides | — | | | needs the server log | |
| 0.4.state | PASS (10 FAIL = B during the deliberate toggle) | | | `0.4 antenna` lines | |
| 0.4.text | consistent (all Pufferfish antennas read "Puffer Fish") | | | trivially true in SP; DS world gives B a unique `RP-B SIGNAL` | |
| 0.4.toggle | flips seen within one 6 Hz poll | | | 3 `FLIP` lines; the real check is server vs client timing | |
| 0.4.beacon | PASS | | | I's beacon working, radius 20000 | |
| 0.5.class / size / projection | PASS / PASS (exact) / PASS | | | projection has `Physics == null` and IS returned by the sphere | |
| 0.5.aabb | WorldAABB is 3.4–5.1× LocalAABB volume | | | `RECORD aabb` — confirms LocalAABB for Dimensions | |
| 0.6.scoped / vanilla | PASS / PASS | | | dish 16→19 actions; Compact Antenna 16 ids, unchanged | |
| 0.6.execute / reload | PASS / PASS | | | `EXECUTED` per press; slot worked after reload before any screen opened | |
| 0.7.altitude / gravity | PASS / PASS (screenshots) | | | Moon: probe surf=4 m vs HUD 4 m, g=0.25 vs HUD 0.25 g; false in space | |
| 0.7.dampeners / unoccupied | PASS / PASS | | | flip seen; live values with the seat empty | |
| 0.8.preserve | PASS, but vacuous | | | Custom Data was empty; DS world seeds a `[Fixture]` section | |
| 0.8.persist | PASS | | | stamp present at load after save and reload | |
| 0.8.reach | — | | | DS only | |

### 7.1 Findings from the SP run that the gate must weigh

- **`CustomActionGetter` runs about 120 times a second** — twice a frame — for as long as one of
  the dish's actions is on a toolbar. Phase 1's action handler must be allocation-free: append three
  pre-built actions and return. No scans, no string building, no logging. The probe's own logging
  of every call put ~13,000 lines in one session and has been throttled (first three calls, then
  every 600th).
- **The projection is returned by `GetEntitiesInSphere`.** It passes because its `Physics` is null,
  so Phase 1 must filter `Physics == null` explicitly, not rely on the sphere to exclude it.
- **The sphere query is cheap:** 0.34–1.23 ms for ~7,200 entities at a 10 km radius.
- **`SessionSettings.SyncDistance` is hidden in the SP world screen** but read correctly from the
  save. A world needs it set in `Sandbox_config.sbc` and `Sandbox.sbc`; offline mode is required
  for local mods.

### 7.2 Probe changes made during the SP run (all committed)

- 0.7 reads the seat the local player is in, on any grid; else the last chosen controller; else
  RP-O's main cockpit / any flyable seat. The first build read RP-O's cryo chamber.
- Fixture letters declare their own tags (section 2 table); tags in a name still add on top, which
  is how relationships are declared.
- The scheduled run is 300 samples (10 minutes); `/radarprobe done` ends it early. One minute was
  not enough for the hands-on steps.
- 0.6 dish-call logging throttled, as above.

---

## 8. Build handoff

The probe code is a Sonnet task. The rules in `TACTICAL_PLAN.md` §9.1 apply in full.

1. **Tooling first:** add a `-Source` parameter to `tools/check-compile.ps1`, defaulting to
   today's folder. *(DeepInfra-sized.)*
2. `probes/RadarProbe/` — `metadata.mod` copied from an existing probe, one session
   component, one file per probe (`Probe01Detection.cs` …), a shared helper for the side tag,
   the log format in §3, fixture-tag parsing and the schedule in §4.
3. `probes/RadarActionProbe/` — the same skeleton for 0.6 and 0.8.
4. Header comment on each file in the house style: the question, how to run it, and the
   result table that decides it — as `OxygenProbe.cs` does.
5. Compile both with the checker. Report any member that does not exist rather than
   substituting a guess.

Sonnet does **not** interpret results. The logs come back to the Phase 0 gate.

### 8.1 Build status

Written 2026-09-30 in a session with no game install, so **nothing has been compiled or run**.
What was and was not verified:

| Check | Result |
|---|---|
| Syntax of all 12 files | Clean, with a real C# parser (tree-sitter) that was first shown to pass a known-good mod file and reject a broken one |
| C# 6 only, no banned APIs, none of the off-limits files touched | Grepped, clean |
| Every `Log.*` helper used is defined in its own mod | Cross-checked |
| **Do the members exist in the game's assemblies?** | **Yes — 2026-10-02.** Both probes compile clean against `Bin64` with `/langversion:6`, unchanged: every member in the table below exists. The checker was shown the same day to still reject a missing member and a C# 7 tuple, so the clean result is not trap 12 |
| Does it load in the game (whitelist)? | Not checked; only the game can say |

**First thing to do, on your machine:**

```powershell
.\tools\check-compile.ps1 -Source probes\RadarProbe\Data\Scripts\RadarProbe
.\tools\check-compile.ps1 -Source probes\RadarActionProbe\Data\Scripts\RadarActionProbe
```

**Members written from memory and most likely to need a fix.** Each is isolated so the fix is a
small deletion; the file headers list them too.

| Member | File | If it does not exist |
|---|---|---|
| `IMyCubeGrid.GetGridGroup`, `IMyGridGroupData.GetGrids` | `Probe02Groups.cs` `Route1` | delete `Route1`; `Resolve` falls through to route 2 |
| `MyAPIGateway.GridGroups.GetGroup` | `Probe02Groups.cs` `Route2` | delete `Route2` |
| `IMyRadioAntenna.IsBroadcasting` | `Probe04Broadcasters.cs` `Watched.State` | drop that term |
| `IMyProjector.ProjectedGrid` | `Probe05ClassSize.cs` `JudgeProjection` | 0.5.projection becomes a manual check |
| `IMyMotorStator.TopGrid` | `Probe01Detection.cs` `RecordSubgrids` | drop the record |
| `IMyFactionCollection.GetRelationBetweenFactions` | `Probe03Ownership.cs` `FactionRoute.Between` | return `"n/a"`; the block route is the decision anyway |
| `IMyTerminalAction` namespace, `CreateAction`, `Name`/`Icon`/`Action`/`Enabled` | `Probe06Actions.cs` | fix the `using`; if `CreateAction` is absent, 0.6 is a FAIL by construction |
| `System.Diagnostics.Stopwatch` | `ProbeCommon.cs` `Timing` | make `Timing.Ms` return -1 |

If **both** group routes fail to compile, that is not "both routes rejected" in the sense of
0.2: a missing member is a compile error and stops the whole mod. Delete the route, note it in
section 7, and 0.2 falls back to walking rotor tops by hand as described in 0.2.

**Where the build departs from the design above, and why.**

- **`SUMMARY` lines** are written when a run ends (and on `/radarprobe status` in the action
  probe): pass / fail / inconclusive counts per check. An addition to the section 3 format, so
  the gate does not have to count thirty verdict lines.
- **Recorded, not judged** checks (`0.1.subgrids`, `0.1.streaming`, `0.2.routes`, `0.5.aabb`,
  `0.8.access`) are `RECORD` lines, not verdicts.
- **0.1.subgrids** is answered through the stators' `TopGrid` rather than the group API, so it
  does not depend on the thing 0.2 is testing.
- **0.2 group detail** is logged on sample 1 and every tenth after; the verdicts run every sample.
- **0.3.own** checks every `OWN`-tagged grid that is present, which is O, A, E and H where H
  is loaded. The design named O, A and E.
- **0.5.class** is driven by the name tags (`STATIC`, `SMALL`, `LARGE`), so J, which the design's
  list omitted, is checked as small. A grid with no `STATIC` tag that reads `IsStatic = true` is
  a mismatch: it would be classed as a station.
- **0.4.toggle** uses a 6 Hz watcher in addition to the 2 s samples, and the watch list carries
  state across rebuilds so a flip that lands between them is not lost.
- **Checks that need a person or a second log** are emitted as `INCONCLUSIVE` with the reason
  stated: `0.3.sides`, `0.4.text`, `0.7.altitude`, `0.7.gravity`; and `0.6.execute`, `0.6.reload`
  and `0.6.side` produce no verdict at all, only the `EXECUTED` lines and the procedure.
- **0.8.reach** reports a stamp that arrives after more than 2 s as `INCONCLUSIVE`, not `FAIL`,
  because the delay is computed from two machines' clocks. "Never arrived" is still a FAIL, and
  is recognised by a `WROTE` line with no matching `SEEN` line in the other logs.
- **Contact id rule** (most blocks, ties to the lowest `EntityId`) is logged as `idPick` on each
  group line, marked whether it is the named grid.
- **`tools/check-compile.ps1`** gained `-Source`.

