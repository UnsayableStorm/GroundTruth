# Ground Truth — Environmental Instruments

Sensor blocks for Space Engineers that measure what is actually happening around a grid,
and report it accurately. Radiation, weather, atmosphere, life.

No progression, no gating, no tech tree. The instruments measure; what you do with the
readings is your business.

**Workshop:** https://steamcommunity.com/sharedfiles/filedetails/?id=3781444888

---

## What is in this repository

| Path | Contents |
|---|---|
| `Data/` | the mod — block definitions, scripts, LCD textures |
| `Models/`, `Textures/` | art |
| `docs/INTEGRATION.md` | **API reference and integration guide** for scripters and modders |
| `docs/ENGINE_TRAPS.md` | **engine findings** — thirteen things that cost real time, published so nobody else pays for them twice |
| `probes/` | whitelist probe mods, ready to copy |
| `tools/` | the icon build pipeline |

`docs/`, `probes/` and `tools/` are excluded from the published mod.

---

## For scripters and modders

Start with [`docs/INTEGRATION.md`](docs/INTEGRATION.md). Around 75 terminal properties,
ten Event Controller events, documented conventions, and a stated version contract.

Short version:

```csharp
var sensors = new List<IMyUpgradeModule>();
GridTerminalSystem.GetBlocksOfType(sensors, b => b.GetValueFloat("GT_SysBlockRole") > 0);

float rads = sensors[0].GetValueFloat("GT_RadRate");   // -1 means "no reading"
```

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

`probes/` holds the probe mods these came from. Most are tiny and execute nothing behind a
`const false` guard, answering "can mod code reach this" in one game load. Two of them do
run — `PaneProbe` places candidate block types and reports what the terminal draws, and
`HudMarkerProbe` strips a component to see what breaks. Copy one and point it at whatever
you are wondering about.

---

## Credits

Block models by **MTGraves**, from the Naval Theme Prop Pack, used with explicit
permission.

Species icons derived from **Twemoji**, © Twitter Inc and contributors, CC-BY 4.0,
modified for LCD use.

Subpart rotation adapted from **Digi's** public spinning-subpart example.

Built with AI assistance — Claude, by Anthropic. There is a full account of how that
worked, including the mistakes, in
[`docs/INTEGRATION.md`](docs/INTEGRATION.md#how-this-was-built).
