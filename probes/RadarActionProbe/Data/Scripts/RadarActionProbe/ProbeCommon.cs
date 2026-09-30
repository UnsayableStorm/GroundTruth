using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

namespace RadarActionProbe
{
    // Logging and fixture lookup for RadarActionProbe. This is a deliberate COPY of the
    // subset of RadarProbe's helpers that this mod needs: the two probe mods are
    // independent (one is read-only, this one touches terminals), so neither can depend on
    // the other being loaded. The log format is the same one, documented in
    // docs/PHASE0_PROBES.md section 3, so one search finds both mods' lines.
    public static class Log
    {
        public const string Tag = "RADARPROBE";
        public static string Side = "?";

        private static readonly Dictionary<string, int[]> _tally = new Dictionary<string, int[]>();

        public static void DecideSide()
        {
            try
            {
                var mp = MyAPIGateway.Multiplayer;
                if (mp == null || !mp.MultiplayerActive) Side = "SP";
                else Side = mp.IsServer ? "SERVER" : "CLIENT";
            }
            catch (Exception e)
            {
                Side = "?";
                MyLog.Default.WriteLineAndConsole(Tag + " ? side detection THREW " + e.GetType().Name + ": " + e.Message);
            }
        }

        public static void Line(string probe, string text)
        {
            MyLog.Default.WriteLineAndConsole(Tag + " " + Side + " " + probe + " " + text);
        }

        public static void Alive(string probe, int sample)
        {
            MyLog.Default.WriteLineAndConsole(Tag + " " + Side + " ALIVE " + probe + " sample=" + sample);
        }

        public static void Threw(string probe, Exception e)
        {
            MyLog.Default.WriteLineAndConsole(Tag + " " + Side + " " + probe + " THREW " + e.GetType().Name + ": " + e.Message);
        }

        public static void Record(string probe, string check, string text)
        {
            MyLog.Default.WriteLineAndConsole(Tag + " " + Side + " " + probe + " RECORD " + check + " " + text);
        }

        public static void Verdict(string check, string result, string reason)
        {
            MyLog.Default.WriteLineAndConsole(Tag + " " + Side + " VERDICT " + check + " " + result + " " + reason);

            int[] t;
            if (!_tally.TryGetValue(check, out t)) { t = new int[3]; _tally[check] = t; }
            if (result == "PASS") t[0]++;
            else if (result == "FAIL") t[1]++;
            else t[2]++;
        }

        public static void Pass(string check, string reason) { Verdict(check, "PASS", reason); }
        public static void Fail(string check, string reason) { Verdict(check, "FAIL", reason); }
        public static void Inconclusive(string check, string reason) { Verdict(check, "INCONCLUSIVE", reason); }

        public static void WriteSummary()
        {
            var keys = new List<string>(_tally.Keys);
            keys.Sort();
            for (int i = 0; i < keys.Count; i++)
            {
                var t = _tally[keys[i]];
                MyLog.Default.WriteLineAndConsole(Tag + " " + Side + " SUMMARY " + keys[i]
                    + " pass=" + t[0] + " fail=" + t[1] + " inconclusive=" + t[2]);
            }
        }

        public static string B(bool v) { return v ? "True" : "False"; }
        public static string F1(double v) { return v.ToString("F1"); }
    }

    public static class Fixture
    {
        public static bool IsDish(IMyCubeBlock b)
        {
            if (b == null) return false;
            string s = b.BlockDefinition.SubtypeName;
            return s == "GT_RotatingRadarDish" || s == "GT_RotatingRadarDish_S";
        }

        // The grid named RP-O, or null. Matches "RP-O" alone or "RP-O " followed by tags.
        public static IMyCubeGrid FindObserver()
        {
            var all = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(all, x => x is IMyCubeGrid);
            foreach (var x in all)
            {
                var g = x as IMyCubeGrid;
                if (g == null) continue;
                string n = g.DisplayName;
                if (string.IsNullOrEmpty(n) || !n.StartsWith("RP-O")) continue;
                if (n.Length == 4 || n[4] == ' ') return g;
            }
            return null;
        }

        // Every working-or-not dish on the observer grid, as terminal blocks.
        public static List<IMyTerminalBlock> FindDishes()
        {
            var result = new List<IMyTerminalBlock>();
            var grid = FindObserver();
            if (grid == null) return result;

            var slims = new List<IMySlimBlock>();
            grid.GetBlocks(slims, null);
            for (int i = 0; i < slims.Count; i++)
            {
                var tb = slims[i].FatBlock as IMyTerminalBlock;
                if (tb != null && IsDish(tb)) result.Add(tb);
            }
            return result;
        }
    }
}
