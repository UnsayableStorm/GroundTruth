# Tactical Displays — Implementation Plan

Status: **planning. Nothing in this document is built.** Every component described here is
designed and agreed before code is written, and each phase ends with in-game verification
before the next one starts.

This plan adapts *Ground Truth Tactical Displays — Implementation Brief* (an external
brief, written without access to this repository). The brief's **goals** stand unchanged:
a shared tactical contact picture, own-ship telemetry, a family of LCD apps that agree
with each other, a small hardware footprint, bounded server cost, and nothing on screen
that the game cannot substantiate. Its **premises** about what already exists were wrong,
and the phases below are rebuilt around what is actually here.

---

## 1. What exists today, and what the brief assumed

| The brief assumed | What is actually in the repository |
|---|---|
| An existing tactical sensor framework producing contacts | No grid or ship detection of any kind. The only entity scan is `Readings.ScanBio`, which walks `IMyCharacter` within sync distance and reports species counts, a count of humanoid/robot "contacts", and bearing/distance to the **single nearest** character. No per-entity identity, velocity or persistence. |
| Contacts to "consume" and "normalise" | Nothing to consume. The contact primitive is new work, and it is Phase 1 of this plan. |
| A controllable antenna that can point at a target | `GT_RotatingRadarDish` spins at a fixed rate about one axis whenever it is working (`Rotation.cs`, `SpinningAntenna`). Presentation only; no aiming exists. |
| A configuration convention for displays | **Exists and is reusable**: a hand-parsed `[GroundTruth]` / `[GroundTruth.N]` section in Custom Data (`PanelSelection.cs`), plus injected terminal dropdowns (`PanelControls.cs`). |
| An LCD application registry | **Exists**: SE's own `MyTextSurfaceScript` attribute *is* the registry. Each app is a class; adding one touches no other app. `TssBase` supplies canvas scaling, palette and instrument resolution. |
| A shared, cached data layer | **Exists, for instruments**: `GroundTruthSession` recomputes each instrument once per second into a `BlockState` cache; panels, detail pane and terminal properties all read that cache and never scan. The pipeline (`StateFor`, `Recompute`, `WriteInfo`) is typed on `IMyTerminalBlock`, not on a specific block type. |
| Telemetry mechanisms to reuse | Partial. `Readings.Environment` has gravity-well, air density, oxygen, sun geometry. Grid speed is read in `Recompute`. There is no altitude, acceleration, orientation or dampener/thrust state today. |

---

## 2. Decisions already made

**D0.1 — The host is the existing Rotating Antenna.** `GT_RotatingRadarDish` and
`GT_RotatingRadarDish_S` become the tactical sensor. No new block, no new model, no new
art. This satisfies the brief's own goal of a small hardware footprint and its non-goal of
duplicating existing blocks. The block keeps doing everything a radio antenna does.

*On ENGINE_TRAPS trap 11.* Ground Truth deliberately moved its instruments **off**
`RadioAntenna` because the type brings an unsuppressible HUD marker and a lightning-rod
radius. That lesson does not apply here and must not be "fixed" later by mistake: this
block is supposed to be an antenna, and its marker and lightning-rod behaviour are the
correct behaviour of what it already is. Detection is being added to an antenna, not an
instrument being built on one.

**D0.2 — It needs no synthetic power sink.** `InstrumentPower` exists because
`UpgradeModule` has no power definition. `RadioAntenna` has its own draw and its own
`IsWorking` gate. Scanning is served behind `IsWorking`, exactly as every other reading is.

**D0.3 — The public terminal-property API is deferred.** `TerminalApi.cs` registers every
`GT_` property against `IMyUpgradeModule` only, and that registration path is the most
expensive piece of engineering in this mod (see the control-list history in
`TerminalApi.cs` and `GroundTruthSession.BeforeStart`). Ground Truth's own displays read the
cache directly and do not need it. Programmable Block access to radar data is Phase 6, as a
separate piece of work.

**D0.4 — The dish's rotation is decoration and stays exactly as it is.** `SpinningAntenna`
is not touched by this work. The dish never aims at anything, detection never depends on
where the model is pointing, and nothing on any screen claims a relationship to the spin.
The brief's antenna-tracking item (§4.9) is dropped, not deferred.

**D0.5 — The unit of "ship" is the mechanical grid group.** A ship with rotors, hinges and
pistons is several `IMyCubeGrid`s. Both the observer ("own ship") and every contact are
collapsed to their mechanical group, so a rover with a turret on a rotor is one contact,
not two. Grids joined only by connectors remain separate contacts: two docked ships are two
ships. *(Subject to the Phase 0 probe confirming `GetGridGroup` is whitelisted.)*

---

## 3. Design decisions

All six settled 2026-09-28. D1–D5 as recommended; D6 changed from the recommendation to
the stricter rule below. Reopening one changes the phase noted against it.

**D1 — Detection model** *(Phase 1)*.
The honest minimum is a sphere at the configured range, capped by what the machine actually
has streamed. The next step up is a **planet horizon check**: a contact on the far side of a
planet is not seen. That is an analytic ray-versus-sphere test against the planet's radius,
not a voxel raycast, so it is cheap.
*Decided:* sphere + planet horizon in Phase 1. Terrain occlusion (mountains) is not
attempted; it would need voxel raycasts and is stated as a known limit instead.

**D2 — Identification: what the sensor is allowed to know** *(Phase 1)*.
A radar return gives position, motion and size. It does not give a ship's name or who owns
it. The game hides those unless the target broadcasts. Three options:

1. Reveal name and relationship for every contact. Simple; contradicts the mod's premise.
2. **Reveal name and relationship only when substantiated**: the target is broadcasting
   (a working antenna with broadcast on, within its broadcast radius of us), or the target
   is owned by us or our faction. Otherwise the contact is `UNIDENTIFIED` with its size
   class, and relationship reads `UNKNOWN`.
3. Never reveal either.

*Decided:* option 2. It matches what the vanilla HUD already reveals, and gives the
player a reason to care about broadcast state. It is also the version the brief's own rule
("do not invent IFF") actually permits.

**D3 — Where the selected contact lives** *(Phase 2)*.
The brief asks for one selection per ship so every display shows the same target.
*Decided:* the selection is per mechanical grid group, stored as the contact's grid
`EntityId` in the Custom Data of **every** working dish in the group:

```
[GroundTruth]
Track.Id = 104857600012345
```

Writing it to all of them means losing one dish does not lose the selection. Custom Data is
already the store for panel selection because it saves with the world and syncs to the
server; this adds no new network message. There is deliberately no name fallback: a
contact's name is not a stable or even a known quantity (see D2).

**D4 — How a player selects** *(Phase 2)*.
*Decided:* three mechanisms, in order of how much they need proving:

1. **Automatic** by default — nearest contact — so an unconfigured system is useful.
2. A **dropdown on the dish's terminal**, listing current contacts nearest first, injected
   through `CustomControlGetter` exactly as `PanelControls` injects the instrument dropdown.
   Proven pattern; no registration against the antenna type.
3. **Toolbar actions** *Next contact / Previous contact / Clear*, for cockpit use. These
   need `CustomActionGetter` to behave the way `CustomControlGetter` does. Built only if the
   Phase 0 probe shows it does; otherwise the dropdown ships alone and this moves to later.

**D5 — Radar plot orientation** *(Phase 2)*.
*Decided:* ship-relative by default — the ship's forward is up on the screen, the
plane is the ship's own horizontal, and each contact's height above or below that plane is
drawn as a stalk from its plotted position. `Orientation = North` in Custom Data switches to
a north-up plot where a north convention exists (ENGINE_TRAPS trap 7; the bearing frames
already in `Readings` handle this).

**D6 — The dish reads nothing but contacts** *(Phase 1)*.
Environment, sun, seal, radiation, weather and life are the other instruments' jobs. The dish
does not take those readings, does not carry `CapEnv` or any other existing capability bit,
and its detail pane carries no `SITE` footer. Its capabilities are `CapRadar = 128` alone.
Bit 32 is unused but sits in the gap beside the reserved, unimplemented `GT_Grid` namespace,
so it is left alone.

*Why it matters beyond saving work:* an instrument that skips a reading holds a zeroed
struct, not a −1. If anything ever printed the dish's `Env`, it would report 0% oxygen and a
sun below the horizon as measurements. Keeping the capability bits off is what stops every
consumer that branches on capabilities from asking it.

---

## 4. Architecture

The brief's layering maps onto the existing code with one new layer:

```
Rotating Antenna (host block)
   │  one scan per ship per interval
   ▼
Tactical service  ── NEW: Tactical.cs ──  contacts, lifecycle, selection, own-ship
   │  cached, read-only to consumers
   ▼
GroundTruthSession.BlockState   (existing cache; the dish joins it as role 5)
   │
   ├── Detail pane             (existing WriteInfo path)
   ├── LCD apps                (new TssBase subclasses)
   └── Terminal properties     (Phase 6 — deferred)
```

**The scan belongs to the ship, not to the dish.** Every working dish in a mechanical group
contributes to one shared contact table for that group. Two dishes on one ship do not
produce two scans; three panels on one ship do not produce any. This is stricter than the
Bio Scanner, which scans per block, and it is what the brief's performance section asks for.

**Consumers never scan.** An LCD app resolves its dish through the existing `TssBase`
resolution (nearest of the role, overridable by name), then reads that dish's group's
contact table. The dropdown and the detail pane read the same table. One computation, every
presentation — the same rule the existing apps follow.

**New file layout** (proposed):

| File | Contents |
|---|---|
| `Tactical.cs` | Contact and own-ship models, the per-group contact table, lifecycle, the scan itself |
| `TacticalMath.cs` | Pure geometry — bearing, elevation, closing rate, horizon test, plot projection. No entity or session access, so it can be tested outside the game (§9.4) |
| `TacticalSelection.cs` | Selected-contact storage and resolution (D3), dropdown injection (D4) |
| `TacticalPanels.cs` | Radar, Contact List, Target Track |
| `NavigationPanels.cs` | Navigation, Sensor Status |
| `SweepPanel.cs` | Sensor Sweep (Phase 4) |

Registry changes are confined to `Instruments.cs` (one role, one capability, two table rows)
and the app id list in `PanelControls.cs`.

---

## 5. Data models

Every field names where it comes from. A field without a source is not in the model.
"Probe" means the API is believed available but its whitelist status is verified in Phase 0
before anything depends on it.

### 5.1 `TacticalContact`

| Field | Source | Unavailable reads as |
|---|---|---|
| `Id` | `EntityId` of the group's largest member by block count, ties to the lowest id (probe 0.2 confirms this picks the hull) | — (always present) |
| `Position` | group's world-space bounding-box centre | — |
| `PreviousPosition` | previous scan | absent on first sighting |
| `Velocity` | `Physics.LinearVelocity` of that member (or position deltas, if probe 0.1 fails on clients) | zero only if genuinely static |
| `Range`, `Bearing`, `Elevation` | computed from the observing dish; bearing frames reuse `Readings.Bearing` | bearing −1 where the frame is undefined |
| `ClosingRate` | relative velocity projected on the line of sight | computed; always available once velocity is |
| `Class` | `IsStatic` + grid size: *Station*, *Large ship*, *Small ship* | — |
| `Dimensions` | group AABB extents, metres | — |
| `Name` | grid `DisplayName`, **only when identified (D2)** | `UNIDENTIFIED` |
| `Relationship` | owner vs. dish owner via the factions API (Probe), **only when identified** | `UNKNOWN` |
| `Broadcasting` | any working antenna on the target broadcasting and in range (Probe) | false |
| `FirstSeen`, `LastSeen` | session seconds | — |
| `State` | lifecycle below | — |
| `Trail` | ring buffer of past positions, fixed length (Phase 4) | empty |

Deliberately absent: weapons, threat level, cargo, crew, heading-of-intent. Nothing in the
game substantiates them from a sensor's point of view.

**Excluded from contacts:** the observer's own mechanical group; projections (no physics);
grids with no physics; characters (the Bio Scanner's domain); voxels and floating objects.
Whether to include very small debris is a threshold in Phase 1 (proposed: ignore groups
under a configurable block count, default 5, stated on the Sensor Status screen).

### 5.2 Contact lifecycle

`DETECTED` — seen in the most recent scan.
`TRACKED` — detected **and** selected. Only one contact per ship can be tracked.
`LOST` — not seen in the most recent scan; last-known data retained for the retention
interval (default 30 s, configurable on the dish). A lost contact is drawn as last-known,
visibly distinct, never as current.
`EXPIRED` — retention elapsed, or the grid closed. Removed from the table; no reference to
the entity survives.

A closed entity (destroyed, grinded, merged) goes straight to `EXPIRED`: there is no
last-known position to honour for something that no longer exists. A selected contact that
is lost stays selected through `LOST` and the selection clears at `EXPIRED`, falling back
to Automatic.

**Bounds.** The table holds at most 64 contacts per ship (nearest kept); trails at most 20
points per contact. Both are constants, both are stated on the Sensor Status screen when
they bite.

### 5.3 `OwnShipTelemetry`

| Field | Source |
|---|---|
| Speed, velocity | grid physics (already read in `Recompute`) |
| Acceleration | Δvelocity between samples |
| Heading, pitch, roll | ship controller orientation vs. gravity; heading uses the existing north convention |
| Altitude (sea level / surface) | `IMyShipController.TryGetPlanetElevation` (Probe) |
| Natural / artificial gravity | `IMyShipController.GetNaturalGravity` / `GetArtificialGravity` |
| Dampeners | `IMyShipController.DampenersOverride` |
| Planet | existing `Readings.Environment.Planet` |

Navigation data comes from a **ship controller** on the panel's grid (cockpit, remote,
flight seat), found by the same nearest-wins resolution. No controller means the Navigation
app says so — it does not synthesise a heading from a dish.

### 5.4 `TacticalSystemState`

Operational (any dish working), dish count / working count, configured range, effective range
(the lower of configured and sync distance — stated, not hidden), contact count, seconds
since last scan, last error, selected contact id. All of it is state the service actually
holds; none of it is a status message written for effect.

---

## 6. Performance budget

| Work | Cadence | Scope |
|---|---|---|
| Sphere query for grids | every **2 s** (configurable, 1–10) | once per mechanical group with a working dish |
| Group collapse, own-group exclusion | per scan | per candidate entity |
| Contact update, lifecycle | per scan | ≤ 64 contacts |
| Own-ship telemetry | 1 s, existing recompute | per panel-resolved controller |
| LCD redraw | each app's own `UpdateInterval`, 1 s default | skipped when the frame is unchanged |
| Sweep animation | its own throttle, client only | never on a dedicated server |

A ship moving at 100 m/s moves 200 m between 2 s scans. That is visible on a close-range
plot and irrelevant at 5 km. The interval is a dish setting because only the player knows
which matters.

Like every other reading, the scan only runs for groups someone is looking at — a panel,
an open terminal, or (Phase 6) a script. An unwatched dish on a parked ship costs nothing.

---

## 7. Multiplayer model

Readings in this mod are computed on whichever machine needs them. A panel renders on a
client, so a client computes its own scan. Consequences, stated rather than papered over:

- **Range is bounded by streaming.** A client only has grids within sync distance; a grid
  that is not streamed does not exist to query. The effective range is shown.
- **Two clients may briefly disagree** at the edge of streaming range. Both are reporting
  truthfully what their machine has.
- **Selection is shared** through Custom Data (D3), which syncs, so every player on the ship
  sees the same tracked contact.
- The server runs no radar scan until Phase 6 gives a server-side consumer a reason to.

---

## 8. Phases

Each step lists what it produces and how it is verified. **A phase is done when its exit
check passes in game**, in single player and as a client on a dedicated server where the
step touches anything networked. `tools/check-compile.ps1` runs before every in-game test.

### Phase 0 — Prove the APIs (no shipped code)

The mod has lost days to APIs that compiled and never worked (ENGINE_TRAPS 9 and 12). Every
API this plan depends on that the mod does not already use is proven first, in two probe
mods, `probes/RadarProbe/` (read-only) and `probes/RadarActionProbe/` (touches terminal
actions and Custom Data), run in single player and as a client on a dedicated server.

**The full design — fixture world, log format, pass/fail criteria and what each result
changes — is in [`PHASE0_PROBES.md`](PHASE0_PROBES.md).** The table below is the summary.

| # | Question | Blocks |
|---|---|---|
| 0.1 | `GetEntitiesInSphere` returns remote `IMyCubeGrid`s on a DS client, and `Physics.LinearVelocity` is populated for them (not zero) | Phase 1 |
| 0.2 | `IMyCubeGrid.GetGridGroup(GridLinkTypeEnum.Mechanical)` is whitelisted and returns the rotor/piston children | D0.5 |
| 0.3 | Owner and relationship: `BigOwners`, `IMyFactionCollection` relation lookups | D2 |
| 0.4 | A target's antenna and beacon broadcast state and radius are readable, and sync when toggled | D2 |
| 0.5 | `IsStatic`, `GridSizeEnum`, `LocalAABB` vs `WorldAABB`, and whether projections can be excluded | Class, Dimensions |
| 0.6 | `CustomActionGetter` injects toolbar actions onto one block without registering them against the type | D4.3 |
| 0.7 | `IMyShipController.TryGetPlanetElevation`, gravity and dampener members | Phase 3 |
| 0.8 | Writing `CustomData` from mod code on a DS client syncs to the server and to other clients | D3 |

**Exit:** a written result for every check, in `PHASE0_PROBES.md` §7. Any row that fails
changes the design of the step it blocks *before* that step is built.

### Phase 1 — The host and the contact primitive

1.1 **Register the dish as an instrument.** `RoleRadar = 5`, `CapRadar = 128`, two rows in
`Instruments`. Update the comments in `Instruments.cs`, `Rotation.cs` and
`CubeBlocks_GroundTruth.sbc` that say the dish is not an instrument.
1.2 **Fix the orphan check.** `Instruments.SubtypesWithoutComponent` will report the dish as
having no `InstrumentPower`. Teach it that `RadioAntenna` subtypes carry their own power, so
the check stays loud for real drift and silent for this.
1.3 **Gate `Recompute` by role** per D6: for the dish it runs the contact scan and nothing
else — no environment, seal, radiation, weather or bio reads. The age/timestamp bookkeeping
at the top of `Recompute` still applies.
1.4 **Build `Tactical.cs`**: the per-group table, the scan (sphere, group collapse, exclusions,
horizon check per D1), identification per D2, lifecycle and bounds per §5.2.
1.5 **Detail pane readout.** The dish's terminal info pane lists its contacts as text:
count, then nearest first — class, range, bearing, closing rate, state, identity. This is the
first presentation for the same reason v0.1 of this mod was detail-pane-only: it proves the
data before anything is drawn. A new `WriteRadar` case in `WriteInfoInner`; unlike the four
existing writers it does **not** call `WriteEnvironment` (D6).

**Exit:** with a dish on a ship and three other grids around it — one ours, one broadcasting
stranger, one silent stranger, one of them with a rotor-mounted subgrid:
the pane shows three contacts, not four; ours is named; the broadcasting stranger is named;
the silent one is `UNIDENTIFIED`; the one behind a planet is absent; grinding one moves it to
`EXPIRED` without passing through `LOST`; flying one out of range shows `LOST` then `EXPIRED`
after the retention interval. The dish's pane has no `SITE` section and no environmental
line of any kind. The existing sixteen instruments read exactly as before.

### Phase 2 — Selection and the core displays

2.1 **Selection** (`TacticalSelection.cs`) per D3/D4: automatic, dish dropdown, and toolbar
actions if 0.6 passed. Selection survives reload.
2.2 **Contact List app** (`GT_Contacts`). Text rows sized to the surface; configurable sort
(range, bearing, closing rate, class) and a row cap; unavailable fields print as `—`, never
as `0`.
2.3 **Tactical Radar app** (`GT_Radar`). Plot per D5; range rings with labels; own heading
mark; contact markers by class; elevation stalks; optional velocity vectors; tracked
contact highlighted; lost contacts hollow. Built-in sprites only (`Circle`,
`CircleHollow`, `SquareSimple`, `Triangle`) — no new textures, so ENGINE_TRAPS 2 and 3 do
not come into play.
2.4 **Target Track app** (`GT_Track`). The tracked contact in full: every field of §5.1 that
is available, and which ones are not. Lost contacts show last-known values with their age.
2.5 **Per-display configuration** through the existing `[GroundTruth.N]` parser: `Range`,
`Sort`, `Rows`, `Vectors`, `Labels`, `Orientation`. Unknown keys ignored, malformed values
fall back to defaults.
2.6 **Register the three app ids** in `PanelControls` so the dish dropdown appears beside
them.

**Exit:** the brief's acceptance criteria that apply to these apps — a List and a Radar on
the same ship show the same contacts; selecting from the dish dropdown moves the highlight on
the Radar and the subject of the Track screen on every panel; a second player on the same
DS sees the same selection; garbage in Custom Data leaves every panel working on defaults.

### Phase 3 — Own ship

3.1 **Own-ship telemetry** in `Tactical.cs`, from a ship controller per §5.3.
3.2 **Navigation app** (`GT_Navigation`).
3.3 **Sensor Status app** (`GT_SensorStatus`) from §5.4, including which limits are
currently binding (streaming range, contact cap, debris threshold).

**Exit:** Navigation agrees with the vanilla cockpit HUD for speed, altitude and gravity on
a planet and in space; a grid with no controller shows the absence, not zeros; unpowering
every dish flips Sensor Status to offline within one scan interval.

### Phase 4 — Presentation

4.1 **Trails** on the Radar app from the bounded ring in §5.1.
4.2 **Sensor Sweep app** (`GT_Sweep`). An LCD app only; it has nothing to do with the dish
model. The sweep turns once per scan interval, so on screen it reads as the scan cadence
rather than as a detection mechanism. Contacts brighten as it passes and fade toward the
next scan. Throttled independently; never runs on a dedicated server.
4.3 Layout and density pass across all tactical apps, including corner and wide LCDs.

**Exit:** four panels of mixed apps on each of two ships near each other on a DS, no
measurable frame cost on the client beyond the existing apps', and trails bounded at their
stated length.

### Phase 5 — Validation and documentation

5.1 Stress: 60+ grids in range; ten tactical panels on one ship; two ships doing the same.
5.2 Lifecycle abuse: merge blocks, grid splits under fire, grinding the tracked contact,
the observing dish destroyed mid-track, pasting a blueprint with a selection saved in it.
5.3 `docs/INTEGRATION.md`: role 5 and `CapRadar` in the tables; the tactical Custom Data keys;
the new apps; the cost model and known limits from §6 and §7. `GT_SysApiVersion` to 1.2.
5.4 **Fix the README quick-start**, which gathers instruments with
`List<IMyUpgradeModule>` and will silently miss a radar on `IMyRadioAntenna`. Recommend
`IMyTerminalBlock` with the role/capability test. (Needed as soon as Phase 6 lands; harmless
to fix earlier.)
5.5 New ENGINE_TRAPS entries for anything Phase 0–4 cost real time.
5.6 Workshop description and screenshots.

### Phase 6 — Deferred: scripts and events

Not scheduled. Listed so the design leaves room for it.

- **Terminal properties on the dish**: either a second registration path for
  `IMyRadioAntenna` or generalising `TerminalApi` over interface type. Scalar system state
  (`GT_RadarContacts`, `GT_RadarRange`, nearest and tracked contact's range/bearing/closing
  rate) fits the existing convention; a full contact list does not, and is the trigger the
  Versioning section of `INTEGRATION.md` names for a mod-message API.
- **Event Controller events**: contact detected, unidentified contact within range, tracked
  contact lost. Recipe in ENGINE_TRAPS trap 8.

---

## 9. Who does what

Three tiers, chosen by what goes wrong if the work is done badly, not by how much typing
it involves.

- **Opus** — decisions, anything where a plausible wrong answer passes review, and the
  phase gates. Kept small on purpose.
- **Sonnet** — the bulk of implementation, working from this document and the files it
  names. Most steps are here.
- **DeepInfra models** — mechanical, fully specified work whose output is a small diff or a
  document that can be checked by reading it. No design judgement, no engine knowledge
  required beyond the rules below.
- **No model** — work a script or `grep` does better. Paying any model to do it is waste.

### 9.1 Rules every delegated task carries

A cheaper model does not know what this repository learned the hard way. Every handoff, at
any tier below Opus, includes these, verbatim:

1. **C# 6 only.** No local functions, tuples, `out var`, pattern matching, `is` with a
   declaration. `tools/check-compile.ps1` has already passed a C# 7 feature once that the
   game then rejected.
2. **The mod whitelist applies.** Only APIs the mod already uses, or that a Phase 0 probe
   has cleared. No `MyIni`, no reflection, no `System.IO`, no threads.
3. **Never touch** `TerminalApi.cs`, `InstrumentPower.cs`, `SealSync.cs`, `Rotation.cs`, or the
   `LoadData`/`BeforeStart` registration timing in `GroundTruthSession.cs`. A step that
   seems to need to is a step to escalate.
4. **−1 means no reading.** An unavailable value is never rendered or stored as 0.
5. **Read `docs/ENGINE_TRAPS.md` first.** It is shorter than the time any one of its entries
   cost.
6. **Match the file's comment voice**: comments explain *why*, including the wrong turns.
7. **Stop and escalate** rather than improvise when the step needs an API not in the Phase 0
   table, a decision not in §2–§3, or a change outside the files the step names.

### 9.2 Opus → Sonnet handoff

Sonnet starts each step by reading this document, `ENGINE_TRAPS.md` and every file the step
names, and finishes it with a compile check and a short report: what changed, what was not
done, what it was unsure of. It does not start the next phase; the gate does.

**Phase gates stay with Opus**: review the phase's full diff against this plan, read the
in-game results for the exit check, and decide whether the phase is done. That is where the
Opus budget buys the most — one careful review per phase instead of supervision per edit.

### 9.3 Step assignments

| Step | Tier | Why |
|---|---|---|
| D1–D6 decisions | **You + Opus** — done | Settled 2026-09-28 (§3) |
| 0.1–0.8 probe **design** (what to log, what counts as pass) | **Opus** | Trap 12: a probe that cannot fail proves nothing |
| Probe **code** | Sonnet | Straightforward once the pass/fail conditions are written |
| Probe **log reading** | No model, then Opus | `grep` the `GT` / `RADARPROBE` lines out of the game log; Opus reads only those. Full logs are the most expensive thing to put in front of any model |
| 1.1 registry rows, comment updates | DeepInfra | Two table rows and three comment rewrites to a given text |
| 1.2 orphan check | Sonnet | Small, but it is a safety check and must stay loud for real drift |
| 1.3 `Recompute` role gate | Sonnet | Touches the shared loop every instrument depends on |
| 1.4 `Tactical.cs` — model, lifecycle, bounds | **Opus designs the types and state machine**, Sonnet implements | Lifecycle edge cases (closed vs. lost, selection through expiry) are where a plausible bug lives |
| 1.4 group collapse, identification (D2) | **Opus** | Correctness depends on engine behaviour the probes reveal; a wrong answer looks right on screen |
| 1.4 geometry: bearing/elevation, closing rate, horizon test, plot projection | DeepInfra, into `TacticalMath.cs` | Pure functions with exact expected outputs. Kept free of entity and session access so they can be tested outside the game (see below) |
| 1.5 detail pane text | Sonnet | Follows the existing `WriteInfo` pattern |
| 2.1 selection storage and dropdown | Sonnet | Copies the `PanelControls` pattern; the Custom Data sync question is settled by probe 0.8 |
| 2.1 toolbar actions | Sonnet, only if 0.6 passed | |
| 2.2–2.4 the three apps | Sonnet | `TssBase` already solves scaling, palette and resolution |
| 2.3 radar plot layout constants, marker sprite table | DeepInfra | Numbers and a lookup, tuned against screenshots |
| 2.5 Custom Data keys | Sonnet | Extends a hand parser whose failure mode must stay "defaults" |
| 2.6 app id registration | DeepInfra | One list |
| 3.1 own-ship telemetry | Sonnet | |
| 3.2–3.3 Navigation, Sensor Status | Sonnet | |
| 4.1–4.3 trails, sweep, layout pass | Sonnet; layout constants to DeepInfra | |
| Test checklists from each phase's exit criteria | DeepInfra | Turns the exit paragraph into a step-by-step in-game script you can follow |
| 5.1–5.2 stress and abuse **results** | **Opus** | Judging whether an odd result is a bug or the engine |
| 5.3–5.4 INTEGRATION.md tables, README quick-start | DeepInfra drafts, Sonnet checks against code | The tables must match the code exactly, which is checkable |
| 5.5 ENGINE_TRAPS entries | **Opus** | Deciding what was actually learned, and saying it symptom-first |
| 5.6 workshop text | DeepInfra draft | |

### 9.4 Making delegation safe

**`TacticalMath.cs` is split out so it can be tested without the game.** Every function in it
takes vectors and numbers and returns vectors and numbers — no `IMyEntity`, no session, no
logging. A small test project referencing `VRage.Math.dll` from the game install, run the way
`check-compile.ps1` references assemblies, can check it against hand-computed cases in a
second. That is what makes it safe to hand to the cheapest tier: the output is either right
or the test says so.

**The compile check runs on your machine**, because it needs the game's assemblies. No model
in a cloud session can run it. A delegated step is not done until you have run it.

**A DeepInfra task gets a self-contained brief**: the §9.1 rules, the exact files, the exact
change, and the expected result. If the brief cannot be written that precisely, the task
belongs to Sonnet.

---

## 10. Known limits, stated up front

- Range cannot exceed what the machine has streamed. Beyond sync distance there is nothing
  to detect, and the Sensor Status screen says so.
- Planets occlude; terrain does not.
- Subgrids joined by connectors are separate contacts.
- A panel reads a dish on its own grid only, as every Ground Truth app does today. A dish
  on a rotor-mounted subgrid is found by panels on that subgrid, not the main hull — to be
  revisited in Phase 1 now that groups are a first-class idea.
- Identification (D2) is only as good as the target's broadcasting. A silent enemy is a
  silent enemy.

## 11. Out of scope

Everything in the brief's non-goals, and its list of future extensions: weapon control,
automated targeting, fleet displays, docking, astrometrics. None is ruled out forever; none
is designed here.
