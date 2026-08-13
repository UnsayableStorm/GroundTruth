# SE Engine Traps

Findings that cost real time, are not documented anywhere obvious, and will bite again
on the next mod. Each one is stated as the symptom first, because that is how you will
meet it.

---

## 1. `IsBot` means opposite things on characters and on players

**Symptom.** Fauna detection returns zero with animals visibly present. Or: every animal
in range is classified as a human player.

There are two `IsBot` properties and they disagree about the same creature:

| Property | On an MES-spawned animal |
|---|---|
| `IMyCharacter.IsBot` | **false** |
| `IMyPlayer.IsBot` | **true** |

Fourteen animals were present and `IMyCharacter.IsBot` reported zero bots. Filtering on
it excludes nothing.

**Also wrong: `SteamUserId == 0`.** The obvious fallback fails too. SE gives bot players
a non-zero `SteamUserId` — they share the host's id with a different serial — so testing
for zero classified 29 animals as 29 people.

```csharp
// Correct: exclude real humans from a creature scan.
if (player.IsBot || player.SteamUserId == 0) continue;   // IMyPlayer.IsBot
```

**Consequence.** Any creature scan must filter on `IMyPlayer.IsBot`, obtained by walking
`MyAPIGateway.Players`, and must not trust the character-level property or the Steam id.

---

## 2. Script sprites need `LCDTextureDefinition`, not `TransparentMaterialDefinition`

**Symptom.** A custom sprite drawn by a text surface script renders nothing. No error, no
log line, no magenta placeholder. Layout around it is correct, so the space is reserved
and left blank.

Sprites drawn with `MySprite.CreateSprite(name, …)` resolve against the **LCD texture
list**. Vanilla `Danger` and `Cross` are defined this way:

```xml
<LCDTextureDefinition>
  <Id>
    <TypeId>LCDTextureDefinition</TypeId>
    <SubtypeId>GT_Bio_Wolf</SubtypeId>
  </Id>
  <TexturePath>Textures\Sprites\GT_Bio_Wolf.dds</TexturePath>
  <SpritePath>Textures\Sprites\GT_Bio_Wolf.dds</SpritePath>
</LCDTextureDefinition>
```

A `TransparentMaterialDefinition` with the same texture registers without complaint and
draws nothing. That is the trap: the wrong answer looks exactly like a missing file.

Both `TexturePath` and `SpritePath` are set. The file may be named
`LCDTextures_<Mod>.sbc`; the plain-name convention is not required.

Side effect: registered icons also appear in the LCD's own selectable image list.

### Diagnosing this class of bug

Draw a **stock sprite** beside the custom one. `Danger` is a good probe.

- stock draws, custom does not → registration or texture
- neither draws → the drawing code
- both draw → fixed

Two minutes of setup replaced a long guess between three plausible causes.

---

## 3. SE silently ignores DDS textures with no mip chain

**Symptom.** Same as above — a sprite draws nothing, with no error.

Every working sprite texture ships a full mip chain. A single-level DDS is skipped.

| | mips | flags | caps |
|---|---|---|---|
| working vanilla sprite | 9 | `0xa1007` | `0x401008` |
| Pillow default output | **0** | `0x81007` | `0x1000` |

Missing bits: `DDSD_MIPMAPCOUNT (0x20000)` in flags, `DDSCAPS_COMPLEX (0x8)` and
`DDSCAPS_MIPMAP (0x400000)` in caps.

**Pillow can encode DXT5 but cannot generate mipmaps.** The workaround is to encode each
level separately, concatenate the payloads, and patch the header — implemented in
`Ground Truth/tools/build_bio_icons.py::save_dds_with_mips`. No `texconv` needed.

Verify by arithmetic rather than by eye: a 128×128 DXT5 chain is
`128 + 16384 + 4096 + 1024 + 256 + 64 + 16 + 16 + 16 = 22000` bytes exactly.

---

## 4. MES does not trim tag values

**Symptom.** A spawn group passes every condition and its creatures never appear.

`TagParse.TagStringListCheck` adds list items verbatim. `[CreatureIds:deer_bot ]` is
stored as `"deer_bot "`, with the trailing space, and never resolves to a subtype.

Related: `CreatureIds` wants a **bot id from `Bots.sbc`**, not a character subtype.
`Wolf` is the bot; `Space_Wolf` is the character. The wrong one fails at spawn, after
passing every eligibility check.

---

## 5. Duplicate `SubtypeId`s across mods do not merge — one silently wins

**Symptom.** Your spawn group is rejected for a condition your file does not contain.

Ground Truth's `Earthlike_Animals_Wolf` was rejected with *Allowed Terrain Check Failed*
despite having no terrain tag. Another mod defined the same `SubtypeId` with
`[AllowedTerrainTypes:Snow]`, and its definition was the one loaded. Wolves had been
snow-only for months.

The same mod also defined `Earthlike_Animals` **twice within itself**, and the second
file's `[PlanetWhitelist:Medieval]` won — killing that group on every normal planet.

**Prefix every `SubtypeId` you author.** `LSN_`, `GT_`, anything unique. A generic name
is an invitation for another mod to redefine your content without either of you knowing.

MES weight-selects one mod and then one group per spawn event, so uniquely naming your
groups changes the odds, not the population.

---

## 6. MES fail reasons are the fastest diagnostic available

Static analysis of MES's source produced a confident wrong answer twice before the log
gave the real one in a single line.

```
/MES.SpawnDebug.SpawnGroup.true
/MES.SC                              (forces a Creature request; the log holds the LAST request only)
/MES.IGLSD.SpawnGroup                (needs the log type as a 5th element; output goes to CLIPBOARD)
```

`/MES.Info.GetEligibleSpawnsAtPosition` lists what *can* spawn. The debug log says *why
not* for everything else. Reach for the second one first.

---

## 7. There is no vanilla compass, so "north" is a mod convention

Vanilla SE surfaces no compass, no heading, and no north. GPS is absolute world XYZ,
which locates you but does not orient you. Planets do not rotate — the sun orbits them —
so there is no physical north to derive.

Consequence: any bearing an instrument reports is uninterpretable unless it agrees with
whatever the player's compass mod uses. **HUD Compass (1469072169) uses world +Y**
projected onto the local horizon:

```csharp
Vector3D RelativeNorth = PlanetCenter + new Vector3D(0f, Planet.AverageRadius, 0f);
```

Note it uses world +Y, not `planet.WorldMatrix.Up`. The two agree only for
axis-aligned planets. SE normally spawns them that way; it is not guaranteed.

**Match the convention rather than deriving your own.** A reading that agrees with the
player's HUD beats a more principled one that silently disagrees.

Also publish a **grid-relative** bearing beside the absolute one. Most players run no
compass mod at all, and "45 degrees off the nose" needs no external reference.

---

## 8. Custom Event Controller events — the complete recipe

**CONFIRMED WORKING 2026-08-09.** The official wiki says only *"They require programming
and EntityComponents sbc to link. Look at the More Events mod to get hints"*, which is
true and useless. This is the full shape, established by reflecting on the game
assemblies and confirmed in game.

**Why it matters.** Terminal properties serve Programmable Block authors — a scripting
audience. A custom event lets a player who has never written code wire a mod reading to
a beacon, a klaxon and a door using a vanilla block they already know.

### Four pieces, all required

**1. The component**

```csharp
[MyComponentType(typeof(GTEventWeatherIntensity))]
[MyEntityDependencyType(typeof(IMyEventControllerBlock))]
[MyComponentBuilder(typeof(MyObjectBuilder_GTEventWeatherIntensity), true)]
public class GTEventWeatherIntensity : MyEventProxyEntityComponent, IMyEventComponentWithGui
```

**2. Two object builders** — one for the component (`MyObjectBuilder_ComponentBase`), one
for its definition (`MyObjectBuilder_ComponentDefinitionBase`), both `[ProtoContract]`
`[MyObjectBuilderDefinition]`.

**3. A definition class**

```csharp
[MyDefinitionType(typeof(MyObjectBuilder_GTEventWeatherIntensityDefinition))]
public class GTEventWeatherIntensityDefinition : MyComponentDefinitionBase
{
    public long UniqueSelectionId;
}
```

**4. Two SBC files.** `EntityComponents.sbc` registers it — `TypeId` must equal the
component type name from `[MyComponentType]`, `SubtypeId` must be
`EventControllerBlockComponent`. `EntityContainers.sbc` attaches it to
`EventControllerBlock/*`.

Miss the SBC half and the component compiles, loads, and never appears in the dropdown,
with nothing logged.

### Containers MERGE — verified, not assumed

Vanilla declares all 22 stock events in a single container keyed `EventControllerBlock/*`.
Redeclaring that container in a mod **adds** to `DefaultComponents`; it does not replace
them. Stock events and the modded event appear together.

This was the real risk in the whole approach — a replace would have silently deleted
every vanilla event — and it is the opposite of the `SubtypeId` behaviour in trap 5,
where one definition silently wins. **Containers merge, definitions collide.** Do not
generalise either one to the other.

### `MyEventControllerGenericEvent<T>` is off limits

Every stock event delegates trigger state, hysteresis and action firing to this generic
helper. **Every member of it is prohibited to mods** — the type resolves, and each member
access is a whitelist error. That is almost certainly why so few mods add events.

Implement it yourself against the one thing that is exposed:

```csharp
ev.TriggerAction(0);   // "yes" toolbar slot
ev.TriggerAction(1);   // "no" toolbar slot
```

Threshold and condition come from `Sandbox.ModAPI.Ingame.IMyEventControllerBlock`:
`Threshold`, `IsLowerOrEqualCondition`, `IsAndModeEnabled`.

Two behaviours the generic would have given you for free, and which must be written by
hand or the event is unusable:

- **Fire only on state change.** A condition that stays true must not re-trigger every
  tick, or a klaxon never stops and a door never settles.
- **Latch the first evaluation without firing.** Otherwise placing the block while the
  condition is already true triggers an alarm for an event the player never saw happen.

### `UniqueSelectionId`

Vanilla occupies 0–22. Pick well clear of it and **never change it after publishing** —
saved Event Controller blocks store the id of the event they were set to, so changing it
silently repoints or orphans every existing setup.

### No update tick

An event component gets no update callback. Stock events subscribe to block events; a
world reading such as weather has none to hook, so poll it from a session component.

---

## 9. Definitions compile but never materialise for mod code

**Symptom.** A definition type is whitelisted, the code compiles, and at runtime every
lookup comes back empty. No exception, no log line.

`MyWeatherEffectDefinition` passed a compile-time whitelist probe cleanly - the type, its
fields, `MyDefinitionManager.Static`, `GetAllDefinitions()` and `GetDefinition(id)` were
all allowed. It still returned nothing:

```
WeatherDefProbe: enumerated=0  weather='Hailstorm'
                 byId(Hailstorm)=found but wrong type: MyDefinitionBase
```

The definition IS registered under that id. What comes back is a plain `MyDefinitionBase`
- the concrete type does not materialise for mod code, so `as MyWeatherEffectDefinition`
yields null and every field is unreachable. Enumeration finds zero for the same reason.

**Compiling is not the same as being delivered.** A compile-time whitelist probe answers
"may I reference this type", not "will the game hand me one". For definitions, prove it at
runtime with a cast check before building anything on top:

```csharp
var def = MyDefinitionManager.Static.GetDefinition(id);
var typed = def as MyWeatherEffectDefinition;
// def != null and typed == null is the failure mode. Log def.GetType().Name.
```

**What still works.** `MyPlanet.Generator` delivers a real `MyPlanetGeneratorDefinition` -
`WeatherGenerators`, durations and per-biome lists all read correctly. So this is not a
blanket rule about definitions; it is per type, and each one must be proven separately.

**The workaround.** The data is not secret - it is in `Content/Data/WeatherEffects.sbc`.
Generate the table from that file and compile it in, with the cost stated: modded content
is absent and must report "unknown" rather than zeros.

---

## 10. The Event Controller threshold is a fraction, not a percentage

**Symptom.** A threshold event never fires, or fires on everything. Both from the same
cause, depending which way the comparison runs.

The slider is labelled **0-100**. `IMyEventControllerBlock.Threshold` returns **0-1**.
Setting 60 yields `0.6`. Measured 2026-08-10:

```
GT threshold GT_Oxygen: value=89.67 thr=0.6 lower=True   ->  never met
```

The reading had been scaled to 0-100 to "match the slider". It matched the label and not
the property.

**This produced two bugs that looked like one plausible behaviour.** A weather-intensity
event set to 80 fired on light fog. The explanation offered at the time - that every
effect ramps to 100% intensity, so fog and a sandstorm read alike - was true, and was not
the cause. The comparison was `100 >= 0.8`, so *any* weather tripped *any* threshold. The
correct explanation only appeared once the numbers were printed side by side.

### Compare in the units the property reports

Percentage-like readings are usually already 0-1 - pass them through unscaled.

### Non-percentage readings do not belong on that slider at all

Minutes and counts cannot live on a 0-1 slider. The obvious workaround is to map them
onto a full-scale range and put the range in the event name, since nothing in the UI can
express it:

```
"Radiation time to critical, 100 = 30 min"
"Biosignature count, 100 = 50"
"Non-biological contacts, 100 = 20"
```

**This was tried, shipped, tested and withdrawn.** It is correct arithmetic and an
unusable control. Setting `20` to mean *ten animals* requires the player to hold a
conversion in their head that exists nowhere in the interface, and the first thing the
mod author said on meeting it in game was that the slider was very confusing - having
written the mapping himself.

Two better answers, in order of cost:

1. **Ask a boolean question instead.** Most uses of "how many animals" are really "is
   there an animal", which needs no slider and no explanation. Three count events were
   replaced by two presence events, and the feature got smaller and clearer.
2. **Build a custom slider with real units**, which the stock
   `MyEventGridSpeedChanged` does by declaring its own `Sync<float>` and its own control.
   That is the right answer when the quantity genuinely matters.

The underlying properties stayed published either way, so a script can still ask the
numeric question. A terminal control and an API are not obliged to offer the same
granularity.

### Print the numbers before explaining the behaviour

Two rounds of plausible reasoning about ramp curves were beaten by one log line carrying
`value` and `thr` together. A wrong comparison and a correct-sounding story about game
mechanics are indistinguishable until the operands are visible.

---

## 11. Borrowing a block type inherits everything it does, including what it draws

**Symptom.** Instrument blocks put a marker on the owner's HUD. Four sensors on a rover
is four markers in your face. Every relevant terminal box — *Show on HUD*, *Show ship
name*, *Broadcast* — is switched off, and the markers stay.

The blocks were `RadioAntenna`, chosen for its interface and its detail info pane, with
`MaxBroadcastRadius` set to 1 so the radio side did nothing. The radio side was never the
problem.

**The marker is not driven by any flag a player or a definition can set.**
`MyRadioBroadcaster.ShowOnHud` is:

- **computed**, not stored — get-only, and virtual on both the derived type and
  `MyDataBroadcaster`
- **derived from the block working**. Powering a block off removes its marker; that is
  the only input we could find
- **unreachable** — `MyRadioBroadcaster` is prohibited to mods, so even a settable flag
  would be out of reach

```
Error: The type or member 'MyRadioBroadcaster' is prohibited
Error: Property 'MyRadioBroadcaster.ShowOnHud' cannot be assigned to -- it is read only
```

**Confirmed against a vanilla antenna**, all boxes unchecked, from the probe's own log:

```
HUDPROBE SmallBlockRadioAntenna working=True broadcast=False hud=False shipname=False
```

Stock behaviour. Nothing to do with the mod.

**Removing the component works, then kills the game.** `MyDataBroadcaster` — the base —
*is* reachable, and `Components.Remove<MyDataBroadcaster>()` succeeds on all nine
instruments. Two seconds later NREs start on background threads, and clicking any block
in the terminal takes the process down in
`MyGuiControlGenericFunctionalBlock..ctor`. `MyRadioAntenna` dereferences the component it
no longer has. A suppression that works and then crashes is not a suppression.

**The lesson is the general one.** A block type is not a bundle of features you can
subset. `RadioAntenna` also brought `LightningRodRadiusLarge` and
`LightningRodRadiusSmall` — the instruments were quietly acting as lightning rods on
storm planets, which nobody had asked for and nothing in the definition mentioned.

Ground Truth moved to `UpgradeModule`: `IMyUpgradeModule` adds two members to
`IMyFunctionalBlock`, it renders the detail pane, and it has no broadcaster, no HUD
behaviour and no lightning rod. **Choose a base type for what it does not do.**

---

## 12. A test that never ran your code is not a negative result

**Symptom.** You conclude a block type cannot do something. It can. The design goes down
a worse path for a day.

The claim, written into an SBC header, this document and a published guide:

> OreDetector blocks render NO detail info panel at all. Verified against a vanilla ore
> detector, which is equally blank.

**It renders the pane perfectly.** Proved by `probes/PaneProbe`, where four block types —
upgrade module, sensor, camera and an ore detector clone — all display their text. The
ore detector was in that probe as the *control*, expected to stay blank, and it did not.

What actually happened is that the original attempt never called `RefreshCustomInfo`, so
the game never asked the writer for text. The pane was **empty, not absent**, and a
vanilla ore detector has nothing to say either — so the "control" confirmed the wrong
thing. Both halves of the evidence were consistent with a false conclusion.

That conclusion is what sent the design to `RadioAntenna`, which is what produced trap 11.

**How to not do this.** Instrument the mechanism, not the outcome. The probe logs
`PANEPROBE writer called for X` from inside the writer, which separates the two failures
that look identical on screen:

| Log line | Meaning |
|---|---|
| writer called, no text on screen | the pane genuinely does not render |
| no log line at all | the game never asked — your test proved nothing |

**A negative result needs the same rigour as a positive one.** "It didn't work" is a
claim about the engine only if you can show your code ran.

---

## 13. `UpgradeModule` has no power draw, and adding one has two steps

Useful if you want a functional block with no inherited behaviour:
`MyObjectBuilder_UpgradeModuleDefinition` declares exactly one field, `Upgrades`. No
`ResourceSinkGroup`, no `RequiredPowerInput`. With no `<Upgrades>` element it also does
nothing at all beside a refinery.

The cost is that power must be attached in code, and the obvious version is silently
wrong:

```csharp
var sink = new MyResourceSinkComponent();
sink.Init(MyStringHash.GetOrCompute("Utility"), info);
Entity.Components.Add<MyResourceSinkComponent>(sink);
// pane reports: required 0.0000 MW, powered: True
```

**A sink that asks for nothing is always satisfied**, so `IsPoweredByType` returns true
and means nothing. Attaching the component is not the same as entering the grid's power
ledger:

```csharp
sink.SetMaxRequiredInputByType(id, mw);
sink.SetRequiredInputByType(id, mw);
sink.Update();
// pane reports: max 0.0200, required 0.0200, current 0.0200
```

`current` tracking `required` is the proof the distributor is actually supplying it —
read all three, because required-without-current is a sink nobody is feeding.

**It does gate the block.** With the sink wired, cutting grid power drops `IsWorking` to
false, verified in game. That matters beyond flavour: if readings are served behind
`IsWorking`, an unpowered instrument stops answering instead of quietly serving stale
numbers.

---

## 14. `IsRoomAtPositionAirtight` wants the room's cell, not the block's

**Symptom.** A block reports that it is not in a sealed volume while the air vents beside
it report the room pressurised. It never reports sealed, anywhere, in any base.

```csharp
grid.IsRoomAtPositionAirtight(block.Position)     // wrong, and always plausible
```

`block.Position` is the grid cell the block **fills**. A block is not air, so there is no
room at that cell to be airtight, and the answer is no in a perfectly sealed compartment.

**What vanilla does.** Air vents test the cell in **front** of themselves:

```csharp
var faced = block.Position + Base6Directions.GetIntVector(block.Orientation.Forward);
grid.IsRoomAtPositionAirtight(faced);
```

**Facing beats a neighbour scan**, and it is worth understanding why, because the
neighbour version looks more robust and is worse. Consider two blocks on the same ship:

| Block | Faces | Correct answer |
|---|---|---|
| Habitat monitor on a room wall | inward | sealed |
| Radiation monitor on the outer hull | outward | vacuum |

Facing gets both right from one call. A scan of all six neighbours gets the second one
**wrong**: a hull-mounted block touches a pressurised cell on its inner face, so it would
claim a shelter it does not have — and shelter is exactly what a radiation reading is for.

**Why this survived testing.** Every test before deployment happened in a space that was
genuinely unsealed — a landing platform, an open frame — so "not sealed" was the right
answer for the wrong reason. It took a player standing in a finished, pressurised base to
produce the first case where the correct answer was yes.

That is the general shape worth remembering: **a predicate that is stuck on one value
looks correct for as long as you only test cases with that answer.** If a boolean has
never been observed to be true, it has not been tested — it has been watched.

---

## 15. A dedicated-server CLIENT has no pressurisation data at all

**Symptom.** A block reports no sealed volume on a ship whose air vents are green.
Everything works in single player. Nothing you change to the seal logic helps.

On a dedicated-server client, measured 2026-08-13:

```
grid.GasSystem                        -> null
grid.IsRoomAtPositionAirtight(cell)   -> false, always
GetOxygenRoomForCubeGridPosition      -> unreachable, GasSystem is null
```

Not an empty room graph. **No gas system object.** Pressurisation is simulated on the
server and the room graph is never replicated; a client receives the *consequences* —
oxygen levels on vents, the character's environment oxygen — not the structure.

**The second half of the trap is worse.** The obvious fix is "compute it on the server",
and a mod that computes readings on demand never runs there at all. Ours creates state
when something asks for it, and everything that asks — a terminal property, a text
surface script, an LCD app — is a client. The server had the code, had the data, and was
never asked a single question. Two days went into the client half before that was noticed.

**What works.** The server evaluates the blocks itself on a timer and pushes state to
clients. Send **on change**, not on a cadence: a seal is a boolean that never flips on a
base nobody is attacking, so idle traffic is zero packets rather than a steady trickle,
and a breach still arrives in about a tick. Add a slow heartbeat, say 30 s, and joining
players, dropped packets and late-streamed grids all fix themselves without being
detected.

**The general rule.** Ask of every reading: *does this data exist where I am computing
it?* Anything derived from pressurisation, and anything else the server owns
authoritatively, answers no on a client — and single player will never tell you, because
there the client is the server.

---

## 16. A mod folder that overwrites `modinfo.sbmi` publishes a NEW item every time

**Symptom.** You publish an update. The Workshop page shows a new timestamp. Servers and
subscribers keep running the old build. Repeat until you doubt your own code.

`modinfo.sbmi` in the mod folder is written **by the game** and carries the Workshop id:

```xml
<WorkshopIds><WorkshopId><Id>3781444888</Id><ServiceName>Steam</ServiceName></WorkshopId></WorkshopIds>
```

Any deploy script that mirrors a working copy over the published folder will replace it
with whatever stub lives in source control. With no id, publish has nothing to update, so
it **creates a brand new item** — silently, and with every appearance of success.

Ours also had the wrong root element, which the log said plainly and nobody read:

```
ERROR: Exception during objectbuilder read! (xml): MyObjectBuilder_ModInfo
<ModInfo xmlns=''> was not expected.
```

**Fix:** exclude `modinfo.sbmi` and `metadata.mod` from the copy, and have the deploy
print the id it is about to publish to:

```
Publish target: Workshop item 3781444888
```

A duplicate item created this way is also the most likely explanation for any
mysteriously duplicated mod in your own Workshop history.

---

## 17. `MyEntityComponentDescriptor` cannot read your registry, so it drifts

**Symptom.** Some variants of a block work and others do nothing. No error anywhere.

A component attaches by attribute:

```csharp
[MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false,
    "GT_HabitatMonitor", "GT_HabitatMonitor_S")]
```

Attributes take **compile-time constants**. They cannot read a dictionary, so a mod that
has correctly centralised its block registry still has one hand-maintained list of
subtypes, and nothing tells you when the two diverge.

Four new variants were added to the registry, the block definitions, the category and the
variant group — and not to the descriptor. Those blocks then had no power draw, and, once
the component also handled network registration, never received synced state either. The
panel simply waited forever while an identical block beside it worked.

**Fix.** Mirror the attribute's list as a readable array next to it, and compare the two
at load:

```csharp
var orphans = Instruments.SubtypesWithoutComponent(InstrumentPower.AttachedTo);
```

Log loudly for each. The lists still have to be edited together, but the next drift
announces itself at load instead of waiting to be found in front of a dead panel.
