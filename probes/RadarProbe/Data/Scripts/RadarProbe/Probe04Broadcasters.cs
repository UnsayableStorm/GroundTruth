using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace RadarProbe
{
    // 0.4 - Broadcasters.
    //
    // QUESTION
    //   Can a client read whether a target is broadcasting, how far, and what its
    //   broadcast says? Under detection rule 1 this decides whether most contacts exist
    //   at all: a ship that is not broadcasting and is beyond 2 km is simply not there.
    //   So this is the probe the whole detection model rests on.
    //
    // WHAT IS READ
    //   Every antenna AND every beacon on every fixture grid: IsWorking, Enabled, and for
    //   antennas EnableBroadcasting, IsBroadcasting, ShowShipName; Radius and HudText for
    //   both; and the distance from the block to the dish. HudText and ShowShipName are
    //   what D2 names a broadcasting contact from, so they must read correctly on a
    //   client.
    //
    // THE TOGGLE (needs you)
    //   During the run, on a dedicated server, from your client: switch RP-B's antenna
    //   broadcast OFF in its terminal, wait 4 s, switch it back ON. Trap 18 records that
    //   push events do not reach mod handlers on a dedicated server, so this probe does
    //   not use one: Watch() polls the cached blocks every 10 ticks (6 times a second)
    //   and writes a FLIP line the moment a state changes. That resolution is fine enough
    //   to say whether the SERVER saw the flip within 4 s. The gate compares the FLIP
    //   times between the two logs.
    //
    // WHAT DECIDES IT
    //   0.4.state   PASS  B broadcasting and the dish is inside its radius; C and J
    //                     not broadcasting
    //   0.4.text    FAIL when B's HudText reads empty on this side; otherwise
    //               INCONCLUSIVE, because "matches what the vanilla HUD shows" needs a
    //               person looking at both (gate)
    //   0.4.toggle  PASS when a flip was observed on this side. Whether it was within
    //               4 s of the toggle is judged at the gate from both logs.
    //               INCONCLUSIVE when no flip happened during the run.
    //   0.4.beacon  PASS  I's beacon is working with a radius above zero
    //
    // WHAT EACH RESULT CHANGES
    //   state or toggle FAIL on CLIENT -> clients cannot see broadcast state reliably and
    //                                     cannot apply detection rule 1. Stop. The likely
    //                                     redesign is a server-side check sent to clients.
    //   text FAIL   -> broadcasting contacts would be named by grid name, revealing more
    //                  than the HUD does. A D2 decision, not a silent fallback.
    //   beacon FAIL -> beacons cannot count on clients; the beacon decision comes back.
    //
    // UNVERIFIED MEMBER: IMyRadioAntenna.IsBroadcasting. If it does not exist, delete the
    // isBroadcasting reads; EnableBroadcasting alone answers the question.
    public sealed class Probe04Broadcasters : Probe
    {
        public override string Id { get { return "0.4"; } }

        private sealed class Watched
        {
            public char Letter;
            public Sandbox.ModAPI.IMyRadioAntenna Antenna;
            public Sandbox.ModAPI.IMyBeacon Beacon;
            public string Last = "";

            public string Name { get { return Antenna != null ? Antenna.CustomName : Beacon.CustomName; } }
            public long Id { get { return Antenna != null ? Antenna.EntityId : Beacon.EntityId; } }
            public bool Closed { get { return Antenna != null ? Antenna.Closed : Beacon.Closed; } }

            public string State()
            {
                if (Antenna != null)
                    return "enable=" + Log.B(Antenna.EnableBroadcasting) + " isBroadcasting=" + Log.B(Antenna.IsBroadcasting)
                        + " working=" + Log.B(Antenna.IsWorking);
                return "enabled=" + Log.B(Beacon.Enabled) + " working=" + Log.B(Beacon.IsWorking);
            }
        }

        private readonly List<Watched> _watched = new List<Watched>();
        private bool _flipSeen;

        public override void Sample(Ctx c)
        {
            // The watch list is rebuilt every sample, so a flip that landed after the last
            // fast poll and before this rebuild would be swallowed by resetting Last.
            // Remember the old states and compare against them.
            var oldStates = new Dictionary<long, string>();
            for (int i = 0; i < _watched.Count; i++) oldStates[_watched[i].Id] = _watched[i].Last;
            _watched.Clear();

            var perGrid = new Dictionary<char, GridSummary>();
            var letters = new List<char>(c.Fix.Keys);
            letters.Sort();

            for (int k = 0; k < letters.Count; k++)
            {
                var f = c.Get(letters[k]);
                var sum = new GridSummary();
                perGrid[f.Letter] = sum;

                var blocks = Ctx.FatBlocks(f.Grid);
                for (int i = 0; i < blocks.Count; i++)
                {
                    var ant = blocks[i] as Sandbox.ModAPI.IMyRadioAntenna;
                    var bea = blocks[i] as Sandbox.ModAPI.IMyBeacon;
                    if (ant == null && bea == null) continue;

                    var w = new Watched { Letter = f.Letter, Antenna = ant, Beacon = bea };
                    w.Last = w.State();
                    _watched.Add(w);

                    string before;
                    if (oldStates.TryGetValue(w.Id, out before) && before != w.Last)
                        ReportFlip(c.Tick, w, before, w.Last);

                    IMyCubeBlock cb = ant != null ? (IMyCubeBlock)ant : (IMyCubeBlock)bea;
                    double dist = c.HasCenter ? Vector3DDistance(c, cb) : -1;

                    if (ant != null)
                    {
                        bool broadcasting = ant.IsWorking && ant.EnableBroadcasting;
                        Log.Line(Id, "antenna grid=RP-" + f.Letter + " id=" + ant.EntityId + " name=\"" + ant.CustomName + "\""
                            + " " + w.Last
                            + " radius=" + Log.F(ant.Radius, 0) + " hudText=\"" + ant.HudText + "\""
                            + " showShipName=" + Log.B(ant.ShowShipName) + " dist=" + Log.F(dist, 0));
                        sum.Count++;
                        if (broadcasting)
                        {
                            sum.Broadcasting = true;
                            if (dist >= 0 && dist <= ant.Radius) sum.InRange = true;
                        }
                        if (!string.IsNullOrEmpty(ant.HudText)) sum.HudText = ant.HudText;
                    }
                    else
                    {
                        bool broadcasting = bea.IsWorking;
                        Log.Line(Id, "beacon grid=RP-" + f.Letter + " id=" + bea.EntityId + " name=\"" + bea.CustomName + "\""
                            + " " + w.Last
                            + " radius=" + Log.F(bea.Radius, 0) + " hudText=\"" + bea.HudText + "\" dist=" + Log.F(dist, 0));
                        sum.Count++;
                        sum.Beacons++;
                        if (broadcasting)
                        {
                            sum.Broadcasting = true;
                            if (dist >= 0 && dist <= bea.Radius) sum.InRange = true;
                            if (bea.Radius > 0) sum.BeaconWithRadius = true;
                        }
                        if (!string.IsNullOrEmpty(bea.HudText)) sum.HudText = bea.HudText;
                    }
                }
            }

            JudgeState(perGrid);
            JudgeText(c, perGrid);
            JudgeBeacon(perGrid);
        }

        private sealed class GridSummary
        {
            public int Count;
            public int Beacons;
            public bool Broadcasting;
            public bool InRange;
            public bool BeaconWithRadius;
            public string HudText = "";
        }

        private static double Vector3DDistance(Ctx c, IMyCubeBlock b)
        {
            return VRageMath.Vector3D.Distance(c.Center, b.GetPosition());
        }

        private void JudgeState(Dictionary<char, GridSummary> perGrid)
        {
            GridSummary s;

            if (!perGrid.TryGetValue('B', out s)) Log.Inconclusive("0.4.state", "RP-B not found by name");
            else if (s.Count == 0) Log.Fail("0.4.state", "RP-B has no antenna or beacon on this side");
            else if (s.Broadcasting && s.InRange) Log.Pass("0.4.state", "RP-B is broadcasting and the dish is inside its radius");
            else Log.Fail("0.4.state", "RP-B broadcasting=" + Log.B(s.Broadcasting) + " dishInRange=" + Log.B(s.InRange));

            char[] silent = new char[] { 'C', 'J' };
            for (int i = 0; i < silent.Length; i++)
            {
                if (!perGrid.TryGetValue(silent[i], out s)) { Log.Inconclusive("0.4.state", "RP-" + silent[i] + " not found by name"); continue; }
                if (!s.Broadcasting) Log.Pass("0.4.state", "RP-" + silent[i] + " is not broadcasting");
                else Log.Fail("0.4.state", "RP-" + silent[i] + " is broadcasting but should be silent");
            }
        }

        private void JudgeText(Ctx c, Dictionary<char, GridSummary> perGrid)
        {
            GridSummary s;
            if (!perGrid.TryGetValue('B', out s)) { Log.Inconclusive("0.4.text", "RP-B not found by name"); return; }

            if (string.IsNullOrEmpty(s.HudText)) Log.Fail("0.4.text", "RP-B's HudText reads empty on this side");
            else if (c.Sample == 1)
                Log.Inconclusive("0.4.text", "HudText=\"" + s.HudText + "\" - confirm by eye that the vanilla HUD shows the same for RP-B");
        }

        private void JudgeBeacon(Dictionary<char, GridSummary> perGrid)
        {
            GridSummary s;
            if (!perGrid.TryGetValue('I', out s)) { Log.Inconclusive("0.4.beacon", "RP-I not found by name"); return; }
            if (s.Beacons == 0) { Log.Fail("0.4.beacon", "RP-I has no beacon on this side"); return; }
            if (s.BeaconWithRadius) Log.Pass("0.4.beacon", "RP-I's beacon is working with a radius");
            else Log.Fail("0.4.beacon", "RP-I's beacon is not working or has no radius on this side");
        }

        public override void Watch(long tick)
        {
            for (int i = 0; i < _watched.Count; i++)
            {
                var w = _watched[i];
                if (w.Closed) continue;

                string now = w.State();
                if (now == w.Last) continue;

                string was = w.Last;
                w.Last = now;
                ReportFlip(tick, w, was, now);
            }
        }

        private void ReportFlip(long tick, Watched w, string from, string to)
        {
            Log.Line(Id, "FLIP tick=" + tick + " grid=RP-" + w.Letter + " id=" + w.Id + " name=\"" + w.Name + "\""
                + " from=[" + from + "] to=[" + to + "]");

            // Only B's toggle answers the question; a flip on any other grid is logged
            // (it is evidence too) but does not settle 0.4.toggle.
            if (w.Letter == 'B')
            {
                _flipSeen = true;
                Log.Pass("0.4.toggle", "a flip on RP-B was observed on this side at tick " + tick
                    + " - the gate checks the SERVER's FLIP time is within 4 s of the CLIENT's");
            }
        }

        public override void Finish()
        {
            if (!_flipSeen)
                Log.Inconclusive("0.4.toggle", "no broadcast flip on RP-B was observed during the run - toggle it off, wait 4 s, on, while the run is active");
        }
    }
}
