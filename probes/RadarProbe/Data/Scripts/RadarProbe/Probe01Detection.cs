using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace RadarProbe
{
    // 0.1 - Remote grids and their velocity, as this machine sees them.
    //
    // QUESTION
    //   Does GetEntitiesInSphere return remote IMyCubeGrid entities on a dedicated-server
    //   client, and is their Physics.LinearVelocity real there - not zero, not stale?
    //
    // THE INDEPENDENT CHECK
    //   LinearVelocity is what the engine CLAIMS. The distance a grid actually moved
    //   between two samples, divided by the simulation time between them, is what
    //   HAPPENED. A velocity that compiles, does not throw, and reads zero on a ship that
    //   is visibly moving is exactly the failure a "no exception" test cannot see
    //   (ENGINE_TRAPS 9). Time is counted in simulation ticks, not wall clock, so a
    //   server running slow does not fake a disagreement.
    //
    // WHAT DECIDES IT
    //   0.1.grids     PASS  every fixture grid inside sync distance is returned
    //                 FAIL  a fixture grid exists on this machine but is not returned
    //                 INCONCLUSIVE  the fixture grid was not found by name, or is
    //                               outside the sync radius
    //   0.1.velocity  PASS  E's LinearVelocity within 10% of its measured speed
    //                 FAIL  reported near zero, or off by more than 10%, while E is
    //                       measurably moving (> 5 m/s); or Physics is null
    //                 INCONCLUSIVE  E not found, not returned, or not moving
    //   0.1.subgrids, 0.1.streaming: RECORDED, not judged.
    //
    // WHAT EACH RESULT CHANGES (plan section 8, Phase 1)
    //   velocity FAIL on CLIENT only -> velocity comes from position deltas between scans
    //   grids FAIL on CLIENT         -> the client-side model is wrong; redesign around a
    //                                   server scan before building anything else
    //   streaming: H returned on the server and not on the client confirms the stated
    //   range limit; H returned on the client means the limit is looser than stated.
    //
    // UNVERIFIED MEMBER: IMyMotorStator.TopGrid (used only for the subgrids record).
    public sealed class Probe01Detection : Probe
    {
        public override string Id { get { return "0.1"; } }

        private sealed class Prev
        {
            public Vector3D Pos;
            public double Seconds;
        }

        private readonly Dictionary<long, Prev> _prev = new Dictionary<long, Prev>();

        // Fixture grids that must be returned when they are inside the sync radius.
        // F is a projector rather than a grid and H is deliberately beyond streaming.
        private const string ExpectedLetters = "OABCDEGIJ";

        public override void Sample(Ctx c)
        {
            Log.Line(Id, "query radius=" + Log.F(c.SyncRadius, 0) + " center=" + c.CenterSource
                + " ms=" + Log.F(c.SphereMs, 2)
                + (c.SphereError.Length > 0 ? " error=" + c.SphereError : ""));

            if (c.Sphere == null)
            {
                Log.Inconclusive("0.1.grids", "no sphere result: "
                    + (c.HasCenter ? "query threw " + c.SphereError : "no centre (no RP-O grid and no player)"));
                return;
            }

            int grids = 0, chars = 0, voxels = 0, floating = 0, other = 0;
            var otherTypes = new Dictionary<string, int>();
            var gridList = new List<IMyCubeGrid>();

            for (int i = 0; i < c.Sphere.Count; i++)
            {
                var e = c.Sphere[i];
                var g = e as IMyCubeGrid;
                if (g != null) { grids++; gridList.Add(g); continue; }
                if (e is IMyCharacter) { chars++; continue; }
                if (e is IMyVoxelBase) { voxels++; continue; }

                string tn = e.GetType().Name;
                if (tn.Contains("FloatingObject")) { floating++; continue; }
                other++;
                int n;
                otherTypes[tn] = otherTypes.TryGetValue(tn, out n) ? n + 1 : 1;
            }

            var ot = new StringBuilder();
            foreach (var kv in otherTypes) ot.Append(ot.Length > 0 ? "," : "").Append(kv.Key).Append(":").Append(kv.Value);
            Log.Line(Id, "entities total=" + c.Sphere.Count + " grids=" + grids + " chars=" + chars
                + " voxels=" + voxels + " floating=" + floating + " other=" + other
                + (ot.Length > 0 ? " otherTypes=" + ot : ""));

            // Measured speed per grid, from the previous sample.
            var measured = new Dictionary<long, double>();
            var reported = new Dictionary<long, double>();
            var nullPhysics = new HashSet<long>();

            for (int i = 0; i < gridList.Count; i++)
            {
                var g = gridList[i];
                long id = g.EntityId;
                Vector3D pos = g.GetPosition();

                bool physNull = g.Physics == null;
                double speed = -1;
                if (physNull) nullPhysics.Add(id);
                else
                {
                    speed = g.Physics.LinearVelocity.Length();
                    reported[id] = speed;
                }

                string meas = "n/a";
                Prev p;
                if (_prev.TryGetValue(id, out p))
                {
                    double dt = c.Seconds - p.Seconds;
                    // A manual /radarprobe sample can land a fraction of a second after
                    // a scheduled one; a speed from that little time is noise.
                    if (dt >= 1.0)
                    {
                        double m = Vector3D.Distance(pos, p.Pos) / dt;
                        measured[id] = m;
                        meas = Log.F(m, 2);
                    }
                }

                Log.Line(Id, "grid id=" + id + " name=\"" + g.DisplayName + "\" physicsNull=" + Log.B(physNull)
                    + " reported=" + (physNull ? "n/a" : Log.F(speed, 2))
                    + " measured=" + meas
                    + " dist=" + Log.F(c.DistanceTo(g), 1));
            }

            JudgeGrids(c);
            JudgeVelocity(c, measured, reported, nullPhysics);
            RecordSubgrids(c);
            RecordStreaming(c);

            // Update AFTER judging: the verdicts above need the previous sample.
            for (int i = 0; i < gridList.Count; i++)
            {
                Prev p;
                if (!_prev.TryGetValue(gridList[i].EntityId, out p)) { p = new Prev(); _prev[gridList[i].EntityId] = p; }
                p.Pos = gridList[i].GetPosition();
                p.Seconds = c.Seconds;
            }
        }

        private void JudgeGrids(Ctx c)
        {
            for (int i = 0; i < ExpectedLetters.Length; i++)
            {
                char ch = ExpectedLetters[i];
                var f = c.Get(ch);
                if (f == null)
                {
                    Log.Inconclusive("0.1.grids", "RP-" + ch + " not found by name on this machine");
                    continue;
                }

                double d = c.DistanceTo(f.Grid);
                if (d > c.SyncRadius)
                    Log.Inconclusive("0.1.grids", "RP-" + ch + " is outside the sync radius (d=" + Log.F(d, 0) + ")");
                else if (c.InSphere(f.Grid))
                    Log.Pass("0.1.grids", "RP-" + ch + " returned (d=" + Log.F(d, 0) + ")");
                else
                    Log.Fail("0.1.grids", "RP-" + ch + " exists on this machine at d=" + Log.F(d, 0)
                        + " but the sphere did not return it");
            }
        }

        private void JudgeVelocity(Ctx c, Dictionary<long, double> measured, Dictionary<long, double> reported, HashSet<long> nullPhysics)
        {
            var e = c.Get('E');
            if (e == null) { Log.Inconclusive("0.1.velocity", "RP-E not found by name"); return; }

            long id = e.Grid.EntityId;
            if (!c.InSphere(e.Grid)) { Log.Inconclusive("0.1.velocity", "RP-E not returned by the sphere (see 0.1.grids)"); return; }
            if (nullPhysics.Contains(id)) { Log.Fail("0.1.velocity", "RP-E Physics is null on this machine"); return; }

            double m;
            if (!measured.TryGetValue(id, out m)) { Log.Inconclusive("0.1.velocity", "no previous sample at least 1 s old yet"); return; }
            if (m < 5.0) { Log.Inconclusive("0.1.velocity", "RP-E is not moving (measured " + Log.F(m, 2) + " m/s)"); return; }

            double r = reported[id];
            if (r < 0.5)
                Log.Fail("0.1.velocity", "reported " + Log.F(r, 2) + " m/s but measured " + Log.F(m, 2) + " m/s");
            else if (Math.Abs(r - m) > 0.10 * m)
                Log.Fail("0.1.velocity", "reported " + Log.F(r, 2) + " vs measured " + Log.F(m, 2) + " m/s (>10% apart)");
            else
                Log.Pass("0.1.velocity", "reported " + Log.F(r, 2) + " vs measured " + Log.F(m, 2) + " m/s");
        }

        // Do rotor-mounted subgrids come back as separate entities? Found through the
        // stators themselves, so this does not depend on the grid-group API that 0.2 tests.
        private void RecordSubgrids(Ctx c)
        {
            string letters = "OA";
            for (int k = 0; k < letters.Length; k++)
            {
                var f = c.Get(letters[k]);
                if (f == null) continue;

                var blocks = Ctx.FatBlocks(f.Grid);
                int stators = 0;
                for (int i = 0; i < blocks.Count; i++)
                {
                    var st = blocks[i] as IMyMotorStator;
                    if (st == null) continue;
                    stators++;
                    var top = st.TopGrid;
                    Log.Record(Id, "subgrids", "parent=RP-" + f.Letter + " stator=\"" + st.CustomName + "\" top="
                        + (top == null ? "none" : top.EntityId.ToString())
                        + " topInSphere=" + Log.B(top != null && c.InSphere(top)));
                }
                if (stators == 0) Log.Record(Id, "subgrids", "parent=RP-" + f.Letter + " has no rotors or hinges");
            }
        }

        private void RecordStreaming(Ctx c)
        {
            var h = c.Get('H');
            if (h == null)
            {
                Log.Record(Id, "streaming", "H not found by name on this machine (expected on a client beyond sync range)");
                return;
            }
            Log.Record(Id, "streaming", "H found dist=" + Log.F(c.DistanceTo(h.Grid), 0)
                + " inSphere=" + Log.B(c.InSphere(h.Grid)) + " syncRadius=" + Log.F(c.SyncRadius, 0));
        }
    }
}
