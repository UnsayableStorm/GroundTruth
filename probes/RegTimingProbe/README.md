# RegTimingProbe

Can `GT_` terminal properties be registered at world load, with no terminal ever
opened, without any block type losing its vanilla controls? The full reasoning and the
decompile findings are in the header of `Data/Scripts/RegTimingProbe/RegTimingProbe.cs`.
In short: the August corruption is explained by a `GetControls<IMyTerminalBlock>` call at
LoadData, not by registration timing, and registering after an upgrade module has
initialised should be safe on every machine.

Not part of Ground Truth. Deploy to `%APPDATA%\SpaceEngineers\Mods\RegTimingProbe` and
remove it from the world when done.

## Runs

| Run | World | Who | Status |
|---|---|---|---|
| A | `tactical testing world` (local, SP) + GroundTruthTacticalTest | Jason | **PASS 2026-10-05** (after the CustomName check fix) |
| B | Tactical Test Server on KORRIBAN, server log + client log | Jason | **PASS 2026-10-07** both sides: trigger at load (GT_HabitatMonitor), 28 -> 30, 51 types intact on server, 42 types on client, read 42, zero terminals |
| C | A or B with Animation Engine (2880317963) added: the mod that wiped the base list on Long Haul | Jason | after A/B |

## Procedure, run A

1. Load `tactical testing world`. **Open no terminal** and no toolbar config screen, and
   do not open the panels of any Ground Truth block.
2. Wait at least **5 minutes** in the world, so the 10 s, 60 s and 300 s checks all run.
3. Only then, optionally, open the terminal of a vanilla block (a console, a light, a
   cockpit) and confirm Name, Show in Terminal, Show in Toolbar Config and On/Off are
   there. Logged as `R.open`.
4. Type `/regprobe status` in chat to write a summary, then exit.

Extract:

    Select-String -Path "$env:APPDATA\SpaceEngineers\SpaceEngineers_*.log" -Pattern 'REGPROBE' |
      ForEach-Object { $_.Line }

## What decides it

PASS in a run: `R.prelist`, `R.register`, every `R.list.*`, `R.types.*`, `R.scope.*` and
`R.read.*` all PASS, and `R.noterminal` PASS. `R.types` is the important one: it
reads every terminal block type in the world through a real instance, which is exactly
the August symptom (blocks missing Name / On/Off).

Any `R.types` FAIL: stop, remove the probe, record it in ENGINE_TRAPS.

If A, B and C all pass, Ground Truth can register its properties from
`InstrumentPower.UpdateOnceBeforeFrame` on every machine. That would remove both the
terminal-open wait and the `GT_API_ENABLE` handshake on dedicated servers.

Expected noise while loaded: one "Possible entity type script logic collision" notice
per Ground Truth instrument subtype, because the probe's trigger sits beside
InstrumentPower.
