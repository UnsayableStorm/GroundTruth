# Workshop description — draft

Steam BBCode, ready to paste. Kept in `tools/` so it does not ship with the mod.

Audience: **normal players**. Scripting and modding detail lives on the GitHub guide,
linked once near the bottom.

---

[h1]Ground Truth — Environmental Instruments[/h1]

[i]Threshold Dynamics, Survey and Assessment[/i]

Sensor blocks that tell you what is actually happening around you. Radiation, weather,
atmosphere, life.

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

[b]Rotating Radar Dish[/b]
A working antenna. Not an instrument — it is here because a sensor array looks wrong
without one.

Every block comes in [b]large and small grid[/b].

[h2]Screens[/h2]

Pick a Ground Truth app from any LCD's script list. No name tags, no setup — each panel
finds its instrument on its own.

There are four dedicated panels and an [b]Overview[/b] that shows everything at once and
rearranges itself to fit whatever screen it is on, including cockpit and corner displays.

[h2]Alarms and automation, without scripting[/h2]

Eleven new options appear in the vanilla [b]Event Controller[/b]. Pick one, choose a
sensor, and wire it to anything.

[list]
[*]Seal breached — shut the doors, sound the alarm
[*]Radiation building, or shelter lost
[*]Radiation time to critical below your limit
[*]Dangerous weather started
[*]A specific weather type began — sandstorms, thunderstorms, whatever your planet has
[*]Solar output dropped — start the reactor
[*]Outside oxygen dropped
[*]Wildlife nearby, or something that is not wildlife
[/list]

They fire once when something changes, not over and over, and they do not go off the
moment you build them.

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
write Programmable Block scripts, everything on these panels can be read from code.

[b]Scripters and modders:[/b] full API reference and integration guide on GitHub —
[LINK HERE]

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
