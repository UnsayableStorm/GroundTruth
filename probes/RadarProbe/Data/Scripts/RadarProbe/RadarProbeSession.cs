using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;

namespace RadarProbe
{
    // RadarProbe - Phase 0 read-only probes for the tactical expansion.
    // Design and pass/fail criteria: docs/PHASE0_PROBES.md (read that first).
    //
    // WHAT THIS MOD DOES
    //   Runs probes 0.1 (remote grids and velocity), 0.2 (grid groups), 0.3 (ownership),
    //   0.4 (broadcasters), 0.5 (class and size) and 0.7 (ship controller) against the
    //   fixture world described in section 2 of that document. It only READS. It writes
    //   nothing to any block, grid or terminal, so it is safe on any world - but run it
    //   on a copy anyway.
    //
    // HOW TO RUN
    //   1. Load the fixture world with this mod enabled. Section 1.2 lists the three
    //      passes: single player, dedicated server as a client, and (optionally) a
    //      second player.
    //   2. Wait. The first sample is at tick 600 (10 s), then one every 2 s for 300
    //      samples. The log says ARMED when the mod is up, and SUMMARY when the run is
    //      over: ten minutes, or sooner if you type /radarprobe done.
    //   3. Type /radarprobe in chat to take one extra sample on demand. On a dedicated
    //      server it only samples THIS machine; the server runs its own schedule
    //      regardless.
    //   4. Probe 0.4 needs one manual action during the run: toggle broadcast off on
    //      RP-B, wait 4 s, toggle it on. Probe 0.7 needs: sample on a planet surface,
    //      flying, and in space; toggle dampeners once; leave the seat once.
    //   5. Extract the log lines:
    //        Select-String -Path "$env:APPDATA\SpaceEngineers\SpaceEngineers_*.log" `
    //          -Pattern 'RADARPROBE' | ForEach-Object { $_.Line } | Set-Content out.txt
    //      Do the same for the dedicated server's log. Only those extracts go to review.
    //
    // WHAT DECIDES EACH RESULT
    //   The VERDICT lines in the log. They are conveniences: the raw lines they were
    //   computed from sit beside them, and the gate reads both. Checks marked
    //   "gate" below need a human or a second log and are emitted here as INCONCLUSIVE.
    //
    //     0.1.grids      sphere returned every fixture grid inside sync distance
    //     0.1.velocity   E's LinearVelocity within 10% of its measured speed
    //     0.2.rotor      A resolves to 2 grids under Mechanical, from either member
    //     0.2.connector  G not in D's Mechanical group, but in its Physical group
    //     0.2.control    B resolves to exactly 1 grid
    //     0.3.own        OWN grids relate to the dish owner as Owner / FactionShare
    //     0.3.other      OTHER grids relate as their tag says
    //     0.3.sides      CLIENT and SERVER agree                              (gate)
    //     0.4.state      B broadcasting and in range; C and J not
    //     0.4.text       B's HudText reads on the client and matches the HUD  (gate)
    //     0.4.toggle     broadcast flip observed on this side; timing         (gate)
    //     0.4.beacon     I's beacon is working with a radius
    //     0.5.class      static / grid size match the name tags
    //     0.5.size       LocalAABB within one block of the cell-bound size
    //     0.5.projection the projection has no physics, or is not in the sphere
    //     0.7.altitude   agrees with the HUD                                  (gate)
    //     0.7.gravity    agrees with the HUD                                  (gate)
    //     0.7.dampeners  flips when toggled
    //     0.7.unoccupied values still live with nobody seated
    //
    // A WHITELIST REJECTION IS A RESULT, NOT A BUG (PHASE0_PROBES 1.1)
    //   One prohibited member stops this whole mod from loading, and the game log names
    //   it. Record the member as a FAIL for the question it served, delete that call,
    //   reload. tools/check-compile.ps1 -Source catches members that do not exist; only
    //   the game catches members that exist and are prohibited.
    //
    // MEMBERS THAT WERE WRITTEN FROM MEMORY AND NOT VERIFIED AGAINST THE ASSEMBLIES
    //   (this file was written in a session with no game install, so nothing here has
    //   been compiled - run check-compile.ps1 -Source first)
    //     Probe01: IMyMotorStator.TopGrid
    //     Probe02: IMyCubeGrid.GetGridGroup, IMyGridGroupData.GetGrids,
    //              MyAPIGateway.GridGroups.GetGroup
    //     Probe03: IMyFactionCollection.GetRelationBetweenFactions
    //     Probe04: IMyRadioAntenna.IsBroadcasting
    //     Probe05: IMyProjector.ProjectedGrid
    public abstract class Probe
    {
        public abstract string Id { get; }

        public abstract void Sample(Ctx c);

        // Called every 10 ticks between the first sample and the end of the run, with no
        // entity search: it may only use references cached by Sample.
        public virtual void Watch(long tick) { }

        // Called once when the scheduled run ends.
        public virtual void Finish() { }
    }

    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class RadarProbeSession : MySessionComponentBase
    {
        private const int FirstSampleTick = 600;       // 10 s, past the loading screen
        private const int SampleIntervalTicks = 120;   // 2 s
        // Ten minutes. The design said one, and the first real run showed that a person
        // cannot fly to RP-O, toggle B's broadcast, work the dampeners and leave the seat
        // inside a minute. /radarprobe done ends the run early once the steps are finished.
        private const int ScheduledSamples = 300;
        private const int WatchIntervalTicks = 10;

        private long _tick;
        private bool _doneRequested;
        private int _sample;
        private int _scheduled;
        private bool _finished;
        private bool _manual;
        private bool _hooked;
        private bool _armed;

        private readonly List<Probe> _probes = new List<Probe>();

        public override void LoadData()
        {
            _probes.Add(new Probe01Detection());
            _probes.Add(new Probe02Groups());
            _probes.Add(new Probe03Ownership());
            _probes.Add(new Probe04Broadcasters());
            _probes.Add(new Probe05ClassSize());
            _probes.Add(new Probe07Controller());

            try
            {
                MyAPIGateway.Utilities.MessageEntered += OnMessage;
                _hooked = true;
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("RADARPROBE ? chat hook THREW " + e.GetType().Name + ": " + e.Message);
            }
        }

        protected override void UnloadData()
        {
            if (_hooked)
            {
                try { MyAPIGateway.Utilities.MessageEntered -= OnMessage; } catch { }
                _hooked = false;
            }
        }

        private void OnMessage(string text, ref bool sendToOthers)
        {
            if (text == null) return;

            // Exactly "/radarprobe". Anything with arguments - "/radarprobe write" - belongs
            // to RadarActionProbe, and running a sample for it would be wrong.
            string t = text.Trim();
            if (t.Equals("/radarprobe done", StringComparison.OrdinalIgnoreCase))
            {
                _doneRequested = true;
                sendToOthers = false;
                return;
            }
            if (!t.Equals("/radarprobe", StringComparison.OrdinalIgnoreCase)) return;

            _manual = true;
            sendToOthers = false;
        }

        public override void UpdateAfterSimulation()
        {
            _tick++;

            if (!_armed && _tick >= 1)
            {
                _armed = true;
                Log.DecideSide();
                var ids = new List<string>();
                for (int i = 0; i < _probes.Count; i++) ids.Add(_probes[i].Id);
                bool dedicated = false;
                try { dedicated = MyAPIGateway.Utilities.IsDedicated; } catch { }
                Log.Line("SESSION", "ARMED probes=" + string.Join(",", ids.ToArray())
                    + " first=" + FirstSampleTick + " interval=" + SampleIntervalTicks
                    + " count=" + ScheduledSamples + " dedicated=" + Log.B(dedicated)
                    + " chat=" + Log.B(_hooked));
            }

            if (_manual)
            {
                _manual = false;
                RunSample(true);
            }

            if (_finished) return;

            if (_doneRequested)
            {
                _doneRequested = false;
                if (_sample > 0)
                {
                    Log.Line("SESSION", "DONE requested after " + _scheduled + " scheduled samples");
                    Finish();
                    return;
                }
            }

            if (_tick >= FirstSampleTick && ((_tick - FirstSampleTick) % SampleIntervalTicks) == 0)
            {
                RunSample(false);
                if (_scheduled >= ScheduledSamples) Finish();
                return;
            }

            if (_sample > 0 && _tick % WatchIntervalTicks == 0)
            {
                for (int i = 0; i < _probes.Count; i++)
                {
                    try { _probes[i].Watch(_tick); }
                    catch (Exception e) { Log.Threw(_probes[i].Id, e); }
                }
            }
        }

        private void RunSample(bool manual)
        {
            _sample++;
            if (!manual) _scheduled++;
            Log.DecideSide();

            Ctx c;
            try { c = Ctx.Build(_sample, _tick); }
            catch (Exception e)
            {
                // Nothing below can run without a context, so no probe gets an ALIVE line
                // for this sample: it did not run, and the log must not claim it did.
                Log.Threw("CTX", e);
                return;
            }

            var fixtureLetters = new List<char>(c.Fix.Keys);
            fixtureLetters.Sort();
            Log.Line("CTX", "sample=" + _sample + (manual ? " (manual)" : "") + " tick=" + _tick
                + " fixture=" + new string(fixtureLetters.ToArray())
                + " observer=" + (c.Observer == null ? "none" : c.Observer.Name)
                + " dish=" + (c.Dish == null ? "none" : c.Dish.EntityId.ToString())
                + " center=" + c.CenterSource
                + " syncRadius=" + Log.F(c.SyncRadius, 0)
                + " duplicates=" + c.Duplicates.Count);
            for (int i = 0; i < c.Duplicates.Count; i++)
                Log.Line("CTX", "DUPLICATE fixture name ignored: " + c.Duplicates[i]);

            for (int i = 0; i < _probes.Count; i++)
            {
                Log.Alive(_probes[i].Id, _sample);
                try { _probes[i].Sample(c); }
                catch (Exception e) { Log.Threw(_probes[i].Id, e); }
            }
        }

        private void Finish()
        {
            _finished = true;
            for (int i = 0; i < _probes.Count; i++)
            {
                try { _probes[i].Finish(); }
                catch (Exception e) { Log.Threw(_probes[i].Id, e); }
            }
            Log.WriteSummary();
            Log.Line("SESSION", "DONE samples=" + _sample);
        }
    }
}
