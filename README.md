# Ground Truth: Environmental and Tactical Information Systems

Sensor blocks for Space Engineers that measure what is actually happening around a grid,
and report it accurately. Radiation, weather, atmosphere, life — and, through the
Rotating Antenna, which other ships are out there.

No progression, no gating, no tech tree. The instruments measure; what you do with the
readings is your business.

**Workshop:** https://steamcommunity.com/sharedfiles/filedetails/?id=3781444888

![The instruments, large grid](docs/images/blocks-array.png)

Radar domes, rotating sensor pods, wall-mounted dishes and the 1×5 comms mast — every
instrument comes in two silhouettes and both grid sizes, so an array can be built to look
like something rather than to look uniform.

---

## Screens

Each panel is an LCD app. No name tags and no setup — every app finds its own instruments
on its own grid, picking the nearest of each role, so a base with a Habitat Monitor per
room gets the right one on each screen. Where a base has several, a dropdown in the
terminal lists them and pins the screen to one; see
[`docs/INTEGRATION.md`](docs/INTEGRATION.md#choosing-the-instrument).

| | |
|---|---|
| ![Overview](docs/images/panel-overview.png) | ![Strip](docs/images/panel-strip-corner-lcd.png) |
| **Overview** — all four systems, laid out to fit the surface it is on. | **Strip** — for corner LCDs, where the other apps are unreadable. Small label, large value, colour for severity. |

| | |
|---|---|
| ![Radiation, exposed](docs/images/panel-radiation-exposed.png) | ![Radiation, safe](docs/images/panel-radiation-safe.png) |
| Exposed: dose accruing, shelter state `EXPOSED`, and how long you have. | Sheltered: the sun occluded, exposure genuinely zero. |

| | |
|---|---|
| ![Weather](docs/images/panel-weather-snow.png) | ![Habitat](docs/images/panel-habitat-open.png) |
| Weather, with what it is doing to solar, wind and oxygen — and an honest `UNKNOWN` for a clearing time whose onset was never observed. | Habitat: no sealed volume here, so planetary radiation is `NOT BLOCKED`. |

![Life detection](docs/images/panel-life-detection.png)

Life Detection reporting a nil return — *"no returns; the instrument has no opinion as to
why"*. An instrument that finds nothing has not proved the place is empty.

Every screen writes its options into the LCD's Custom Data the first time it runs — each
one empty, with a note saying what it accepts — so nobody has to look up a key or guess the
syntax. See [`docs/INTEGRATION.md`](docs/INTEGRATION.md#options-in-custom-data).

### Tactical displays

![Target Track](docs/images/panel-target-track.webp)

The **Rotating Antenna** is the ship's tactical sensor. It identifies any ship whose antenna
or beacon reaches it, picks up anything within 2 km whether broadcasting or not, and sees
as far as the world's view distance. Three apps read it: **Contact List**, **Tactical
Radar** and **Target Track**, which follows the nearest enemy by default — or whatever you
pick from the antenna's terminal or a toolbar button. How detection, identification and
tracking work, exactly: [`docs/INTEGRATION.md`](docs/INTEGRATION.md#tactical-sensor).

### Event Controller

Ten Ground Truth events appear in the vanilla Event Controller — seal breached, radiation
building, dangerous weather, a specific weather type, solar output, outside oxygen,
wildlife, non-biological contacts and more. They work on dedicated servers and straight
after a world load, fire when a setting change flips the answer, and show what each sensor
is reading in the controller's info panel.

---

## What is in this repository

| Path | Contents |
|---|---|
| `Data/` | the mod — block definitions, scripts, LCD textures |
| `Models/`, `Textures/` | art |
| `docs/INTEGRATION.md` | **API reference and integration guide** for scripters and modders — every property, event, LCD option and the tactical sensor's rules |
| `docs/ENGINE_TRAPS.md` | **engine findings** — twenty-one things that cost real time, published so nobody else pays for them twice |
| `docs/TACTICAL_PLAN.md` | the tactical sensor's design record: decisions, probe results, and what changed in testing |
| `probes/` | whitelist probe mods, ready to copy |
| `tools/` | the icon build pipeline |

`docs/`, `probes/` and `tools/` are excluded from the published mod.

---

## For scripters and modders

Start with [`docs/INTEGRATION.md`](docs/INTEGRATION.md). 76 terminal properties, ten Event
Controller events, nine LCD apps and every option they read, the tactical sensor's
detection rules, documented conventions, and a stated version contract (currently **1.2**).

Short version:

```csharp
// Find instruments by subtype: works even where the properties do not exist yet.
var sensors = new List<IMyUpgradeModule>();
GridTerminalSystem.GetBlocksOfType(sensors, b => b.BlockDefinition.SubtypeName.StartsWith("GT_"));

float rads = sensors[0].GetValueFloat("GT_RadRate");   // -1 means "no reading"
```

The properties exist from **world load**, on single player and dedicated servers alike —
no terminal needs opening and no request is needed (API 1.2). The radar has **no**
properties yet; the tracked contact is readable and writable as `Track.Id` in the Rotating
Antenna's Custom Data.

Role numbers **1000+** are reserved for anyone publishing instruments that follow the same
conventions.

---

## For anyone modding Space Engineers at all

[`docs/ENGINE_TRAPS.md`](docs/ENGINE_TRAPS.md) is the part most likely to be useful to
people who never install this mod. Each entry is written symptom-first, because that is how
you meet them:

- Two `IsBot` properties that give opposite answers about the same creature
- Sprites that silently draw nothing unless registered as `LCDTextureDefinition`
- DDS textures the game ignores when they have no mip chain
- Definitions that pass a whitelist probe, compile, and never materialise at runtime
- The complete recipe for a **custom Event Controller event**, which the wiki describes in
  one sentence and which is otherwise most of a day of reverse engineering
- The Event Controller threshold slider that is labelled 0-100 and reports 0-1
- Antennas that draw a HUD marker with every box unchecked, why no mod can stop it, and
  what happens if you rip the component out anyway
- Why a test that produced nothing may have proved nothing — a false negative that cost a
  day and picked the wrong block type
- Giving a block a power draw in code, and why the obvious version reports `powered: True`
  while asking for 0.0000 MW
- Why a dedicated-server **client** has no pressurisation data at all, and why the server
  will not compute it for you either
- A deploy script that silently publishes a **new Workshop item** every time, because it
  overwrote the file holding the id
- Component descriptors drifting from your own registry, because an attribute cannot read
  a dictionary
- A terminal hook that never fires on a dedicated server — where Programmable Blocks
  actually run
- `GetArtificialGravity` disagreeing with the HUD inside a planet's gravity
- A block type's terminal control list **frozen the first time anyone asks for it**: why
  touching it at world load strips Name and On/Off from every block in the game, and the
  moment when registering early is provably safe
- An `UpgradeModule` whose `IsWorking` ignores the power draw you gave it, so it keeps
  working on a dead grid

`probes/` holds the probe mods these came from. Most are tiny and execute nothing behind a
`const false` guard, answering "can mod code reach this" in one game load. Two of them do
run — `PaneProbe` places candidate block types and reports what the terminal draws, and
`HudMarkerProbe` strips a component to see what breaks. Copy one and point it at whatever
you are wondering about.

---

## Help wanted — visual work

The measuring is done and documented. What the project could genuinely use is someone with
an eye, because every visual decision here was made by an engineer optimising for legibility
and nothing else.

Four things, smallest first:

**Workshop imagery.** The page has honest screenshots taken to prove features work. It has
no header image, and no shot sequenced to show a newcomer in three seconds what this is.
That is a different craft from documenting a panel, and it is the gap most likely to decide
whether anyone installs the mod at all.

**Icons.** The G-menu icons are inherited from the source model packs. They are fine
individually and do not read as one family, which makes a deliberate eighteen-block shelf
look like a pile of borrowed parts.

**Panel layout.** The colour semantics are settled and load-bearing — green safe, amber
worth knowing, red happening to you, and the palette is documented in `TextPanels.cs`.
The *arrangement* is not sacred. Someone with real typographic sense could rework the
layouts inside those rules and the mod would be better for it.

**Block models.** The deep end. The meshes are MTGraves', used with permission and properly
credited, and they are why the instruments look as good as they do. Original models would
make this fully a Threshold Dynamics product rather than TD-branded naval props.

Open an issue or a pull request if any of that appeals. Code contributions are welcome
too, but the honest answer to "where would help matter most" is: not the code.

---

## Credits

Block models by **MTGraves**, from his Naval Theme Prop Pack and his comms pack, used
with explicit permission. Six textures the meshes reference are redistributed with them,
so the blocks look right without either pack installed.

Species icons derived from **Twemoji**, © Twitter Inc and contributors, CC-BY 4.0,
modified for LCD use.

Subpart rotation adapted from **Digi's** public spinning-subpart example.

Built with AI assistance — Claude, by Anthropic. There is a full account of how that
worked, including the mistakes, in
[`docs/INTEGRATION.md`](docs/INTEGRATION.md#how-this-was-built).
