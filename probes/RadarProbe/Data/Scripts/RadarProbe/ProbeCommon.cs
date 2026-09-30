using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace RadarProbe
{
    // Shared plumbing for the Phase 0 probes. Design: docs/PHASE0_PROBES.md.
    //
    // LOG FORMAT (section 3 of that document). Every line starts with RADARPROBE so one
    // search extracts everything:
    //
    //   RADARPROBE <side> <probe> <key=value ...>
    //   RADARPROBE <side> VERDICT <probe>.<check> PASS|FAIL|INCONCLUSIVE <reason>
    //   RADARPROBE <side> ALIVE <probe> sample=<n>
    //   RADARPROBE <side> <probe> THREW <type>: <message>
    //   RADARPROBE <side> <probe> RECORD <check> ...     recorded, not judged
    //   RADARPROBE <side> SUMMARY <probe>.<check> pass=n fail=n inconclusive=n
    //
    // SUMMARY is the one addition to the documented format: a tally written when the
    // scheduled run ends, so the gate can see at a glance whether a check ever failed
    // without counting thirty verdict lines. It summarises the VERDICT lines above it
    // and replaces none of them.
    //
    // <side> is SP, SERVER or CLIENT and is decided once at load.
    public static class Log
    {
        public const string Tag = "RADARPROBE";

        // Whether the session is single player, the server, or a client of one.
        // Multiplayer.IsServer is true on a dedicated server AND on a host, so a host
        // reads SERVER. The distinction that matters for this plan is "does this
        // machine own the simulation", which is what IsServer answers.
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

        // Written every sample BEFORE anything that could throw. A probe with no ALIVE
        // lines proved nothing (ENGINE_TRAPS 12).
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

        // check is "<probe>.<name>", e.g. "0.1.grids".
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

        public static string F(double v, int digits) { return v.ToString("F" + digits); }
        public static string B(bool v) { return v ? "True" : "False"; }
    }

    // Wall-clock cost of the sphere query. Isolated so that if the whitelist rejects
    // Stopwatch, this one class is deleted, Ms() is made to return -1, and nothing else
    // changes (PHASE0_PROBES 1.1: the timing informs plan section 6 and decides nothing).
    public static class Timing
    {
        public static System.Diagnostics.Stopwatch Start()
        {
            return System.Diagnostics.Stopwatch.StartNew();
        }

        public static double Ms(System.Diagnostics.Stopwatch sw)
        {
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
    }

    // One fixture grid, parsed from its name. The name is the fixture's own declaration
    // of what it is supposed to be, so the probe can check the engine against it and a
    // mis-built fixture shows up as a disagreement instead of skewing a result.
    public sealed class FixtureGrid
    {
        public char Letter;
        public IMyCubeGrid Grid;
        public string Name;
        public HashSet<string> Tags = new HashSet<string>();

        public bool Has(string tag) { return Tags.Contains(tag); }
    }

    public static class Fixture
    {
        // "RP-A OWN LARGE ROTOR SILENT" -> letter A, tags OWN LARGE ROTOR SILENT.
        // A subgrid on a rotor is unnamed by the builder and does not match, which is
        // correct: only the named grids are the fixture.
        public static FixtureGrid Parse(IMyCubeGrid grid)
        {
            string name = grid.DisplayName;
            if (string.IsNullOrEmpty(name)) return null;

            string[] parts = name.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            string head = parts[0];
            if (head.Length != 4 || !head.StartsWith("RP-")) return null;

            var f = new FixtureGrid { Letter = char.ToUpperInvariant(head[3]), Grid = grid, Name = name };
            for (int i = 1; i < parts.Length; i++) f.Tags.Add(parts[i].ToUpperInvariant());
            return f;
        }
    }

    // Everything a probe needs for one sample, gathered once so every probe judges the
    // same moment and the sphere query is paid for once.
    public sealed class Ctx
    {
        public int Sample;
        public long Tick;
        public double Seconds;   // simulation seconds since load, from ticks

        public readonly Dictionary<char, FixtureGrid> Fix = new Dictionary<char, FixtureGrid>();
        public readonly List<string> Duplicates = new List<string>();

        public FixtureGrid Observer;
        public IMyCubeBlock Dish;
        public bool HasCenter;
        public Vector3D Center;
        public string CenterSource = "none";

        public double SyncRadius;
        public List<IMyEntity> Sphere;             // null if the query threw
        public readonly HashSet<long> SphereGridIds = new HashSet<long>();
        public double SphereMs = -1;
        public string SphereError = "";

        public FixtureGrid Get(char letter)
        {
            FixtureGrid f;
            return Fix.TryGetValue(letter, out f) ? f : null;
        }

        public bool InSphere(IMyEntity e)
        {
            return e != null && SphereGridIds.Contains(e.EntityId);
        }

        public double DistanceTo(IMyEntity e)
        {
            return HasCenter ? Vector3D.Distance(Center, e.GetPosition()) : -1;
        }

        // True when the dish's block subtype is ours.
        public static bool IsDish(IMyCubeBlock b)
        {
            if (b == null) return false;
            string s = b.BlockDefinition.SubtypeName;
            return s == "GT_RotatingRadarDish" || s == "GT_RotatingRadarDish_S";
        }

        public static Ctx Build(int sample, long tick)
        {
            var c = new Ctx { Sample = sample, Tick = tick, Seconds = tick / 60.0 };

            // Local enumeration for the FIXTURE LOOKUP. This is deliberately not the
            // sphere query: 0.1 must be able to tell "the grid exists on this machine
            // but the sphere did not return it" (FAIL) from "there is no such grid"
            // (INCONCLUSIVE).
            var all = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(all, e => e is IMyCubeGrid);
            foreach (var e in all)
            {
                var g = e as IMyCubeGrid;
                if (g == null) continue;
                var f = Fixture.Parse(g);
                if (f == null) continue;
                if (c.Fix.ContainsKey(f.Letter)) { c.Duplicates.Add(f.Name); continue; }
                c.Fix[f.Letter] = f;
            }

            c.Observer = c.Get('O');
            if (c.Observer != null)
            {
                var slims = new List<IMySlimBlock>();
                c.Observer.Grid.GetBlocks(slims, null);
                for (int i = 0; i < slims.Count; i++)
                {
                    var fat = slims[i].FatBlock;
                    if (IsDish(fat)) { c.Dish = fat; break; }
                }

                if (c.Dish != null) { c.Center = c.Dish.GetPosition(); c.HasCenter = true; c.CenterSource = "dish"; }
                else { c.Center = c.Observer.Grid.GetPosition(); c.HasCenter = true; c.CenterSource = "observer-grid"; }
            }
            else
            {
                var player = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.Player;
                if (player != null && player.Character != null)
                {
                    c.Center = player.Character.GetPosition();
                    c.HasCenter = true;
                    c.CenterSource = "player";
                }
            }

            double radius = 3000;
            try { radius = MyAPIGateway.Session.SessionSettings.SyncDistance; } catch { }
            if (radius < 100) radius = 3000;
            c.SyncRadius = radius;

            if (c.HasCenter)
            {
                try
                {
                    var sw = Timing.Start();
                    var sphere = new BoundingSphereD(c.Center, radius);
                    c.Sphere = MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere);
                    c.SphereMs = Timing.Ms(sw);
                    if (c.Sphere != null)
                        for (int i = 0; i < c.Sphere.Count; i++)
                            if (c.Sphere[i] is IMyCubeGrid) c.SphereGridIds.Add(c.Sphere[i].EntityId);
                }
                catch (Exception e)
                {
                    c.Sphere = null;
                    c.SphereError = e.GetType().Name + ": " + e.Message;
                }
            }

            return c;
        }

        // Fat blocks of a grid, or an empty list. GetBlocks is the pattern the mod already
        // uses (TextPanels.Search); it is not the cheapest call, which does not matter for
        // eleven grids sampled every two seconds.
        public static List<IMyCubeBlock> FatBlocks(IMyCubeGrid grid)
        {
            var result = new List<IMyCubeBlock>();
            var slims = new List<IMySlimBlock>();
            grid.GetBlocks(slims, null);
            for (int i = 0; i < slims.Count; i++)
                if (slims[i].FatBlock != null) result.Add(slims[i].FatBlock);
            return result;
        }
    }
}
