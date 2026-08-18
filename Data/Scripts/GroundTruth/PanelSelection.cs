using System.Collections.Generic;

namespace GroundTruth
{
    // Which instrument a panel reads, when the grid has more than one of a role.
    //
    // The original rule was "the first block of the wanted role found on this grid",
    // which is enumeration order and therefore arbitrary. On a ship with one of each
    // that is correct and invisible. On a base with a Habitat Monitor per room it is
    // wrong on every panel but one, and wrong in a way that looks like a working
    // display - the seal it reports is a real seal, just not the room you are standing
    // in. A reading attributed to the wrong place is exactly the failure this mod
    // exists to avoid, so it is not enough to make it configurable; the DEFAULT has to
    // stop being arbitrary.
    //
    // So there are two rules, in order:
    //
    //   1. NEAREST wins. No configuration, and on a normal base it is already right:
    //      the LCD in the galley is closer to the galley's monitor than to the
    //      dormitory's. This preserves the no-setup promise the mod is built on.
    //
    //   2. A NAME in the panel's Custom Data overrides it, for the cases where nearest
    //      is not what you meant - an airlock screen that should report the greenhouse,
    //      a bridge console showing a specific hold.
    //
    // Deliberately NOT used for matching: the pressurised room the panel sits in, which
    // would be the ideal key for the Habitat app. Room identity comes from the
    // pressurisation system, and a dedicated-server CLIENT has none - see ENGINE_TRAPS
    // trap 10. Selection would then work in single player and silently pick a different
    // instrument on a server, which is worse than a rule that is merely approximate.
    //
    // ---- Custom Data format ----
    //
    //   [GroundTruth]
    //   Habitat = Galley
    //   Weather = Mast
    //   Instrument = Bay 2
    //
    // Keys are role names - Radiation, Habitat, Weather, Life (Bio is accepted as an
    // alias) - plus Instrument, which applies to whichever single role the app wants
    // and is the only key the four per-role apps need. A role key beats Instrument.
    //
    // A block with several surfaces - a cockpit, a console - can address one of them:
    //
    //   [GroundTruth.1]
    //   Habitat = Airlock
    //
    // where 1 is the surface index. That section is read after the shared one, so it
    // overrides it for that surface only.
    //
    // Everything outside a [GroundTruth...] section is ignored, so this coexists with
    // whatever else has written to the same Custom Data.
    //
    // Parsed by hand rather than through MyIni. The format is four lines of key=value
    // and hand-parsing costs about thirty lines; MyIni costs a whitelist question
    // answered by a game load, and this mod has paid that toll enough times.
    public sealed class PanelSelection
    {
        private const string Section = "groundtruth";

        // The Custom Data this was parsed from. Reference-equal on most frames because
        // the string is not rebuilt, and a full compare of a few hundred characters once
        // a second is beneath measurement anyway.
        private string _raw;

        // Raw, trimmed, ORIGINAL CASE - the panel prints it back when nothing matches,
        // and echoing a player's "Galley" as "galley" reads like a different word.
        private readonly Dictionary<float, string> _byRole = new Dictionary<float, string>();
        private string _any;

        // The entity id of the chosen instrument, written alongside the name under
        // "<Role>.Id". BOTH are stored because each survives something the other does
        // not, and a selection has to survive both:
        //
        //   an ID survives a RENAME - it is what the block is, not what it is called,
        //   and it is saved with the world, so the binding holds across a reload
        //
        //   a NAME survives a BLUEPRINT PASTE - the pasted sensors are new entities
        //   with new ids, and an id-only binding would come back Automatic on every
        //   screen of a pasted base
        //
        // Id wins when it resolves; the name is the fallback. So renaming a monitor
        // keeps the screens pointed at it, and pasting a base keeps them pointed at the
        // equivalent block in the copy.
        private readonly Dictionary<float, long> _idByRole = new Dictionary<float, long>();

        /// <summary>
        /// Re-parse if the Custom Data changed. True when it did, which is the caller's
        /// cue to drop any instrument it resolved under the old selection.
        /// </summary>
        public bool Refresh(string customData, int surfaceIndex)
        {
            if (customData == null) customData = "";
            if (_raw != null && _raw == customData) return false;

            _raw = customData;
            _byRole.Clear();
            _idByRole.Clear();
            _any = null;

            if (customData.Length == 0) return true;
            if (customData.IndexOf('[') < 0) return true;

            // Shared section first, then the per-surface one, so the narrower wins.
            Parse(customData, Section);
            if (surfaceIndex >= 0)
                Parse(customData, Section + "." + surfaceIndex);

            return true;
        }

        private void Assign(float role, bool isId, string value)
        {
            if (!isId) { _byRole[role] = value; return; }

            // An unparseable id is treated as absent rather than as a selection of
            // nothing, so a hand-mangled line falls back to the name beside it.
            long id;
            _idByRole[role] = value.Length > 0 && long.TryParse(value, out id) ? id : 0L;
        }

        /// <summary>The entity id to bind to for this role, or 0 for "use the name".</summary>
        public long IdFor(float role)
        {
            long id;
            return _idByRole.TryGetValue(role, out id) ? id : 0L;
        }

        /// <summary>The name to match for this role, or null for "nearest".</summary>
        public string For(float role)
        {
            string want;
            if (_byRole.TryGetValue(role, out want))
                return want.Length == 0 ? null : want;   // present but empty = automatic
            return _any;
        }

        /// <summary>The Custom Data key a role is written under.</summary>
        public static string KeyFor(float role)
        {
            if (role == Instruments.RoleRadiation) return "Radiation";
            if (role == Instruments.RoleHabitat) return "Habitat";
            if (role == Instruments.RoleWeather) return "Weather";
            if (role == Instruments.RoleBio) return "Life";
            return null;
        }

        /// <summary>
        /// Return <paramref name="customData"/> with this surface's selection for a role
        /// set to <paramref name="value"/>, or to automatic when it is null or empty.
        ///
        /// This is what the terminal dropdown writes through. Custom Data is the store
        /// because it already saves with the world and already syncs to the server -
        /// a per-surface setting needing neither a new component nor a new network
        /// message. That it stays readable and hand-editable is a side effect, and a
        /// welcome one, but the dropdown is the interface.
        ///
        /// Everything outside our own section is preserved byte for byte, including
        /// another mod's sections and the player's own notes.
        /// </summary>
        public static string Write(string customData, int surfaceIndex, float role,
                                   string value, long entityId)
        {
            var key = KeyFor(role);
            if (key == null) return customData;

            // Name first so it reads as the heading and the id as its footnote, which
            // is also the order they are resolved in reverse: id wins, name is fallback.
            customData = WriteKey(customData, surfaceIndex, key, value);
            return WriteKey(customData, surfaceIndex, key + ".Id",
                            entityId == 0 ? "" : entityId.ToString());
        }

        private static string WriteKey(string customData, int surfaceIndex,
                                       string key, string value)
        {
            if (surfaceIndex < 0) surfaceIndex = 0;
            var header = "[" + Section + "." + surfaceIndex + "]";
            var headerLower = header.ToLowerInvariant();
            var keyLower = key.ToLowerInvariant();
            var line = key + " = " + (value == null ? "" : value.Trim());

            var lines = new List<string>((customData ?? "").Split('\n'));

            // Trailing \r survives Split('\n') on CRLF data and would end up inside a
            // compared token. Strip on read, restore nothing: SE stores plain \n.
            for (int i = 0; i < lines.Count; i++)
                lines[i] = lines[i].TrimEnd('\r');

            int sectionStart = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim().ToLowerInvariant() != headerLower) continue;
                sectionStart = i;
                break;
            }

            if (sectionStart < 0)
            {
                // No section yet. Append one, keeping a blank line before it so this
                // never runs into whatever the player already had there.
                while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
                    lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0) lines.Add("");
                lines.Add(header);
                lines.Add(line);
                return string.Join("\n", lines.ToArray());
            }

            // Inside the section, up to the next header, replace the key if present.
            for (int i = sectionStart + 1; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.Length > 0 && t[0] == '[') break;      // next section

                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                if (t.Substring(0, eq).Trim().ToLowerInvariant() != keyLower) continue;

                lines[i] = line;
                return string.Join("\n", lines.ToArray());
            }

            lines.Insert(sectionStart + 1, line);
            return string.Join("\n", lines.ToArray());
        }

        /// <summary>True when any selector at all is set, for the help text.</summary>
        public bool Any { get { return _any != null || _byRole.Count > 0; } }

        private void Parse(string text, string wantedSection)
        {
            bool inSection = false;

            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue;

                if (line[0] == '[')
                {
                    int close = line.IndexOf(']');
                    if (close < 1) { inSection = false; continue; }
                    var name = line.Substring(1, close - 1).Trim().ToLowerInvariant();
                    inSection = name == wantedSection;
                    continue;
                }

                if (!inSection) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line.Substring(0, eq).Trim().ToLowerInvariant();
                var value = line.Substring(eq + 1).Trim();

                // An EMPTY value is recorded, not skipped. It means "automatic" - and
                // recording it is what lets a per-surface section override a block-wide
                // one back to automatic. Skipping it would silently re-inherit the
                // block-wide name, which is the dropdown saying Automatic and the panel
                // showing a named instrument.
                bool isId = key.EndsWith(".id");
                if (isId) key = key.Substring(0, key.Length - 3);

                switch (key)
                {
                    case "radiation": Assign(Instruments.RoleRadiation, isId, value); break;
                    case "habitat": Assign(Instruments.RoleHabitat, isId, value); break;
                    case "weather": Assign(Instruments.RoleWeather, isId, value); break;
                    case "life":
                    case "bio": Assign(Instruments.RoleBio, isId, value); break;
                    case "instrument": if (!isId) _any = value; break;
                }
            }
        }

        /// <summary>
        /// How well a block's name answers a selector: 2 exact, 1 contains, 0 no.
        /// Case-insensitive, and CONTAINS rather than equals because the useful name is
        /// "Habitat Monitor - Galley" and the useful selector is "Galley".
        /// </summary>
        public static int Rank(string displayName, string want)
        {
            if (want == null) return 1;             // no selector: everything qualifies
            if (string.IsNullOrEmpty(displayName)) return 0;

            var name = displayName.ToLowerInvariant();
            var w = want.ToLowerInvariant();

            if (name == w) return 2;
            return name.IndexOf(w) >= 0 ? 1 : 0;
        }
    }
}
