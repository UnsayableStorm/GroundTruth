using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace RadarProbe
{
    // 0.3 - Ownership and relationship, as this machine sees them.
    //
    // QUESTION
    //   Can a client determine a target's owner and its relationship to the dish's
    //   owner? Detection rule 2 ("it is ours") and every relationship shown on a
    //   broadcast contact depend on the answer.
    //
    // TWO ROUTES
    //   Block route:   IMyCubeBlock.GetUserRelationToOwner(dishOwnerId) on one block of
    //                  the target. Handles unowned grids and players without a faction.
    //   Faction route: each side's faction via Session.Factions, then the relation
    //                  between the two factions. Logged for comparison only; if the
    //                  routes disagree the block route wins (PHASE0_PROBES 0.3).
    //   Relations are compared as strings (ToString of the enum) so that this file does
    //   not depend on which enum members this game version has.
    //
    // WHAT DECIDES IT
    //   0.3.own    PASS  every OWN-tagged grid reports Owner or FactionShare
    //   0.3.other  PASS  each OTHER grid's relation matches its ENEMY / NEUTRAL /
    //                    FRIEND / NOBODY tag; INCONCLUSIVE when it has no such tag
    //   0.3.sides  CLIENT and SERVER agree for every grid. Needs both logs, so it is
    //              emitted here as INCONCLUSIVE and settled at the gate by comparing the
    //              "rel" lines between the two logs.
    //
    // WHAT EACH RESULT CHANGES
    //   sides FAIL -> the client lacks ownership data. Relationship becomes a
    //                 server-sent field or UNKNOWN on clients: a real cost to D2, and it
    //                 goes back to the owner of the plan before Phase 1.
    //   block route passes, faction route disagrees -> use the block route only.
    //
    // UNVERIFIED MEMBER: IMyFactionCollection.GetRelationBetweenFactions (isolated in
    // FactionRoute below; its return type changed between game versions, so the result
    // is only ever printed).
    public sealed class Probe03Ownership : Probe
    {
        public override string Id { get { return "0.3"; } }

        public override void Sample(Ctx c)
        {
            if (c.Dish == null)
            {
                Log.Inconclusive("0.3.own", "no working dish found on RP-O, so there is no owner to relate to");
                Log.Inconclusive("0.3.other", "no dish owner");
                return;
            }

            long dishOwner = c.Dish.OwnerId;
            long localPlayer = -1;
            try
            {
                var p = MyAPIGateway.Session.Player;
                if (p != null) localPlayer = p.IdentityId;
            }
            catch { }

            if (c.Sample == 1)
            {
                Log.Line(Id, "dish id=" + c.Dish.EntityId + " ownerId=" + dishOwner + " localPlayerIdentity=" + localPlayer
                    + " dishFaction=" + FactionRoute.Describe(dishOwner));
                Log.Inconclusive("0.3.sides", "cross-log check: compare the 'rel' lines between the CLIENT and SERVER logs at the gate");
            }

            if (dishOwner == 0)
            {
                Log.Inconclusive("0.3.own", "the dish is unowned (ownerId=0)");
                Log.Inconclusive("0.3.other", "the dish is unowned (ownerId=0)");
                return;
            }

            var letters = new List<char>(c.Fix.Keys);
            letters.Sort();
            for (int k = 0; k < letters.Count; k++)
            {
                var f = c.Get(letters[k]);
                string rel = Relation(f, dishOwner);
                string owners = OwnerList(f.Grid);

                // The faction route, for comparison only.
                long firstOwner = 0;
                var big = f.Grid.BigOwners;
                if (big != null && big.Count > 0) firstOwner = big[0];

                if (c.Sample == 1 || c.Sample % 10 == 0)
                    Log.Line(Id, "rel grid=RP-" + f.Letter + " bigOwners=[" + owners + "] rel=" + rel
                        + " ownerFaction=" + FactionRoute.Describe(firstOwner)
                        + " factionRel=" + FactionRoute.Between(dishOwner, firstOwner));

                Judge(f, rel);
            }
        }

        // The relation from the dish's owner to one block of the target. A block that has
        // an owner is preferred, because an unowned block would say NoOwnership about a
        // grid that is in fact somebody's.
        private static string Relation(FixtureGrid f, long dishOwner)
        {
            IMyCubeBlock chosen = null;
            var blocks = Ctx.FatBlocks(f.Grid);
            for (int i = 0; i < blocks.Count; i++)
            {
                if (chosen == null) chosen = blocks[i];
                if (blocks[i].OwnerId != 0) { chosen = blocks[i]; break; }
            }
            if (chosen == null) return "NoBlocks";
            return chosen.GetUserRelationToOwner(dishOwner).ToString();
        }

        private static string OwnerList(IMyCubeGrid g)
        {
            var sb = new StringBuilder();
            var big = g.BigOwners;
            if (big != null)
                for (int i = 0; i < big.Count; i++)
                    sb.Append(i > 0 ? "," : "").Append(big[i]);
            return sb.ToString();
        }

        private void Judge(FixtureGrid f, string rel)
        {
            string name = "RP-" + f.Letter;

            if (f.Has("OWN"))
            {
                if (rel == "Owner" || rel == "FactionShare") Log.Pass("0.3.own", name + " reports " + rel);
                else Log.Fail("0.3.own", name + " is tagged OWN but reports " + rel);
                return;
            }

            if (!f.Has("OTHER")) return;

            // Tag -> the start of the enum name it should produce.
            string want = null;
            if (f.Has("ENEMY")) want = "Enem";
            else if (f.Has("NEUTRAL")) want = "Neutral";
            else if (f.Has("FRIEND")) want = "Friend";
            else if (f.Has("NOBODY")) want = "NoOwner";

            if (want == null)
            {
                Log.Inconclusive("0.3.other", name + " has no ENEMY / NEUTRAL / FRIEND / NOBODY tag; it reports " + rel);
                return;
            }

            if (rel.StartsWith(want)) Log.Pass("0.3.other", name + " reports " + rel);
            else Log.Fail("0.3.other", name + " is tagged for '" + want + "...' but reports " + rel);
        }
    }

    // The faction route, isolated so a missing or changed member is one small deletion.
    // Everything it returns is a string for logging; nothing is decided from it.
    internal static class FactionRoute
    {
        public static string Describe(long playerId)
        {
            if (playerId == 0) return "none";
            try
            {
                var f = MyAPIGateway.Session.Factions.TryGetPlayerFaction(playerId);
                if (f == null) return "no-faction";
                return f.Tag + "#" + f.FactionId;
            }
            catch (Exception e) { return "ERR(" + e.GetType().Name + ")"; }
        }

        public static string Between(long playerA, long playerB)
        {
            if (playerA == 0 || playerB == 0) return "n/a";
            try
            {
                var fa = MyAPIGateway.Session.Factions.TryGetPlayerFaction(playerA);
                var fb = MyAPIGateway.Session.Factions.TryGetPlayerFaction(playerB);
                if (fa == null || fb == null) return "n/a(no-faction)";
                var r = MyAPIGateway.Session.Factions.GetRelationBetweenFactions(fa.FactionId, fb.FactionId);
                return r.ToString();
            }
            catch (Exception e) { return "ERR(" + e.GetType().Name + ")"; }
        }
    }
}
