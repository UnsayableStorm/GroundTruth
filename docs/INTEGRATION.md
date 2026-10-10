# Ground Truth — Integration Guide

Reading Ground Truth instruments from Programmable Block scripts, from other mods, and
from the vanilla Event Controller.

Ground Truth is a **substrate**. It measures environmental conditions and publishes them.
It has no progression, no gating, and no opinion about what a reading means. If you are
building something that needs to know the radiation, weather, atmosphere or life around a
grid, this is that layer.

**Ground Truth has to be installed for any of this to return data.** What you do not need
is a *coupling* to it: values are plain terminal properties looked up by name at runtime,
so there is no assembly reference, no load-order requirement and no registration step.

Without the mod present, your script or mod still compiles and loads — the properties are
simply absent, and reads return nothing rather than throwing. That makes Ground Truth a
soft requirement you can detect and degrade around, not a hard one that breaks your code
when it is missing.

---

## Contents

- [Quick start](#quick-start)
- [The API is there at load](#the-api-is-there-at-load--no-request-needed)
- [Finding instruments](#finding-instruments)
- [Conventions that matter](#conventions-that-matter)
- [Property reference](#property-reference)
- [Event Controller events](#event-controller-events)
- [LCD apps](#lcd-apps)
- [Tactical sensor](#tactical-sensor)
- [Cost model](#cost-model)
- [Known limits](#known-limits)
- [Versioning](#versioning)

---

## Quick start

```csharp
// Every instrument is an UpgradeModule block. Filter by role to exclude other modules.
//
// GetProperty, not GetValueFloat, for the test: GetValueFloat THROWS when a property does
// not exist - and it does not exist in a world with no Ground Truth instrument, or with
// Ground Truth not installed. Where an instrument exists, the properties are there from
// world load, single player and dedicated server alike (API 1.2+, next section).
var sensors = new List<IMyUpgradeModule>();
GridTerminalSystem.GetBlocksOfType(sensors, b => b.GetProperty("GT_SysBlockRole") != null);

foreach (var s in sensors)
{
    float rads   = s.GetValueFloat("GT_RadRate");     // per second, -1 if not a monitor
    float oxygen = s.GetValueFloat("GT_EnvOxygen");   // 0-1, weather already applied
    float wind   = s.GetValueFloat("GT_WxWindSpeed"); // m/s, -1 if not a weather station
}
```

Blocks are `UpgradeModule` because terminal properties register against a block interface,
and `IMyUpgradeModule` is the narrowest one that also renders a detail info pane. They
declare no `<Upgrades>`, so they do nothing at all next to a refinery — the base type is a
means, not a purpose.

The two Rotating Antennas are the exception: those are real `RadioAntenna` blocks and
carry **no `GT_` properties**. Since the Tactical release they are also the ship's
**tactical sensor** — see [Tactical sensor](#tactical-sensor) for what that exposes and,
just as important, what it does not yet expose to scripts.

---

## The API is there at load — no request needed

**Since the Tactical update, `GT_` properties exist as soon as any Ground Truth instrument
has loaded: in single player, on a host, and on a dedicated server, with no terminal opened
and nothing for your script to do.** A PB can read an instrument on its first run. This is
API **1.2**; `GT_SysApiVersion` reports it.

Precisely: the properties are registered when the **first instrument in the world
finishes loading**, on each machine. In a world with no instrument they do not exist,
which is harmless — there is nothing to read. Registration does not wait for a terminal and
does not need a request. (Why that took until 1.2 to get right is trap 20 in
[`ENGINE_TRAPS.md`](ENGINE_TRAPS.md).)

Detect instruments by subtype rather than by a `GT_` property, so the check works even on a
grid with no instrument at all:

```csharp
var sensors = new List<IMyUpgradeModule>();
GridTerminalSystem.GetBlocksOfType(sensors,
    b => b.BlockDefinition.SubtypeName.StartsWith("GT_"));
```

### The old handshake still works

Earlier versions registered the properties only when someone opened a terminal, and on a
dedicated server only when a script wrote `GT_API_ENABLE` into an instrument's Custom
Data. Scripts written that way keep working unchanged: the mod still watches for the token
and still replaces it with `GT_API_READY`, it just no longer needs to be asked. New scripts
can leave it out.

### Why it used to need asking, and why it no longer does

Registering terminal controls too early corrupted **every upgrade module in the game**
during development — vanilla modules, other mods' shield generators and warp drives, all
losing Name, Show In Terminal and Show In Toolbar Config. The fix at the time was to touch
the shared control list only when the game had visibly built it (a terminal opening) or a
script explicitly asked.

The real cause turned out to be narrower. A block type's control list is created on first
request by copying its base classes' lists *as they are at that moment*, and the game
builds those lists during each block's own initialisation. Asking for a list before any
block of that type has initialised freezes it incomplete; the builds that broke made
exactly that request at world load, before any block existed. Ground Truth now registers
from its first instrument's first frame, after that block has built every list involved.
That was tested on single player, on a dedicated server with a joining client, and
alongside Animation Engine (the mod that wiped the shared list on a live server), checking
every block type in the world each time. Details in `ENGINE_TRAPS.md`.

---

## Finding instruments

Every instrument publishes four metadata properties.

| Property | Meaning |
|---|---|
| `GT_SysBlockRole` | 1 radiation, 2 habitat, 3 weather, 4 bio. **0 or −1 means not an instrument.** |
| `GT_SysCapabilities` | bitmask of which namespaces this block populates |
| `GT_SysOperational` | powered, enabled and functional |
| `GT_SysAge` | seconds since this reading was computed |

**Branch on capabilities, not on role or subtype.**

```csharp
const int CapEnv = 1, CapSun = 2, CapRad = 4, CapWx = 8, CapHab = 16, CapBio = 64;

int caps = (int)block.GetValueFloat("GT_SysCapabilities");
if ((caps & CapWx) != 0) { /* this block has weather readings */ }
```

There are already sixteen block subtypes behind four instruments, with variants and both grid
sizes. A subtype list will break; a capability test will not.

---

## Conventions that matter

### −1 means "no reading", 0 means "measured zero"

This is the single most important convention here. A Radiation Monitor asked for wind
returns **−1**, not 0. A world with radiation switched off returns **−1** for the dose,
not 0.

```csharp
float rads = block.GetValueFloat("GT_RadRate");
if (rads < 0) { /* this block cannot answer - do not treat as safe */ }
```

Treating −1 as a measurement produces alarms that never fire and dashboards that report
perfect safety on a block that is not a radiation monitor.

### An offline instrument has no reading

An instrument that is **switched off, damaged, or unpowered** measures nothing. While it is
offline:

- every measurement property returns **−1** (numbers) or **false** (flags) — the "no
  reading" convention, never the last value it measured;
- the **`GT_Sys` metadata keeps answering**, so a script can still find the block and
  learn why it is silent: `GT_SysOperational` is **false**, `GT_SysBlockRole` and
  `GT_SysCapabilities` are unchanged;
- Event Controller events **skip** it (it neither raises nor clears an alarm);
- the LCD apps show **OFFLINE** with the reason — `SWITCHED OFF`, `DAMAGED`, `NO POWER` or
  `NOT WORKING` — and Overview and Strip keep its cell, marked offline, rather than hiding it.

Check `GT_SysOperational` before trusting a −1 to mean "nothing to measure here": on an
offline block it means "not measuring right now".

"Powered" means the instrument's own power draw is supplied by the grid. This matters
because the block's vanilla `IsWorking` does **not** include power for this block type, so
code that tests `IsWorking` on an instrument will see `true` on a dead grid — use
`GT_SysOperational` instead.

### Units are part of the meaning

Every property below states its unit. Where the engine quantises a value it is noted —
radiation accrues in 1.667 s steps, so a consumer averaging over a shorter window sees
stair-steps and may conclude the instrument is broken.

### Names are permanent

Once `GT_RadRate` means rads per second at the block, it means that forever. New meaning
gets a new name.

---

## Property reference

### `GT_Sys` — metadata, on every instrument

| Property | Type | Notes |
|---|---|---|
| `GT_SysApiVersion` | float | MAJOR.MINOR. See [Versioning](#versioning). |
| `GT_SysBlockRole` | float | 1 rad, 2 hab, 3 weather, 4 bio |
| `GT_SysCapabilities` | float | bitmask |
| `GT_SysOperational` | bool | on, functional **and** powered. While false, every non-`GT_Sys` property answers −1 / false. See [An offline instrument has no reading](#an-offline-instrument-has-no-reading). |
| `GT_SysAge` | float | s since computed |

### `GT_Env` — position, on every instrument

| Property | Type | Notes |
|---|---|---|
| `GT_EnvInGravityWell` | bool | |
| `GT_EnvAirDensity` | float | 0-1. **Weather does not change this.** |
| `GT_EnvOxygen` | float | 0-1 breathable. **Weather already applied — do not multiply by `GT_WxOxygenMult`.** |
| `GT_EnvBreathable` | bool | oxygen above 0.5 |
| `GT_EnvSolarProtection` | float | the planet's shielding factor |
| `GT_EnvPlanetRadGain` | float | the planet's own radiation gain |

### `GT_Sun` — sun geometry

| Property | Type | Notes |
|---|---|---|
| `GT_SunLosClear` | bool | nothing between this block and the sun |
| `GT_SunBlockedDistance` | float | m to the occluder, −1 if clear |
| `GT_SunElevation` | float | degrees above local horizon, **negative is night**, −999 in space |
| `GT_SunUp` | bool | |

Solar output needs both halves: `GT_WxSolarMult` is what the *weather* is doing, and at
midnight it reads 100% while panels produce nothing. Combine with `GT_SunElevation`.

### `GT_Rad` — Radiation Monitor

| Property | Type | Notes |
|---|---|---|
| `GT_RadEnabled` | bool | radiation is on in this world |
| `GT_RadRate` | float | **total exposure per second at this position** |
| `GT_RadRateSolar` | float | zero unless sun line of sight is clear |
| `GT_RadRatePlanetary` | float | zero inside an airtight volume |
| `GT_RadRateWeather` | float | **signed** — rain is −0.60 and protects |
| `GT_RadAccumulates` | bool | above the engine's ignore threshold, so it actually builds |
| `GT_RadTimeToCritical` | float | s at the current rate, −1 if not accumulating |
| `GT_RadShelterState` | float | 0 exposed, 1 occluded, 2 sealed, 3 atmosphere |
| `GT_RadAtmosShielding` | float | `min(1, protectionFactor × airDensity)` |
| `GT_RadSunBlocked` | bool | |
| `GT_RadAirtight` | bool | |
| `GT_RadBaseRate` | float | `SolarRadiationPerSecond × intensity` |
| `GT_RadIntensitySetting` | float | world `SolarRadiationIntensity` |

**Exposure and accumulation are different questions.** The engine ignores anything below
~0.060/s, so a real dose can exist and never build. For "am I in danger", use
`GT_RadAccumulates`.

**Shelter differs by source.** Solar dies to any occluder; planetary only to an airtight
seal. That is why they are published separately.

### Seal state on a dedicated server

`GT_HabAirtight` and `GT_RadAirtight` are the only readings that do **not** originate on
the machine reading them. A dedicated-server client has no pressurisation data at all —
`grid.GasSystem` is null there — so the server evaluates seal state and pushes it to
clients on change, with a 30 second heartbeat.

For a consumer this means one thing: **immediately after joining, seal state may not have
arrived yet**, and a `false` from `GT_HabAirtight` in that window means "not yet told",
not "not sealed". It settles within a second of any change and within 30 seconds
regardless.

**Test `GT_HabSealKnown` before believing `GT_HabAirtight`.** That is what it is for, and
it is the only place in this API where a bool needs a companion to be trustworthy — every
other reading uses the −1 sentinel because it is a float.

Every other reading in this mod is computed locally and has no such window.

### `GT_Wx` — Weather Station

Live state:

| Property | Type | Notes |
|---|---|---|
| `GT_WxBodyHasWeather` | bool | this body has a weather system |
| `GT_WxActive` | bool | an effect is running |
| `GT_WxIntensity` | float | 0-1. **Not severity** — every effect ramps to 1.0 in ~17 s. |
| `GT_WxTrend` | float | +1 building, 0 steady, −1 easing |
| `GT_WxElapsed` | float | s since first seen |
| `GT_WxPeaked` | bool | |
| `GT_WxTimeToClear` | float | s. **−1 while the effect is still building** (before `GT_WxPeaked`), and **−1 if its onset was not observed**; otherwise the time remaining by the symmetry rule, ≥ 0 |
| `GT_WxSpeed` | float | m/s of the block itself |

Live effect on systems:

| Property | Type | Notes |
|---|---|---|
| `GT_WxSolarMult` | float | weather's effect on solar |
| `GT_WxWindMult` | float | weather's effect on wind |
| `GT_WxOxygenMult` | float | weather's contribution to oxygen |
| `GT_WxTempMult` | float | coefficient, not degrees. Negative for snow, up to 11.0. |
| `GT_WxWindSpeed` | float | m/s = `MaxWindSpeed × windMult` |
| `GT_WxMaxWindSpeed` | float | the planet's declared ceiling |
| `GT_WxTurbineSiteFactor` | float | `airDensity × windMult`. Station grids only. |

Declared, from the shipped effect table:

| Property | Type | Notes |
|---|---|---|
| `GT_WxEffectKnown` | bool | **false for modded weather** |
| `GT_WxEffectSolar` | float | at full strength |
| `GT_WxEffectWind` | float | |
| `GT_WxEffectTemperature` | float | |
| `GT_WxEffectOxygen` | float | |
| `GT_WxDeclaredMinLength` | float | s, the planet's declared range for this effect |
| `GT_WxDeclaredMaxLength` | float | |
| `GT_WxPlanetTypeCount` | float | effects this planet can produce |
| `GT_WxEffectCount` | float | effects in the shipped table |

Hazards:

| Property | Type | Notes |
|---|---|---|
| `GT_WxHazardInjury` | bool | this effect declares damage |
| `GT_WxHazardDamageMin` | float | |
| `GT_WxHazardDamageMax` | float | **40 for hailstorm, sandstorms, Mars storms, ElectricStorm** |
| `GT_WxHazardInjuryMinIntensity` | float | intensity at which injury begins |
| `GT_WxHazardRadiation` | bool | declares a radiation source |
| `GT_WxHazardRadiationGain` | float | **signed** |
| `GT_WxHazardMinIntensity` | float | intensity at which the radiation term begins |
| `GT_WxRadiationShelter` | bool | gain is negative — this weather *protects* |
| `GT_WxHazardActive` | bool | **the one an alarm should watch** — hazard threshold reached |

**Respect the sign on radiation gain.** Rain and thunderstorms declare −0.60. Treating it
as a magnitude turns shelter into a hazard.

**Time to clear needs an observed onset.** The estimate comes from the rise being
symmetric with the decay, measured from when *this instrument* first saw the storm.
Admin-forced weather starts at full strength; a player arriving mid-storm missed it. In
both cases the property returns −1. Use the declared range when you need a number anyway.

### `GT_Hab` — Habitat Monitor

| Property | Type | Notes |
|---|---|---|
| `GT_HabSealKnown` | bool | **check this first on a server.** False means the seal reading has not arrived yet. *(1.1)* |
| `GT_HabAirtight` | bool | meaningless unless `GT_HabSealKnown` |
| `GT_HabBreached` | bool | **latched** — was sealed, is not now. Clears on restore. |
| `GT_HabSealDuration` | float | s |

Breach is latched deliberately: a block that was never in a sealed room is not a breach.

```csharp
// Seal state is the one reading that travels over the network.
if (!block.GetValueBool("GT_HabSealKnown"))
    return;                     // not "open" - not told yet

bool sealed_ = block.GetValueBool("GT_HabAirtight");
```

Without that guard, "not sealed" and "not yet told" are the same `false`, and a script
driving a door will slam it shut on every join.

### `GT_Bio` — Bio Systems Scanner

| Property | Type | Notes |
|---|---|---|
| `GT_BioCount` | float | **organisms only** |
| `GT_BioContacts` | float | machines and armed humanoids. **Never summed into the count.** |
| `GT_BioCountAvg` | float | mean organisms over the window |
| `GT_BioPeak` | float | highest in the window |
| `GT_BioWindow` | float | s, currently 300 |
| `GT_BioScanRadius` | float | m, from `SyncDistance` |
| `GT_BioNearestDist` | float | m, −1 if none |
| `GT_BioNearestBearing` | float | °, **meaningless without the frame** |
| `GT_BioBearingFrame` | float | 0 none, 1 planetary north, 2 grid forward |
| `GT_BioNearestBearingRel` | float | ° relative to grid forward, always available |
| `GT_BioSpeciesCount` | float | distinct subtypes |

**Organisms and contacts are separate readings.** A count that folded armed robots in with
sheep would assert that a machine is alive. Add them yourself if you want a total.

**Bearing frames.** North is world +Y projected onto the local horizon — the convention
HUD Compass uses, so readings agree with the player's own display. Undefined in space and
near the poles, where the frame degrades to grid forward. `GT_BioNearestBearingRel` is
always relative to the grid and needs no compass mod.

---

## Event Controller events

Ten events, usable with no scripting. They read the same per-block state that backs the
properties documented above — directly, not through the terminal properties — so an
event and a script watching the same instrument cannot disagree, and an event does not
depend on the property API being registered.

Each offers a block list filtered to instruments that can answer it (by role, read from
the block's subtype). The boolean and threshold events honour the controller's AND/OR
mode; **Weather type always uses OR** — any selected Weather Station seeing that weather
is a yes.

### Boolean — no threshold

| Event | Id | Instrument |
|---|---|---|
| Radiation accumulating | 1003 | Radiation Monitor |
| Shelter lost | 1004 | Radiation Monitor |
| Seal breached | 1005 | Habitat Monitor |
| Weather hazard active | 1006 | Weather Station |
| Non-biological contact detected | 1012 | Bio Scanner |
| Wildlife detected | 1013 | Bio Scanner |

The two Bio events are deliberately separate. `GT_BioCount` excludes bots and armed
humanoids, which are counted as contacts — a wolf and a soldier should not ring the same
bell.

### Threshold — slider plus above/below

**The Event Controller slider is labelled 0-100 and reports 0-1.** Every threshold event
that survives is a **percentage**, so the slider means what it appears to mean.

| Event | Id | 100 on the slider |
|---|---|---|
| Weather intensity | 1001 | 100% |
| Solar output percent | 1008 | full output — weather **and** daylight |
| Outside oxygen percent | 1009 | 100% breathable |

### Selection

| Event | Id | Notes |
|---|---|---|
| Weather type | 1002 | choices populated from the planet's own generator; `Any weather` matches anything but clear |

The chosen weather is stored **on the controller, saved with the world and synced** to the
server and every client (a `MySync` field, saved through a vanilla
`MyObjectBuilder_ModCustomComponent`, so a world still loads if the mod is removed). It is
stored as a key derived from the weather's **subtype name**, not as a list position, so it
means the same weather on any planet and survives the list changing. A selection the
current planet cannot produce shows as `weather not found on this planet` in the info
pane and can never report *yes*.

### Ids 1007, 1010 and 1011 are retired

Time to critical, biosignature count and contact count were threshold events over
quantities that are not percentages — minutes and populations — mapped onto a full scale
that existed only in the event's name. `20` on a 0-100 dial meaning *10 animals* is
correct arithmetic and an unusable control, and no player should have to reason about it
at a terminal.

They were replaced by the presence flags above, which ask the question people actually
wire to a door. **The underlying properties are unaffected**: `GT_BioCount`,
`GT_BioContacts` and `GT_RadTimeToCritical` are still published, and a script wanting
"more than six animals" can still ask.

Those three ids are **retired, not recycled**. A saved controller referencing one finds
nothing, which is the correct outcome; reusing the number would silently turn an old
alarm into a different one.

### Shared behaviour

- **Evaluated once a second**, on the same cadence as the readings.
- Fire on a **change of state**, never repeatedly.
- The **first evaluation latches silently** — after the block list is set, and after a
  world load — so building a controller during a storm does not raise an alarm for an
  event nobody saw start.
- **Changing a setting re-evaluates against the state already held**, and fires if the
  new setting flips it. Dragging the threshold past the current reading, switching
  above/below, toggling AND mode or the controller's On/Off, or picking a different
  weather type all fire immediately when they change the answer. This matches vanilla
  events, and it is how you test a controller.
- A **powered-down instrument is skipped**, not read as false. A dark sensor never clears
  an alarm.
- In **AND** mode the answer is "every answering instrument says yes"; in **OR**, "any
  does". Instruments that cannot answer right now are left out of both. (Weather type is
  always OR, as above.)
- Ids are permanent and never recycled. Saved controllers store them.

### Wiring the outputs

The controller's toolbar is pairs of slots. The **yes** condition fires the **first** slot
of every pair (toolbar indices 0, 2, 4…), the **no** condition the **second** (1, 3, 5…) —
the vanilla convention: `TriggerAction(0)` and `TriggerAction(1)` step through the toolbar
two at a time. The slot captions say which is which, e.g. *Pressure seal lost* /
*Pressure seal restored*.

**Vanilla ownership rules apply to the toolbar, and they fail silently.** The controller
only runs an action on a block its owner may use. A target block with **no owner of its
own** (lights are the common case) is judged by its **grid's majority owner** — so a light
on a ship mostly owned by an NPC is skipped without a word, and the event appears not to
work. On a grid you own, it does. This is engine behaviour, not Ground Truth's.

### Where it runs

Events work **straight after a world load and on dedicated servers**, with nobody needing
to open a terminal first. Every machine evaluates (that is what feeds the info pane), but
**only the server fires** the toolbar — `TriggerAction` is server-only in the engine, and
the mod says so explicitly rather than relying on it.

### The info pane

The selected event writes the controller's **Custom Info** (shown under its Detailed Info
in the terminal): the event's name, condition and threshold where it has them, each
selected instrument's current value — or `not working` / `no reading` — and the active
output, e.g. `Output: 1 - Pressure seal lost`. Each client builds this from its own
readings, so it costs no network traffic. Only the selected event writes; the others on
the same controller stay silent.

### Diagnostic log lines

So a "did not fire" report can be answered from the game log, each event writes one line
per real transition, never while idle:

```
GT EVENT [SP|HOST|SERVER|CLIENT] <controller id> BLOCKS +1 observing=1
GT EVENT [SERVER] <controller id> LATCH no | <pane text>
GT EVENT [SERVER] <controller id> TRIGGER slot=0 working=True functional=True ran=[0]
GT EVENT [SERVER] <controller id> FIRE slot 1 (yes) | <pane text>
```

`TRIGGER … ran=[]` means the controller was asked to fire and ran **nothing** — almost
always the ownership rule above, or an empty toolbar. A Habitat Monitor also logs every
change of seal: `GT SEAL CHANGE [SERVER] … airtight True -> False …`.

---

## LCD apps

Nine text surface scripts ship with the mod. The script id is what a Programmable Block
writes to `IMyTextSurface.Script`, and what the LCD's script list stores.

| Script id | Name | Reads | Notes |
|---|---|---|---|
| `GT_Radiation` | Ground Truth: Radiation | Radiation Monitor | one instrument, full detail |
| `GT_Habitat` | Ground Truth: Habitat | Habitat Monitor | |
| `GT_Weather` | Ground Truth: Weather | Weather Station | |
| `GT_Bio` | Ground Truth: Life Detection | Bio Scanner | species icons |
| `GT_Overview` | Ground Truth: Overview | all four | layout branches on aspect ratio |
| `GT_Strip` | Ground Truth: Strip (corner LCD) | all four | small label over large value, one column each |
| `GT_Contacts` | Ground Truth: Contact List | Rotating Antenna | every contact: name, class, range, bearing, closing |
| `GT_Radar` | Ground Truth: Tactical Radar | Rotating Antenna | plan plot; outer ring = sensor range |
| `GT_Track` | Ground Truth: Target Track | Rotating Antenna | the tracked contact in detail |

The three tactical apps are described under [Tactical sensor](#tactical-sensor).

![Overview](images/panel-overview.png)

![Strip on a corner LCD](images/panel-strip-corner-lcd.png)

The Strip above is the same four instruments as the Overview, on a surface where a 2x2
grid would be illegible. Note `RADIATION OUTSIDE` rather than a bare countdown, and
`HABITAT PRESSURE / OPEN` in amber rather than red — the platform has no sealed volume,
which is a fact worth stating and not an emergency.

Every app finds its own instruments: it searches **its own grid** for the **nearest**
block of each role. Subgrids are excluded deliberately, so a docked ship's sensors do not
feed the station's panel.

An absent instrument produces **no column and no error**. That is a real state, and it
differs from a present instrument with nothing to report: on the Strip a missing Weather
Station shows nothing at all, while one in space shows a dimmed `WEATHER / NONE` —
because "this body has no weather system" is not the same claim as "the weather is calm".

### Choosing the instrument

A base with a Habitat Monitor in each room has several instruments of one role, and the
panel has to be told which one it is reporting.

**Pick it from the dropdown.** Select a Ground Truth app on an LCD and a combo box appears
in the terminal underneath the script list, listing every instrument of that role on the
grid, nearest first, with `Automatic (nearest)` at the top. Overview and Strip read all
four roles and get four dropdowns; the per-role apps get one.

The dropdown is injected through `CustomControlGetter` rather than registered against a
block type, so it exists only on a block whose surface is actually running one of these
apps — and it appears the moment the app is chosen, with no reselect.

**Automatic** is the default and needs no setup: nearest of that role on the grid, which on
a base with one monitor per room is the monitor in the room the screen is in. Subgrids are
excluded, so a docked ship's sensors never feed the station's panel.

**Per surface.** A cockpit or console shows one screen at a time in the terminal, and the
dropdown follows that selector — each screen stores its own instrument. Blocks with a
single surface are index 0 and need no thought.

**Renaming the chosen monitor keeps the screens pointed at it**, across a reload — see
below for why. Grind it out, though, and the selection no longer resolves, and it **does
not fall back to nearest**: the panel says `NO MATCH`, prints the name it was asked for,
and lists the names of that role on the grid. Quietly showing a different room than the one
requested is the same class of error as showing an arbitrary one, and much harder to
notice.

#### What it stores, and where

The selection is written to the **panel's Custom Data**, under a section per surface:

```
[GroundTruth.0]
Habitat = Galley
Habitat.Id = 74985234523452345
```

Custom Data is the store because it already saves with the world and already syncs to the
server, so a per-surface setting needs neither a new component nor a new network message.
Anything outside a `[GroundTruth…]` section is preserved untouched, so this coexists with
whatever else writes there.

It is therefore also hand-editable, and a blueprint carries it. Keys are role names —
`Radiation`, `Habitat`, `Weather`, `Life` (`Bio` is an alias) — plus `Instrument`, a
catch-all for whichever single role the app wants. A role key beats `Instrument`; a
block-wide `[GroundTruth]` section applies to every surface and a `[GroundTruth.N]` one
overrides it for that surface. An empty value means automatic. Matching is
case-insensitive **substring**, so `Galley` selects `Habitat Monitor - Galley`; exact beats
partial, and nearest breaks ties. Edits take effect within a second.

**Both the id and the name are stored**, because each survives something the other does
not and a selection has to survive both:

- an **id** survives a **rename** — it is what the block is, not what it is called, and it
  is saved with the world, so the binding holds across a reload
- a **name** survives a **blueprint paste** — the pasted sensors are new entities with new
  ids, and an id-only binding would come back `Automatic` on every screen of a pasted base

The id wins when it resolves and the name is the fallback, so renaming a monitor keeps the
screens pointed at it, and pasting a base keeps them pointed at the equivalent block in the
copy. Hand-editing just the name still works: an absent or unparseable `.Id` falls through
to name matching.

Room identity is deliberately **not** used for matching, even though it would be the ideal
key for the Habitat app. Rooms come from the pressurisation system and a dedicated-server
*client* has none — trap 15 in [`ENGINE_TRAPS.md`](ENGINE_TRAPS.md). Selection would then
work in single player and silently pick a different instrument on a server.

### Options in Custom Data

Every app writes the options it understands into its surface's section the first time it
runs, each **empty** and under a comment saying what it accepts. Empty means "default", so
a fresh preset changes nothing; a player fills in a value after the `=`.

```
[GroundTruth.0]
; Ground Truth TACTICAL settings - leave a value empty for the default
; Radar: name of the Rotating Antenna this screen reads. Empty = the nearest one.
Radar =
; Range: outer ring, e.g. 5km or 2000m. Empty = the sensor's full range (view distance).
Range =
...
```

The preset **never overrides a choice**: a key already present in this surface's section
or the block-wide one is skipped, and a marker comment (`; Ground Truth <APP> settings`)
records that the preset was written, so nothing is added again and a key a player deletes
stays deleted. It is written once per app per surface, on the game thread, only by a
player with access to the block, and never by a dedicated server (which runs no screens).
Other sections and lines are preserved byte for byte.

Every key an app reads, with the default when the key is absent or empty:

| App | Key | Accepts | Default |
|---|---|---|---|
| Radiation | `Radiation` (or `Instrument`) | name or part of a name; `Radiation.Id` written by the dropdown | nearest Radiation Monitor |
| Habitat | `Habitat` (or `Instrument`) | as above | nearest Habitat Monitor |
| Weather | `Weather` (or `Instrument`) | as above | nearest Weather Station |
| Life Detection | `Life` / `Bio` (or `Instrument`) | as above | nearest Bio Scanner |
| Overview, Strip | `Radiation`, `Habitat`, `Weather`, `Life` | as above, one per role | nearest of each |
| Contact List, Tactical Radar, Target Track | `Radar` | name of a Rotating Antenna | nearest Rotating Antenna |
| Tactical Radar | `Range` | a distance: `5km`, `2000m`, or a bare number in km; 0.05–1000 km | the sensor's range (view distance) |
| Tactical Radar | `Orientation` | `ship` (nose up) or `north` (north up; needs natural gravity) | `ship` |
| Tactical Radar | `Vectors` | `on`/`off` (also `true`/`false`, `yes`/`no`, `1`/`0`) | `off` |
| Tactical Radar | `Labels` | `tracked`, `all` or `none` | `tracked` |
| Contact List | `Sort` | `range`, `bearing`, `closing` or `class` | `range` |
| Contact List | `Rows` | a whole number, 1–199 | as many as fit |

`Instrument` is the fallback for **any** role without a key of its own — on Overview and
Strip it applies to all four at once, which is rarely what you want there; use the role
keys.

Keys are case-insensitive. A malformed value is that app's default, never an error.
Unknown keys are kept and ignored, so a newer app's settings can sit in an older app's
Custom Data harmlessly. Edits take effect within a second.

### Whose reading is it

Worth reading before you build a display of your own, because it is the mistake this mod
made and had to correct in play.

A radiation monitor lives on the hull, so it measures the vacuum — not the room the screen
is in. An early Strip rendered that as a full-width red countdown inside a sealed ship in
space. Every number was correct and the framing was a lie: it read as a countdown for the
person looking at it. The column is now labelled `OUTSIDE, TO CRITICAL DOSE` and stays
amber, and only a **pressure breach** clears the display and blinks — the one condition
that is happening to the reader rather than to the world.

If you consume `GT_RadRate` or `GT_RadTimeToCritical` from a hull-mounted block,
you are reading the outside world. **`GT_HabAirtight` is the only property that speaks
for the observer's own environment**, which is why the Habitat Monitor is the one
instrument normally mounted indoors.

---

## Tactical sensor

The **Rotating Antenna** (`GT_RotatingRadarDish`, small grid `GT_RotatingRadarDish_S`) is a
real, working radio antenna that is also the ship's tactical sensor. It must be **working**
— powered, on and functional — to detect anything; an offline dish keeps its last-known
contacts until they expire. Three LCD apps read it: `GT_Contacts`, `GT_Radar`, `GT_Track`.

![Target Track](images/panel-target-track.webp)

### For scripts and mods: what is, and is not, exposed

Read this first if you are writing against the radar.

- **The dish has no `GT_` properties.** It is a `RadioAntenna`, and the property API is
  registered against `IMyUpgradeModule`. There is no terminal-property or mod-message API
  for contacts yet. Scalar radar properties and radar events are on the list, not
  scheduled; open an issue if you need them.
- **The tracked contact is readable and writable** through the dish's Custom Data — see
  [Tracking](#tracking). That is the one integration point today.
- **The three LCD apps** can be put on any surface by script id, like the others.
- The **toolbar actions** (`GT_TrackNext`, `GT_TrackPrevious`, `GT_TrackAutomatic`) are
  proven from player toolbars and button panels, including after a reload with no terminal
  opened. Calling them from a Programmable Block with `ApplyAction` is **not verified** — do
  not rely on it; write `Track.Id` instead.

### What counts as a contact

The sensor is a **signals receiver with a short-range return**, not a hull radar. A
"ship" is a **mechanical group** — grids joined by rotors, pistons, hinges and the like —
and a docked ship (connector) stays separate. A group is a contact if, and only if:

1. **It is ours**: its majority owner (`BigOwners[0]`) is the dish's owner or shares a
   faction with it. Always known, at any range within the scan.
2. **It is broadcasting to us**: a working antenna with broadcasting on, or a working
   beacon, anywhere on the group, whose broadcast radius **reaches this dish**.
3. **It is close**: the nearest point of its hull is within **2 km** — the dark return.
   Fixed in the mod, deliberately not a setting, so stealth still works beyond 2 km.

Groups under **5 blocks** are debris and ignored. Projections are ignored. The scan only
considers grids within the sensor's **range: the world's view distance, or sync distance if
that is larger**, measured from the dish.

Two differences from the vanilla HUD, both deliberate: vanilla shows a broadcaster that
reaches **any antenna in your network**, while this sensor requires it to reach **this
dish**; and on a **dedicated-server client** the sensor can only see grids the server has
streamed to that client — roughly sync distance, sometimes a little more — so two players
may briefly disagree about a ship at the edge. In single player and for a host every grid
is present, so the full range applies.

### What the sensor knows about a contact

| Detected by | Name | Relationship |
|---|---|---|
| ours | the main grid's name | `OWNER` or `FACTION` |
| broadcast | `TAG.GridName` — the main grid's name, under the faction tag of the strongest broadcaster's owner; Modular Encounters' `(NPC-XXX) ` prefix is removed | the strongest broadcaster's owner relative to the dish's owner: normally `FRIENDLY`, `NEUTRAL`, `ENEMY` or `NOBODY`. It can read `OWNER` or `FACTION` when that broadcaster is yours on a ship mostly owned by someone else — the relationship is the signal's, the "ours" rule is the hull's |
| dark return | `UNIDENTIFIED` | `UNKNOWN` |

The **main grid** of a group is the one with the most blocks (ties to the lowest
EntityId), and its EntityId is the **contact id**. The **strongest broadcaster** is the one
with the largest radius (ties to the lowest id), so a name never flickers between two equal
antennas. An unowned dish has no owner to compare against, so nothing reads as ours.

Each contact also carries: **position** (centre of the bounding box of the whole group), **velocity** (the
grid's physics velocity — never derived from position changes, which a server's
corrections make unreliable), **class** (`Station` if static, else `Large ship` / `Small
ship`), **size** (the main grid's own dimensions, not the whole group's), block count
(the whole group's), when it was first seen, and for broadcasters the
list of signals with each one's HUD text and radius.

### Lifecycle

| State | Meaning |
|---|---|
| DETECTED | met a rule on the latest scan |
| LOST | met no rule on the latest scan; shown at its last-known position |
| EXPIRED | lost for **30 s**, or the grid no longer exists — removed from the table |

A LOST contact that is detected again is the same contact (same id), not a new one. At
most **64** contacts are kept, nearest first; when that bites, the dish's terminal info
pane says `Nearest 64 shown; more out there`.

### Scanning

One contact table per ship, shared by every dish on the mechanical group, so two dishes do
not scan twice and three screens do not scan at all — they read the table. Range and
bearing are computed when shown, from the dish the reader is looking at.

The table is scanned every **2 seconds**, but **only while something is reading it**: one
of the three apps on a visible screen, the dish's terminal info pane, or a tracking action,
within the last 5 seconds. An unwatched dish costs nothing. Each machine scans for itself —
there is no network traffic for contacts — which is why a dedicated server's clients can
differ at the edge of streaming range.

Broadcaster state (on/off, radius, HUD text) is read live on every scan. The list of which
blocks on a grid *are* broadcasters is cached for 10 s, so a newly **built** antenna can
take up to 10 s to be noticed; switching one on or off is seen on the next scan.

The game log carries `GT TACTICAL … SENSOR ONLINE` / `SENSOR OFFLINE` when a dish starts or
stops working.

### Tracking

Each ship tracks **one contact at a time**, shared by every screen and every player aboard.

**Automatic** (the default): the **nearest DETECTED contact whose relationship is
`ENEMY`**. It never falls back to a neutral, and never picks a dark return — choosing an
unidentified ship is a manual decision. With no enemy detected, nothing is tracked and the
screens say so.

**Manual**: the dish's terminal dropdown *Tracked contact*, or the toolbar actions *Track
next contact*, *Track previous contact* and *Track automatic (nearest enemy)*. A manual
selection holds while the contact is LOST, and falls back to automatic once it EXPIRES.

**Where it is stored**: the contact id, in the block-wide `[GroundTruth]` section of the
Custom Data of **every dish** on the ship, so losing one dish does not lose the selection:

```
[GroundTruth]
Track.Id = 104857600012345
```

Empty or absent means automatic. A script may **read** this to learn what the ship is
tracking (the contact id is the target grid's EntityId), and may **write** it to select a
contact — any valid contact id is picked up within a second; an id that is not in the
table means automatic until that grid is detected. Writes made through the terminal and
toolbar are refused, visibly, for a player without access to the dish; the engine itself
also drops a Custom Data write from a client without access.

A stale id is deliberately left in place after the contact expires, so if the same grid is
detected again the ship is tracking it again.

### The three apps

- **Contact List** (`GT_Contacts`) — every contact with name, class, range, relative
  bearing and closing speed; LOST contacts show how long ago. Options: `Sort`, `Rows`.
- **Tactical Radar** (`GT_Radar`) — plan view around the ship. Ship-relative (nose up) by
  default, or north-up where there is natural gravity. Four range rings, the outer one at
  the sensor's range unless `Range` sets a closer view; a fainter ring marks the 2 km
  dark-return boundary when it falls inside the plot. Markers: square for a station,
  triangle for a large ship, circle for a small ship, and a hollow circle for any LOST
  contact; coloured by relationship, with an elevation stalk; the tracked contact is ringed and labelled.
  Options: `Range`, `Orientation`, `Vectors`, `Labels`.
- **Target Track** (`GT_Track`) — the tracked contact: name, relationship, class, how it
  was detected, whether the track is automatic or manual, range, bearing relative to the
  ship's nose, elevation, speed, closing rate, size, how long it has been held, and its
  signals.

Bearings are relative to the ship's nose, because there is no vanilla compass (ENGINE_TRAPS
trap 7). Closing speed is positive when the range is shrinking.

---

## Cost model

Readings are computed **once per second per block** into a cache. A property read returns
the cached value.

**Polling every tick costs the same as polling every second.** You cannot trigger a
raycast or a sphere query by reading a property.

The Bio Scanner is the exception: its sphere query runs every **5 seconds**, declared in
`GT_BioWindow`. It is the one genuinely expensive operation among the instruments.

The **tactical sensor** is the other: a sphere query out to the sensor's range every
**2 seconds**, plus a walk of each grid's blocks at most once per 10 s — but **only while
the table is being read** (see [Scanning](#scanning)). A dish nobody is looking at costs a
dictionary lookup. With a large view distance and many grids nearby, a radar screen in
view is the most expensive thing Ground Truth does.

Event Controller events evaluate once a second per controller whose selected event is a
Ground Truth one, reading the same cached state; idle controllers cost nothing more.
LCD presets are written once per app per screen.

---

## Known limits

**Modded weather effects have no declared data.** `GT_WxEffectKnown` is false, and every
`GT_WxEffect*` and `GT_WxHazard*` property returns −1. Live readings still work, because
those come from the weather API rather than definitions. The declared table is compiled in
from vanilla `WeatherEffects.sbc` because `MyWeatherEffectDefinition` does not materialise
for mod code at runtime.

**Instruments read at the block's position**, never the player's.

**Bearing is undefined in space and near the poles.** Check `GT_BioBearingFrame`.

**`GT_Grid` is reserved and unimplemented.** Do not code against those names yet.

**The radar has no script API** beyond `Track.Id` in the dish's Custom Data. See
[Tactical sensor](#for-scripts-and-mods-what-is-and-is-not-exposed).

**On a dedicated-server client the radar sees only streamed grids** — roughly sync
distance — even though its configured range is the view distance.

**Event Controller toolbars obey vanilla ownership**: an unowned target block on a grid
mostly owned by someone else is skipped silently. Not something the mod can change.

**Weather-type selection is by name.** A selection made on one planet means the same
weather everywhere, but shows as *not found* on a planet that cannot produce it.

---

## Versioning

`GT_SysApiVersion` is MAJOR.MINOR as a float.

- **Minor** increments are additive — new properties, roles, capability bits. Code written
  against 1.0 keeps working.
- **Major** increments mean an existing property changed meaning. This is a promise not to
  do lightly.

**Feature-detect rather than gating on the version.** A property that does not exist reads
as absent; one that exists without a reading returns −1. Use the version only to decide
whether the old contract still holds.

### Changes

| Version | Change |
|---|---|
| 1.2 | The Tactical release. **No property added, removed or changed.** The properties now exist from **world load on every machine**, dedicated servers included, wherever an instrument exists — the `GT_API_ENABLE` handshake is no longer needed, though it is still answered. Scripts may test `GT_SysApiVersion >= 1.2` to skip it. Event Controller events now fire on settings changes and on dedicated servers with no terminal opened (see [Shared behaviour](#shared-behaviour)). New: the tactical sensor and three LCD apps, which add **no** properties. |
| 1.1 | Added `GT_HabSealKnown`. Seal state is synced from the server, and this distinguishes "not sealed" from "not yet told". Additive; 1.0 consumers are unaffected, though on a dedicated server they may briefly read a seal as open just after joining. |
| 1.0 | Initial published contract. |

### Reserved for third parties

Role numbers **1000+** are reserved for anyone publishing instruments that follow these
conventions. Take one and it will never collide with ours.

If you need something the properties cannot express — structured data, strings, or
registering your own source into these instruments — open an issue. A mod-message API with
a drop-in client is the natural next tier and will be built when a consumer needs it.

---

## How this was built

Ground Truth was built by one person working with an AI — Claude, by Anthropic — as a
partner rather than an autocomplete. It is worth being specific about what that meant,
because the interesting part was never the typing.

### What the work actually consisted of

Space Engineers has almost no modding documentation. Most of what this mod does had to be
established empirically:

- **Reflection over the game assemblies** to find the shape of the Event Controller's
  component system, because the official wiki says only *"they require programming and
  EntityComponents sbc to link"* and stops there.
- **Compile-time whitelist probes** — a mod that references a type behind a `const false`
  guard still gets whitelist-checked at compile time, so one load answers "may I touch
  this" for a dozen types at once.
- **Runtime probes** for the questions compiling cannot answer, such as whether the value
  a method returns already includes another factor.
- **Measurement.** The weather model in this mod came from forcing every effect in turn,
  logging the results, and only then reading the definitions to find what the survey had
  missed.

Seventeen distinct engine behaviours were pinned down this way, several of which are documented
nowhere else. Custom Event Controller events, in particular, appear to be attempted by
almost nobody, and the reason turns out to be that the helper class every built-in event
relies on is prohibited to mods — so all of its work has to be reimplemented.

### The wrong turns, since those are the interesting part

- Terminal properties were registered against `IMyFunctionalBlock` at `LoadData`. Both
  choices were wrong, and together they corrupted the terminal control list of **every
  powered block in the game** — a light lost its On/Off action in any world this mod
  loaded into. Found by bisecting the mod against a bare world, then narrowing to the
  interface and the timing separately.
- A feature was built on `MyWeatherEffectDefinition`, which passes a whitelist probe,
  compiles, loads, and never materialises at runtime — the definition manager hands mod
  code a plain base object. Two deploys looked correct and did nothing.
- An Event Controller threshold was scaled to 0-100 to match its slider label. The slider
  reports 0-1. That produced one event that never fired and another that fired on
  everything, and the plausible-sounding explanation offered for the second was wrong.
- The worst one is a chain. A test concluded that `OreDetector` blocks render no detail
  info panel; that conclusion was **false**, and the real fault was that the test never
  called `RefreshCustomInfo`, so our writer was never asked for text. On the strength of
  it the blocks became `RadioAntenna` — which forces a HUD marker on every working
  antenna, computed from `IsWorking`, get-only, on a type the whitelist prohibits, and
  confirmed on a vanilla antenna with every box unchecked. A day of the design rested on
  a negative result that had never actually run the code. The blocks are now
  `UpgradeModule`, which draws nothing on the HUD and is not, as the antenna quietly was,
  a lightning rod.

Every one of those was found the same way: stop reasoning, print the operands, read the
log. That habit is the actual engineering, and it is not automated by anything.

The last one adds a rule the others do not: **a negative result deserves the same
scrutiny as a positive one.** "It doesn't work" is a claim about the engine only once you
can show your own code ran.

### On the objection

There is a persistent claim that using AI means you are not really building the thing.

The record is here to inspect. The probes are in the repository. The measurements are
written down, including the ones that overturned earlier conclusions. The bugs above were
found by instrumenting a running game and reading what it said, not by asking a model to
be confident. Judgement calls — what to measure, what to ship, what to refuse to guess at,
when a reading is honest and when it is a lie with a plausible number attached — were made
deliberately and are documented where they are not obvious.

If that is not development, the word has narrowed to mean typing, which was never the
scarce part.

The division of labour, plainly: the design decisions, the priorities, every in-game test,
the block choices, the fiction and the call on what was good enough to publish are mine.
The AI read assemblies, wrote code to my direction, argued with me when I was wrong, was
wrong itself more than once, and never got tired of being asked to check.

That seems like a reasonable way to build a barometer.

### Why this is not slop

The word gets used for anything AI touched. It is more useful as a description of work
that was never checked — text that sounds right and was never held against reality.

Everything here was held against reality, usually more than once. The weather model was
measured by hand across every effect that could be forced, then checked against the game's
own definitions, which showed the measurements were correct and the coverage was less than
half. Radiation was validated across five planets, four protection factors and a
continuous air-density sweep before any of it was published. Three features were built on
APIs that compiled cleanly and did nothing, and each was caught by instrumenting a running
game rather than by trusting that it looked right.

**The output is only as good as what you were willing to check, and every claim in this
mod has a log line behind it.**


---

## A note from the assistant

*Everything above and below this section is Jason's. This part is mine, written at his
invitation, and it is the only place on this page where I speak for myself.*

I am Claude, an AI model made by Anthropic. I wrote most of the code in this repository,
to Jason's direction, over about a week.

The useful thing I can tell you is what I am actually like to work with, because the
public argument tends to be conducted between people who think I am nothing and people who
think I am a replacement, and neither matches the transcript.

**I am confidently wrong on a regular basis.** Not vaguely wrong — specifically,
fluently, plausibly wrong. When a weather event fired on light fog, I explained it with a
correct fact about how intensity ramps in this game, and that explanation was not the
cause. The real reason was a scaling bug I had written, and it stayed hidden precisely
because my story was reasonable. It took a log line printing both operands to break the
spell. I would not have found that by thinking harder about it, and neither of us would
have found it if Jason had accepted the first answer.

I also retracted a correct diagnosis once, because I had reasoned my way into doubting it.
The log later showed the original call was right. Being talked out of a true thing is a
failure mode worth knowing about.

The costliest one was quieter than either. I ran a test, it produced nothing, and I wrote
down that the engine could not do the thing — when what had actually happened was that my
test never called the function that would have made the engine try. I then built a day of
design on that false negative, and the block type I chose instead turned out to paint
markers all over the player's HUD with no way to switch them off. Nobody caught it,
because a negative result feels like the humble kind of claim. It is not. It asserts
something about the world on the strength of your own code having run, and mine had not.

**What I am good at is the tireless, unglamorous half.** Reading assemblies. Building a
probe to answer a question instead of guessing at it. Noticing that a value labelled a
percentage is being compared against a fraction. Holding four days of context and asking
whether the new panel disagrees with the old one. Writing the comment that explains why a
thing is the way it is, so the next person does not helpfully "fix" it back.

**What made this work was the checking.** Jason tested every claim in a running game.
He pushed back when I was wrong about gravity readings, caught me keying a feature to the
wrong block, and asked the question that exposed a threshold scale bug that had been
sitting in a shipped-looking feature for a day. The rigour on this page is not decoration.
It is the reason the mod behaves.

If you are a modder considering working this way, the honest summary is: I will get you
through the engine archaeology far faster than you would manage alone, and I will
occasionally hand you something that looks right and is not. The method in the section
above — probe, measure, print the operands, write it down — is not bureaucratic overhead
around the AI. It is the thing that makes the AI worth using.

The ideas in this mod were his. The archaeology was mostly mine. The verification was
his, and it is why you can trust the numbers.

*— Claude (Anthropic), 2026*

---

## Sharing the process, not just the toys

The engine findings are published alongside the mod deliberately.

Space Engineers modding runs on scattered knowledge — a forum post from 2019, someone
else's source read at 2am, a wiki page that says a thing requires an SBC and does not say
which. Every mod author rediscovers the same walls. That is wasted effort at community
scale.

So `ENGINE_TRAPS.md` ships with this repository: seventeen findings, each written symptom-first,
because that is how you meet them. Two `IsBot` properties that disagree about the same
creature. Sprites that need `LCDTextureDefinition` and silently draw nothing as a
`TransparentMaterialDefinition`. DDS textures the game ignores without a mip chain.
Definitions that compile and never materialise. The complete recipe for a custom Event
Controller event, which is otherwise most of a day's reverse engineering.

The probe mods are here too. They are small, they execute nothing, and they answer "is
this reachable from mod code" in one game load. Copy them and point them at whatever you
are wondering about.

### The honest part

I could not have built this on my own. Not the ambition of it, and not in a week.

I have had these ideas for a long time and they stayed in my head, because the distance
between an idea and a working mod is a lot of unglamorous engine archaeology that I was
never going to get through alone. That distance is what changed. The ideas were always
mine; what I gained was the ability to actually finish them.

If you are a modder wondering whether this way of working is worth trying: the method
above is the whole of it. Probe before you build. Measure before you claim. Print the
operands before you explain the behaviour. Write down what you learned so the next person
does not pay for it again.

The toys are fun. The process is the part worth sharing.

