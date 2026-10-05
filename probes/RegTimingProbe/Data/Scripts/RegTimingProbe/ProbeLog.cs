using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Utils;

namespace RegTimingProbe
{
    // Log lines in the same shape as RadarProbe's (docs/PHASE0_PROBES.md section 3), with
    // their own tag so one search pulls out this probe and nothing else:
    //
    //   REGPROBE <side> <check> RECORD <what> ...
    //   REGPROBE <side> VERDICT <check> PASS|FAIL|INCONCLUSIVE <reason>
    //   REGPROBE <side> SUMMARY <check> pass=n fail=n inconclusive=n
    public static class Log
    {
        public const string Tag = "REGPROBE";
        public static string Side = "?";

        private static readonly Dictionary<string, int[]> _tally = new Dictionary<string, int[]>();

        public static void DecideSide()
        {
            try
            {
                var mp = MyAPIGateway.Multiplayer;
                if (mp == null || !mp.MultiplayerActive) Side = "SP";
                else Side = mp.IsServer ? (MyAPIGateway.Utilities.IsDedicated ? "SERVER" : "HOST") : "CLIENT";
            }
            catch (Exception e)
            {
                Side = "?";
                MyLog.Default.WriteLineAndConsole(Tag + " ? side detection THREW " + e.GetType().Name + ": " + e.Message);
            }
        }

        public static void Line(string text)
        {
            MyLog.Default.WriteLineAndConsole(Tag + " " + Side + " " + text);
        }

        public static void Record(string check, string text)
        {
            Line(check + " RECORD " + text);
        }

        public static void Threw(string where, Exception e)
        {
            Line(where + " THREW " + e.GetType().Name + ": " + e.Message);
        }

        public static void Verdict(string check, string result, string reason)
        {
            Line("VERDICT " + check + " " + result + " " + reason);

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
                Line("SUMMARY " + keys[i] + " pass=" + t[0] + " fail=" + t[1] + " inconclusive=" + t[2]);
            }
            if (keys.Count == 0) Line("SUMMARY no verdicts yet");
        }

        public static string B(bool v) { return v ? "True" : "False"; }
    }
}
