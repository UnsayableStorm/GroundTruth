using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace RadarProbe
{
    // 0.2 - Grid groups.
    //
    // QUESTION
    //   Can mod code resolve a grid's mechanical group (rotor / hinge / piston children),
    //   and does that group exclude connector links? The plan collapses a ship with a
    //   rotor turret into ONE contact and keeps two docked ships as TWO, so both halves
    //   matter.
    //
    // TWO ROUTES, ASKED SEPARATELY
    //   Route 1: IMyCubeGrid.GetGridGroup(type).GetGrids(list)
    //   Route 2: MyAPIGateway.GridGroups.GetGroup(grid, type, list)
    //   Each is wrapped so that one THROWING at runtime does not end the question. Note
    //   that this only protects against runtime failure: a member that does not exist is
    //   a COMPILE error and stops the whole mod, and a prohibited one is rejected at load.
    //   Both are recorded in PHASE0_PROBES section 7 and the call deleted (section 1.1).
    //
    // WHAT DECIDES IT
    //   0.2.rotor      PASS  A resolves to exactly 2 grids under Mechanical, and the
    //                        other member resolves to the same 2
    //                  FAIL  1 grid, a different count, or both routes fail
    //   0.2.connector  PASS  G not in D's Mechanical group but in its Physical group
    //                  FAIL  G in D's Mechanical group
    //                  INCONCLUSIVE  G in neither (is it docked?), or D/G not found
    //   0.2.control    PASS  B resolves to exactly 1 grid
    //   0.2.routes     RECORDED: which routes worked and whether they agree
    //
    // ALSO DECIDED HERE: THE CONTACT ID
    //   A group has no root. Proposed rule: the member with the MOST BLOCKS, ties to the
    //   lowest EntityId. This probe logs the member block counts and the id that rule
    //   picks so the gate can confirm it picks the hull and not a turret.
    //
    // WHAT EACH RESULT CHANGES
    //   both routes rejected -> collapse groups by walking rotor / hinge / piston tops
    //                           by hand in Tactical.cs
    //   connector FAIL       -> docked ships merge into one contact; plan section 10 says so
    //
    // UNVERIFIED MEMBERS: IMyCubeGrid.GetGridGroup, IMyGridGroupData.GetGrids,
    // MyAPIGateway.GridGroups.GetGroup. Delete the route whose member does not exist.
    public sealed class Probe02Groups : Probe
    {
        public override string Id { get { return "0.2"; } }

        // Result of one route: the member list, or null with the reason it failed.
        private sealed class Result
        {
            public List<IMyCubeGrid> Members;
            public string Error = "";
            public bool Ok { get { return Members != null; } }
        }

        private readonly Dictionary<long, int> _blockCounts = new Dictionary<long, int>();

        public override void Sample(Ctx c)
        {
            _blockCounts.Clear();

            // Group membership does not change while the run is under way, so the detail is
            // written on the first sample and every tenth after it. The verdicts below run
            // every sample regardless.
            if (c.Sample == 1 || c.Sample % 10 == 0)
            {
                var letters = new List<char>(c.Fix.Keys);
                letters.Sort();

                for (int k = 0; k < letters.Count; k++)
                {
                    var f = c.Get(letters[k]);
                    LogGrid(f, GridLinkTypeEnum.Mechanical);
                    LogGrid(f, GridLinkTypeEnum.Physical);
                }
            }

            JudgeRotor(c);
            JudgeConnector(c);
            JudgeControl(c);
        }

        private Result Route1(IMyCubeGrid g, GridLinkTypeEnum type)
        {
            var r = new Result();
            try
            {
                var grp = g.GetGridGroup(type);
                if (grp == null) { r.Error = "GetGridGroup returned null"; return r; }
                var list = new List<IMyCubeGrid>();
                grp.GetGrids(list);
                r.Members = list;
            }
            catch (Exception e) { r.Error = e.GetType().Name + ": " + e.Message; }
            return r;
        }

        private Result Route2(IMyCubeGrid g, GridLinkTypeEnum type)
        {
            var r = new Result();
            try
            {
                var list = new List<IMyCubeGrid>();
                MyAPIGateway.GridGroups.GetGroup(g, type, list);
                r.Members = list;
            }
            catch (Exception e) { r.Error = e.GetType().Name + ": " + e.Message; }
            return r;
        }

        private int BlockCount(IMyCubeGrid g)
        {
            int n;
            if (_blockCounts.TryGetValue(g.EntityId, out n)) return n;
            var slims = new List<IMySlimBlock>();
            g.GetBlocks(slims, null);
            _blockCounts[g.EntityId] = slims.Count;
            return slims.Count;
        }

        // The proposed contact-id rule: most blocks, ties to the lowest EntityId.
        private IMyCubeGrid Pick(List<IMyCubeGrid> members)
        {
            IMyCubeGrid best = null;
            int bestBlocks = -1;
            for (int i = 0; i < members.Count; i++)
            {
                int b = BlockCount(members[i]);
                if (best == null || b > bestBlocks || (b == bestBlocks && members[i].EntityId < best.EntityId))
                {
                    best = members[i];
                    bestBlocks = b;
                }
            }
            return best;
        }

        private static string Describe(Result r, Func<IMyCubeGrid, int> blocks)
        {
            if (!r.Ok) return "FAILED(" + r.Error + ")";
            var sb = new StringBuilder();
            sb.Append("count=").Append(r.Members.Count).Append(" members=[");
            for (int i = 0; i < r.Members.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(r.Members[i].EntityId).Append(":\"").Append(r.Members[i].DisplayName)
                  .Append("\":").Append(blocks(r.Members[i])).Append("b");
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static bool SameSet(List<IMyCubeGrid> a, List<IMyCubeGrid> b)
        {
            if (a.Count != b.Count) return false;
            var ids = new HashSet<long>();
            for (int i = 0; i < a.Count; i++) ids.Add(a[i].EntityId);
            for (int i = 0; i < b.Count; i++) if (!ids.Contains(b[i].EntityId)) return false;
            return true;
        }

        private void LogGrid(FixtureGrid f, GridLinkTypeEnum type)
        {
            var r1 = Route1(f.Grid, type);
            var r2 = Route2(f.Grid, type);
            Func<IMyCubeGrid, int> blocks = BlockCount;

            string pick = "n/a";
            var src = r1.Ok ? r1 : (r2.Ok ? r2 : null);
            if (src != null)
            {
                var p = Pick(src.Members);
                if (p != null) pick = p.EntityId + (p.EntityId == f.Grid.EntityId ? " (the named grid)" : " (NOT the named grid)");
            }

            Log.Line(Id, "group grid=RP-" + f.Letter + " type=" + type
                + " route1=" + Describe(r1, blocks)
                + " route2=" + Describe(r2, blocks)
                + " agree=" + (r1.Ok && r2.Ok ? Log.B(SameSet(r1.Members, r2.Members)) : "n/a")
                + " idPick=" + pick);

            Log.Record(Id, "routes", "grid=RP-" + f.Letter + " type=" + type
                + " route1=" + (r1.Ok ? "ok" : "failed") + " route2=" + (r2.Ok ? "ok" : "failed"));
        }

        // First route that worked, so a single failed route does not block a verdict.
        private Result Resolve(IMyCubeGrid g, GridLinkTypeEnum type)
        {
            var r1 = Route1(g, type);
            return r1.Ok ? r1 : Route2(g, type);
        }

        private void JudgeRotor(Ctx c)
        {
            var a = c.Get('A');
            if (a == null) { Log.Inconclusive("0.2.rotor", "RP-A not found by name"); return; }

            var r = Resolve(a.Grid, GridLinkTypeEnum.Mechanical);
            if (!r.Ok) { Log.Fail("0.2.rotor", "both routes failed: " + r.Error); return; }
            if (r.Members.Count != 2)
            {
                Log.Fail("0.2.rotor", "A resolved to " + r.Members.Count + " grid(s), expected 2");
                return;
            }

            // From the OTHER member, the same two grids must come back.
            IMyCubeGrid other = r.Members[0].EntityId == a.Grid.EntityId ? r.Members[1] : r.Members[0];
            var back = Resolve(other, GridLinkTypeEnum.Mechanical);
            if (!back.Ok) { Log.Fail("0.2.rotor", "resolving from the subgrid failed: " + back.Error); return; }

            if (SameSet(r.Members, back.Members))
                Log.Pass("0.2.rotor", "A and its subgrid resolve to the same 2 grids from either member");
            else
                Log.Fail("0.2.rotor", "the subgrid resolved to a different set (" + back.Members.Count + " grid(s))");
        }

        private void JudgeConnector(Ctx c)
        {
            var d = c.Get('D');
            var g = c.Get('G');
            if (d == null || g == null)
            {
                Log.Inconclusive("0.2.connector", "RP-" + (d == null ? "D" : "G") + " not found by name");
                return;
            }

            var mech = Resolve(d.Grid, GridLinkTypeEnum.Mechanical);
            var phys = Resolve(d.Grid, GridLinkTypeEnum.Physical);
            if (!mech.Ok || !phys.Ok)
            {
                Log.Fail("0.2.connector", "could not resolve D's groups: " + (mech.Ok ? phys.Error : mech.Error));
                return;
            }

            bool inMech = Contains(mech.Members, g.Grid);
            bool inPhys = Contains(phys.Members, g.Grid);

            if (inMech) Log.Fail("0.2.connector", "G is in D's Mechanical group - docked ships would merge into one contact");
            else if (inPhys) Log.Pass("0.2.connector", "G is in D's Physical group and not in its Mechanical group");
            else Log.Inconclusive("0.2.connector", "G is in neither of D's groups - is G actually docked to D?");
        }

        private void JudgeControl(Ctx c)
        {
            var b = c.Get('B');
            if (b == null) { Log.Inconclusive("0.2.control", "RP-B not found by name"); return; }

            var r = Resolve(b.Grid, GridLinkTypeEnum.Mechanical);
            if (!r.Ok) { Log.Fail("0.2.control", "both routes failed: " + r.Error); return; }

            if (r.Members.Count == 1) Log.Pass("0.2.control", "B resolves to exactly 1 grid");
            else Log.Fail("0.2.control", "B resolved to " + r.Members.Count + " grids, expected 1");
        }

        private static bool Contains(List<IMyCubeGrid> list, IMyCubeGrid g)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].EntityId == g.EntityId) return true;
            return false;
        }
    }
}
