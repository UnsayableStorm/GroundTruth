using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace RadarProbe
{
    // 0.5 - Class and size.
    //
    // QUESTION
    //   Are static / dynamic, grid size and real dimensions readable, and can a
    //   projection be told apart from a ship? A contact's class is "Station", "Large
    //   ship" or "Small ship"; its dimensions are shown on Target Track; and a projected
    //   blueprint must never appear as a contact.
    //
    // WHAT IS READ, per fixture grid
    //   IsStatic, GridSizeEnum, LocalAABB extents, WorldAABB extents, the cell bounds
    //   (Min / Max) and the size they imply, (Max - Min + 1) x GridSize, and whether
    //   Physics is null. Expected values come from the name tags: STATIC, SMALL, LARGE.
    //
    // THE PROJECTION (RP-F)
    //   F is a projector on O or A with a blueprint projected, not a grid of its own. The
    //   projected grid is reached through the projector's ProjectedGrid and checked for
    //   Physics == null and for whether the sphere query returned it.
    //
    // WHAT DECIDES IT
    //   0.5.class       PASS  IsStatic matches the STATIC tag (present <-> true), and
    //                         GridSizeEnum matches SMALL / LARGE where the name has one
    //   0.5.size        PASS  LocalAABB extents within one block of the cell-bound size on
    //                         every axis
    //   0.5.projection  PASS  the projected grid has Physics == null, or was not returned
    //                         by the sphere; FAIL when it has physics AND was returned;
    //                         INCONCLUSIVE when nothing is being projected on O or A
    //   0.5.aabb        RECORDED: how much larger WorldAABB is than LocalAABB
    //
    // WHAT EACH RESULT CHANGES
    //   projection FAIL -> another discriminator is needed before Phase 1, or projections
    //                      would appear as contacts
    //   aabb            -> decides the Dimensions source. The expectation is LocalAABB:
    //                      WorldAABB grows as a ship rotates, and a contact must not seem
    //                      to change size because it turned.
    //
    // UNVERIFIED MEMBER: IMyProjector.ProjectedGrid.
    public sealed class Probe05ClassSize : Probe
    {
        public override string Id { get { return "0.5"; } }

        public override void Sample(Ctx c)
        {
            var letters = new List<char>(c.Fix.Keys);
            letters.Sort();

            for (int k = 0; k < letters.Count; k++)
            {
                var f = c.Get(letters[k]);
                var g = f.Grid;

                Vector3 local = g.LocalAABB.Max - g.LocalAABB.Min;
                Vector3D world = g.WorldAABB.Max - g.WorldAABB.Min;

                int cx = g.Max.X - g.Min.X + 1;
                int cy = g.Max.Y - g.Min.Y + 1;
                int cz = g.Max.Z - g.Min.Z + 1;
                float gs = g.GridSize;
                double sx = cx * gs, sy = cy * gs, sz = cz * gs;

                string size = g.GridSizeEnum.ToString();
                bool physNull = g.Physics == null;

                if (c.Sample == 1 || c.Sample % 10 == 0)
                {
                    Log.Line(Id, "grid grid=RP-" + f.Letter + " isStatic=" + Log.B(g.IsStatic) + " size=" + size
                        + " physicsNull=" + Log.B(physNull)
                        + " cells=" + cx + "x" + cy + "x" + cz + " gridSize=" + Log.F(gs, 2)
                        + " cellBounds=" + Log.F(sx, 1) + "x" + Log.F(sy, 1) + "x" + Log.F(sz, 1)
                        + " local=" + Log.F(local.X, 1) + "x" + Log.F(local.Y, 1) + "x" + Log.F(local.Z, 1)
                        + " world=" + Log.F(world.X, 1) + "x" + Log.F(world.Y, 1) + "x" + Log.F(world.Z, 1));

                    double lv = local.X * local.Y * local.Z;
                    double wv = world.X * world.Y * world.Z;
                    Log.Record(Id, "aabb", "grid=RP-" + f.Letter + " worldOverLocalVolume=" + (lv > 0 ? Log.F(wv / lv, 2) : "n/a"));
                }

                JudgeClass(f, g, size);
                JudgeSize(f, local, sx, sy, sz, gs);
            }

            JudgeProjection(c);
        }

        private void JudgeClass(FixtureGrid f, IMyCubeGrid g, string size)
        {
            string name = "RP-" + f.Letter;
            var problems = new StringBuilder();

            if (f.Has("STATIC") != g.IsStatic)
                problems.Append("IsStatic=").Append(Log.B(g.IsStatic)).Append(" but the name ")
                        .Append(f.Has("STATIC") ? "says STATIC; " : "has no STATIC tag; ");

            if (f.Has("SMALL") && size != "Small") problems.Append("tagged SMALL but GridSizeEnum=").Append(size).Append("; ");
            if (f.Has("LARGE") && size != "Large") problems.Append("tagged LARGE but GridSizeEnum=").Append(size).Append("; ");

            if (problems.Length == 0) Log.Pass("0.5.class", name + " isStatic=" + Log.B(g.IsStatic) + " size=" + size);
            else Log.Fail("0.5.class", name + " " + problems);
        }

        private void JudgeSize(FixtureGrid f, Vector3 local, double sx, double sy, double sz, float gs)
        {
            double dx = Math.Abs(local.X - sx);
            double dy = Math.Abs(local.Y - sy);
            double dz = Math.Abs(local.Z - sz);

            string detail = "RP-" + f.Letter + " diff x/y/z=" + Log.F(dx, 2) + "/" + Log.F(dy, 2) + "/" + Log.F(dz, 2)
                + " (one block = " + Log.F(gs, 2) + ")";
            if (dx <= gs && dy <= gs && dz <= gs) Log.Pass("0.5.size", detail);
            else Log.Fail("0.5.size", detail);
        }

        private void JudgeProjection(Ctx c)
        {
            int found = 0;
            string letters = "OA";
            for (int k = 0; k < letters.Length; k++)
            {
                var f = c.Get(letters[k]);
                if (f == null) continue;

                var blocks = Ctx.FatBlocks(f.Grid);
                for (int i = 0; i < blocks.Count; i++)
                {
                    var proj = blocks[i] as IMyProjector;
                    if (proj == null) continue;

                    var pg = proj.ProjectedGrid;
                    if (pg == null) continue;
                    found++;

                    bool physNull = pg.Physics == null;
                    bool returned = c.InSphere(pg);
                    string detail = "projector on RP-" + f.Letter + " projected id=" + pg.EntityId
                        + " physicsNull=" + Log.B(physNull) + " returnedBySphere=" + Log.B(returned)
                        + " isStatic=" + Log.B(pg.IsStatic);

                    if (physNull) Log.Pass("0.5.projection", detail + " (Physics is null)");
                    else if (!returned) Log.Pass("0.5.projection", detail + " (not returned by the sphere)");
                    else Log.Fail("0.5.projection", detail + " (has physics AND was returned - it would appear as a contact)");
                }
            }

            if (found == 0)
                Log.Inconclusive("0.5.projection", "no projector with an active projection found on RP-O or RP-A (RP-F)");
        }
    }
}
