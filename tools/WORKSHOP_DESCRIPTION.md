# Workshop description — draft

Steam BBCode, ready to paste. Kept in `tools/` so it does not ship with the mod.

Audience: **normal players**. Scripting and modding detail lives on the GitHub guide,
linked once near the bottom.

---

[h1]Ground Truth: Environmental and Tactical Information Systems[/h1]

[i]Threshold Dynamics, Survey and Assessment[/i]

Sensor blocks that tell you what is actually happening around you. Radiation, weather,
atmosphere, life - and now, who else is out there.

No tech tree, no research, no unlocks. Build them, read them, wire them to whatever you
want to happen next.

[h2]The instruments[/h2]

[b]Radiation Monitor[/b]
How much radiation is hitting this spot, and how long you can stay. Knows the difference
between sunlight you can hide from and planetary radiation that needs a sealed room.

[b]Weather Station[/b]
What the weather is doing, how long until it clears, and what it is doing to your solar
panels, wind turbines and breathable air. Warns you when a storm can actually hurt you —
some of them do 40 damage, and some quietly remove your oxygen.

[b]Habitat Monitor[/b]
Is this room still sealed? How long has it held? Small enough to mount inside the room it
watches.

[b]Bio Systems Scanner[/b]
What is alive nearby, what kind, how far away and which direction. Counts robots and
armed humanoids separately from wildlife.

[b]Rotating Antenna[/b]
A working antenna that is now also your ship's [b]tactical sensor[/b]. It hears every ship
whose antenna or beacon reaches you, and it picks up anything within 2 km whether it is
broadcasting or not. See [b]Tactical displays[/b] below.

Every block comes in [b]large and small grid[/b].

[h2]Screens[/h2]

Pick a Ground Truth app from any LCD's script list. No name tags, no setup — each panel
finds its instrument on its own.

There are four dedicated panels, one per instrument, and two that show everything at once:

[list]
[*][b]Overview[/b] - all four systems, rearranging itself to fit the screen it is on, from
a cockpit display to a full 2.5 m panel.
[*][b]Strip[/b] - built for [b]corner LCDs[/b], where the others are unreadable. Small
labels over large values, so a glance from across the room says what is measured and how
much to care. A pressure breach is the one thing that clears the screen and blinks;
everything else keeps its place and explains itself.
[/list]

Every screen writes its options into the LCD's [b]Custom Data[/b] the first time it runs -
each one with a note saying what it accepts. Fill in a value after the [b]=[/b] and the
screen follows it. Nothing to look up, no syntax to guess.

[h2]Tactical displays[/h2]

Put a [b]Rotating Antenna[/b] on your ship and three more apps come alive:

[list]
[*][b]Contact List[/b] - every ship the sensor knows about: name, class, range, bearing
and whether it is closing on you. Sort by range, bearing, closing speed or class.
[*][b]Tactical Radar[/b] - a plot of everything around you, ship's-nose-up or north-up on a
planet. The outer ring is as far as the sensor can see, so the edge of the screen is the
edge of what you know.
[*][b]Target Track[/b] - one ship in detail: range, bearing, elevation, speed, closing
rate, size and how long you have held it.
[/list]

[b]What the sensor knows, and what it does not.[/b] A ship broadcasting on an antenna or
beacon is identified by name and faction - as soon as its signal reaches you. A ship
running silent shows only as [b]UNIDENTIFIED[/b], and only inside 2 km. Your own ships are
always known. The sensor sees as far as the world's view distance.

[b]Tracking.[/b] By default the track follows the [b]nearest enemy[/b] and says so. Pick a
different ship from the antenna's terminal, or put [b]Track next contact[/b], [b]Track
previous contact[/b] and [b]Track automatic[/b] on a toolbar or button panel and cycle
through them without leaving your seat.

[h2]Alarms and automation, without scripting[/h2]

Ten new options appear in the vanilla [b]Event Controller[/b]. Pick one, choose a
sensor, and wire it to anything.

[list]
[*]Seal breached — shut the doors, sound the alarm
[*]Radiation building, or shelter lost
[*]Dangerous weather started
[*]A specific weather type began — sandstorms, thunderstorms, whatever your planet has
[*]Solar output dropped — start the reactor
[*]Outside oxygen dropped
[*]Wildlife detected
[*]Something that is not wildlife detected — robots and armed humanoids are counted apart
[/list]

They fire once when something changes, not over and over, and they do not go off the
moment you build them. Change the threshold and they fire right away if the new setting
is already met - which is how you test one. The Event Controller's info panel shows what
each sensor is reading and which output is active.

They work on dedicated servers and straight after loading a world, with nobody needing to
open a terminal first.

[h2]Some things you might not know the game does[/h2]

[list]
[*]Rain and thunderstorms [b]reduce[/b] your radiation exposure. Standing in a storm is
safer than standing in the open.
[*]Alien fog removes [b]all[/b] breathable oxygen, not some of it.
[*]Hailstorms, sandstorms and electrical storms can injure you directly.
[*]Wind turbines produce more in a storm than in clear weather.
[/list]

The instruments read all of this from the game itself and simply tell you.

[h2]Playing with other mods[/h2]

Ground Truth measures and reports. It has no opinion about what the readings mean, adds
no progression, and overrides nothing.

If you use a research or progression mod, these readings are available to it. If you
write Programmable Block scripts, everything on these panels can be read from code - from
the moment the world loads, on single player and dedicated servers alike, with no setup.

[b]Scripters and modders:[/b] full API reference and integration guide on GitHub —
https://github.com/UnsayableStorm/GroundTruth

[h2]Tell me what is wrong with it[/h2]

Feedback is genuinely wanted, and acted on. Most of what is good about this mod came from
using it and finding something awkward.

A recent example: the alarms for wildlife and for robots used to be sliders, where 20 on a
0-100 dial secretly meant ten animals. It was correct and nobody could have guessed it. The
sliders are gone and there is now simply [i]something is out there[/i]. That change came
from one round of testing and one complaint.

So if a reading looks wrong, a panel is unreadable on your screen size, an event does not
fire when you expect it to, or a block will not sit where it ought to - say so in the
comments. Bug reports and "this is annoying" are equally useful, and "I expected X and got
Y" is the most useful of all.

Issues and pull requests are welcome on GitHub too, if that is more your thing.

[h2]Credits[/h2]

Block models from [b]MTGraves'[/b] Naval Theme Prop Pack, used with his explicit
permission and much appreciated.

Species icons derived from [b]Twemoji[/b], © Twitter Inc and contributors, CC-BY 4.0,
modified for LCD use.

Subpart rotation adapted from [b]Digi's[/b] public spinning-subpart example.

Built with AI assistance — Claude, by Anthropic — as a working partner throughout. Design
decisions, in-game testing and every judgement call are mine. There is a longer account of
how that actually worked on the GitHub page, for anyone interested.

[hr][/hr]

[i]Readings are provided for informational purposes. Provision of a reading does not
constitute a recommendation. Survey and Assessment does not advise on habitability.[/i]
