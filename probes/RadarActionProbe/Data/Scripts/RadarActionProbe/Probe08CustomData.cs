using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace RadarActionProbe
{
    // 0.8 - Custom Data written by mod code, on a client.
    //
    // QUESTION
    //   When mod code on a dedicated-server client sets a dish's CustomData, does it reach
    //   the server and other clients, and does it survive a save? The selected contact
    //   is stored in the Custom Data of every dish on the ship (plan decision D3), so this
    //   decides whether selection needs a network message of its own.
    //
    // MECHANISM
    //   "/radarprobe write" (typed on any machine) writes a fresh stamp into every dish on
    //   RP-O:
    //
    //       [RadarProbe]
    //       Stamp = <utc ticks>-<random>
    //
    //   Everything outside the [RadarProbe] section is preserved, and checked. Every
    //   machine running this mod then POLLS dish Custom Data once a second and logs the
    //   first sample in which each new stamp appears. It does NOT rely on the
    //   CustomDataChanged event: ENGINE_TRAPS 18 records that event not reaching a mod
    //   handler on a dedicated server, and a probe built on it would test the event, not
    //   the data.
    //
    // WHAT IS LOGGED
    //   WROTE     stamp, dish, lengths before and after, whether the read-back matches,
    //             whether text outside the section survived, and terminal access
    //   SEEN      stamp, dish, seconds since the stamp was written, and whether THIS
    //             machine is the one that wrote it (a writer always sees its own stamp)
    //   BASELINE  what was in each dish at load - the evidence for persistence
    //
    // The stamp carries the writer's UTC clock so a reader can compute a delay. That is
    // only as good as the two machines' clocks; on separate machines a late SEEN is
    // reported INCONCLUSIVE rather than FAIL for that reason.
    //
    // PROCEDURE (needs you)
    //   1. From your client on the dedicated server: /radarprobe write.
    //   2. Optionally repeat from a second player who has access to RP-O, and from one
    //      who does not (that is the 0.8.access record).
    //   3. Save the server, restart it, rejoin. The first poll after load logs BASELINE;
    //      a stamp there is 0.8.persist.
    //   4. /radarprobe status prints the tally.
    //
    // WHAT DECIDES IT
    //   0.8.preserve  PASS  text outside the section is byte-identical after the write
    //   0.8.reach     PASS  a machine that did not write the stamp saw it within 2 s
    //                 A stamp that appears in the WROTE line and in no other log is a
    //                 FAIL, decided at the gate by the absence of a SEEN line.
    //   0.8.persist   PASS  a stamp this session did not write is present at load
    //   0.8.access    RECORDED: HasLocalPlayerAccess and the read-back at each write
    //
    // WHAT EACH RESULT CHANGES
    //   reach FAIL -> selection travels as a mod network message to the server, which
    //                 writes the Custom Data itself. SealSync.cs already runs this mod's
    //                 networking and is the pattern.
    //   access     -> if a player without access can change the selection, the selection
    //                 write must check terminal access itself.
    public sealed class Probe08CustomData
    {
        public const string Id = "0.8";

        private const string Header = "[RadarProbe]";
        private const string Key = "Stamp";

        private List<IMyTerminalBlock> _dishes = new List<IMyTerminalBlock>();
        private long _lastRefreshTick = -100000;
        private bool _baselineDone;

        private readonly HashSet<string> _seen = new HashSet<string>();
        private readonly HashSet<string> _wroteHere = new HashSet<string>();
        private readonly Random _rng = new Random();
        private int _pollSample;

        // ---- writing ----

        public void Write()
        {
            Refresh(0, true);
            if (_dishes.Count == 0)
            {
                Log.Line(Id, "WROTE nothing: no dish found on a grid named RP-O");
                return;
            }

            string stamp = DateTime.UtcNow.Ticks + "-" + _rng.Next(1000, 9999);
            _wroteHere.Add(stamp);

            for (int i = 0; i < _dishes.Count; i++)
            {
                var d = _dishes[i];
                try
                {
                    string before = d.CustomData ?? "";
                    string outsideBefore = Outside(before);
                    string updated = WithStamp(before, stamp);

                    bool access = false;
                    try { access = d.HasLocalPlayerAccess(); } catch { }

                    d.CustomData = updated;

                    string readBack = d.CustomData ?? "";
                    string outsideAfter = Outside(readBack);
                    bool preserved = outsideBefore == outsideAfter;

                    Log.Line(Id, "WROTE stamp=" + stamp + " dish=" + d.EntityId + " lenBefore=" + before.Length
                        + " lenAfter=" + readBack.Length + " readBackMatches=" + Log.B(readBack == updated)
                        + " outsideSameAsBefore=" + Log.B(preserved) + " outsideLen=" + outsideAfter.Length
                        + " outsideChecksum=" + Checksum(outsideAfter));

                    Log.Record(Id, "access", "dish=" + d.EntityId + " hasLocalPlayerAccess=" + Log.B(access)
                        + " readBackMatches=" + Log.B(readBack == updated));

                    if (preserved) Log.Pass("0.8.preserve", "text outside [RadarProbe] unchanged on the writer (" + outsideAfter.Length + " chars)");
                    else Log.Fail("0.8.preserve", "text outside [RadarProbe] changed on the writer");
                }
                catch (Exception e) { Log.Threw(Id, e); }
            }

            Log.Inconclusive("0.8.reach", "wrote stamp " + stamp + ": look for a SEEN line with this stamp in the SERVER and any other client log; none means FAIL");
        }

        // Custom Data with our section replaced (or appended). Lines are split on \n only,
        // so any \r stays attached to its line and comes back out unchanged.
        private static string WithStamp(string data, string stamp)
        {
            var kept = new List<string>(OutsideLines(data));

            // No blank line is invented in the middle of the player's text: a separator is
            // added only if the text does not already end with a newline.
            string outside = string.Join("\n", kept.ToArray());
            var sb = new StringBuilder(outside);
            if (outside.Length > 0 && !outside.EndsWith("\n")) sb.Append("\n");
            sb.Append(Header).Append("\n").Append(Key).Append(" = ").Append(stamp).Append("\n");
            return sb.ToString();
        }

        // The lines outside the [RadarProbe] section. A section runs from its header to the
        // next line that starts a section, or the end of the text.
        private static List<string> OutsideLines(string data)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(data)) return result;

            bool inside = false;
            string[] lines = data.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("["))
                    inside = string.Equals(t, Header, StringComparison.OrdinalIgnoreCase);
                if (!inside) result.Add(lines[i]);
            }
            return result;
        }

        // Compared and checksummed with trailing line breaks removed. Writing the section
        // after text that ended in a newline moves that newline across the header, which is
        // not a change to the player's text and must not read as one.
        private static string Outside(string data)
        {
            return string.Join("\n", OutsideLines(data).ToArray()).TrimEnd('\r', '\n');
        }

        // FNV-1a, 32 bit. string.GetHashCode is not guaranteed to agree between
        // processes, and this number is compared across the CLIENT and SERVER logs.
        private static string Checksum(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619;
                }
                return h.ToString("x8");
            }
        }

        // ---- polling ----

        // Called once a second by the session.
        public void Poll(long tick)
        {
            _pollSample++;
            Log.Alive(Id, _pollSample);

            Refresh(tick, false);
            if (_dishes.Count == 0) return;

            for (int i = 0; i < _dishes.Count; i++)
            {
                var d = _dishes[i];
                if (d.Closed) continue;

                string data = d.CustomData ?? "";
                string stamp = ReadStamp(data);

                if (!_baselineDone)
                {
                    // The first look after load. A stamp here was not written by this
                    // session, so it survived a save and a restart.
                    Log.Line(Id, "BASELINE dish=" + d.EntityId + " stamp=" + (stamp.Length > 0 ? stamp : "none") + " len=" + data.Length);
                    if (stamp.Length > 0)
                    {
                        _seen.Add(d.EntityId + ":" + stamp);
                        Log.Pass("0.8.persist", "stamp " + stamp + " present at load (written " + AgeText(stamp) + " ago)");
                    }
                    continue;
                }

                if (stamp.Length == 0) continue;
                string key = d.EntityId + ":" + stamp;
                if (!_seen.Add(key)) continue;

                bool local = _wroteHere.Contains(stamp);
                double after = AgeSeconds(stamp);
                string outside = Outside(data);
                Log.Line(Id, "SEEN stamp=" + stamp + " dish=" + d.EntityId + " after=" + Log.F1(after)
                    + "s writtenHere=" + Log.B(local) + " outsideLen=" + outside.Length + " outsideChecksum=" + Checksum(outside));

                if (!local)
                {
                    if (after <= 2.0) Log.Pass("0.8.reach", "stamp " + stamp + " reached this machine in " + Log.F1(after) + " s");
                    else Log.Inconclusive("0.8.reach", "stamp " + stamp + " arrived after " + Log.F1(after) + " s; clock skew between machines can account for this - the gate compares log timestamps");
                }
            }

            if (!_baselineDone)
            {
                _baselineDone = true;
                bool any = false;
                foreach (var k in _seen) { any = true; break; }
                if (!any) Log.Inconclusive("0.8.persist", "no stamp present at load - write one, save, restart the server, rejoin");
            }
        }

        private static string ReadStamp(string data)
        {
            bool inside = false;
            string[] lines = data.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith("["))
                {
                    inside = string.Equals(t, Header, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inside) continue;

                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(t.Substring(0, eq).Trim(), Key, StringComparison.OrdinalIgnoreCase))
                    return t.Substring(eq + 1).Trim();
            }
            return "";
        }

        // Seconds between the stamp's writer clock and this machine's, or -1.
        private static double AgeSeconds(string stamp)
        {
            int dash = stamp.IndexOf('-');
            long ticks;
            if (dash <= 0 || !long.TryParse(stamp.Substring(0, dash), out ticks)) return -1;
            return (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;
        }

        private static string AgeText(string stamp)
        {
            double sec = AgeSeconds(stamp);
            return sec < 0 ? "unknown" : Log.F1(sec) + " s";
        }

        // Dishes are looked up by name, which walks every entity, so the list is cached and
        // refreshed every ten seconds, or when a member has closed.
        private void Refresh(long tick, bool force)
        {
            bool stale = force || tick - _lastRefreshTick >= 600;
            for (int i = 0; !stale && i < _dishes.Count; i++) if (_dishes[i].Closed) stale = true;
            if (!stale && _dishes.Count > 0) return;

            _lastRefreshTick = tick;
            _dishes = Fixture.FindDishes();
        }
    }
}
