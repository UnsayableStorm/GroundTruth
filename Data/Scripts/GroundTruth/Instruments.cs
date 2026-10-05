using System.Collections.Generic;
using Sandbox.ModAPI;

namespace GroundTruth
{
    // The single registry of what each block subtype is.
    //
    // This used to be four parallel switch statements - IsOurs, RoleOf, capability
    // lookup and icon selection - and adding the Bio Scanner meant updating all four.
    // One was missed, the LCD app found no instrument on the grid, and the panel read
    // "NO INSTRUMENT" beside a working block. Going from 4 subtypes to 10 would have
    // made that failure routine.
    //
    // Everything now reads from this table. Adding a variant is one line here.
    public static class Instruments
    {
        public const int RoleNone = 0;
        public const int RoleRadiation = 1;
        public const int RoleHabitat = 2;
        public const int RoleWeather = 3;
        public const int RoleBio = 4;
        public const int RoleRadar = 5;

        public const int CapEnv = 1;
        public const int CapSun = 2;
        public const int CapRad = 4;
        public const int CapWx = 8;
        public const int CapHab = 16;
        public const int CapBio = 64;
        // 32 is skipped on purpose: it sits beside the reserved, unimplemented GT_Grid
        // namespace, and a bit that looks free next to a reservation is how two meanings
        // end up sharing one number.
        public const int CapRadar = 128;

        public struct Info
        {
            public int Role;
            public int Capabilities;
            public string Subpart;   // rotating subpart dummy name, null if static
            public float PowerKW;    // draw while enabled - see InstrumentPower.cs
            // True for a block whose own type already draws power and is bound by its
            // own component, so InstrumentPower deliberately does not attach to it.
            public bool SelfPowered;
        }

        // Power, in kilowatts, and deliberately small.
        //
        // UpgradeModule carries no power draw of its own - its definition has exactly
        // one field - so every watt here is attached in code. These blocks are
        // instruments, not machinery: the cost should be felt on a solar-and-battery
        // outpost that has just gone into shadow, and be beneath notice on a ship.
        //
        // Scaled by what each one actually does rather than by its size. The bio
        // scanner sweeps entities in a radius, the weather station reads a planet's
        // weather state, the radiation monitor traces line of sight to the sun, and the
        // habitat monitor asks whether the room it sits in is sealed.
        private const float PowerRadiation = 12f;
        private const float PowerWeather = 15f;
        private const float PowerBio = 30f;
        private const float PowerHabitat = 6f;

        // Alt and small-grid variants are the SAME instrument in a different shell:
        // same role, same capabilities, same code path. They differ only in silhouette
        // and, for the animated ones, in the name of the subpart that spins.
        private static readonly Dictionary<string, Info> _table = new Dictionary<string, Info>
        {
            { "GT_RadiationMonitor",      New(RoleRadiation, CapEnv | CapSun | CapRad, null, PowerRadiation) },
            { "GT_RadiationMonitor_S",    New(RoleRadiation, CapEnv | CapSun | CapRad, null, PowerRadiation) },
            { "GT_RadiationMonitorAlt",   New(RoleRadiation, CapEnv | CapSun | CapRad, null, PowerRadiation) },
            { "GT_RadiationMonitorAlt_S", New(RoleRadiation, CapEnv | CapSun | CapRad, null, PowerRadiation) },

            { "GT_HabitatMonitor",        New(RoleHabitat,   CapEnv | CapHab | CapRad, null, PowerHabitat) },
            { "GT_HabitatMonitor_S",      New(RoleHabitat,   CapEnv | CapHab | CapRad, null, PowerHabitat) },
            { "GT_HabitatMonitorAlt",     New(RoleHabitat,   CapEnv | CapHab | CapRad, null, PowerHabitat) },
            { "GT_HabitatMonitorAlt_S",   New(RoleHabitat,   CapEnv | CapHab | CapRad, null, PowerHabitat) },

            { "GT_WeatherStation",        New(RoleWeather,   CapEnv | CapSun | CapWx, "SensorPod", PowerWeather) },
            { "GT_WeatherStation_S",      New(RoleWeather,   CapEnv | CapSun | CapWx, "SG_SensorPod", PowerWeather) },
            { "GT_WeatherStationAlt",     New(RoleWeather,   CapEnv | CapSun | CapWx, "SensorPod2", PowerWeather) },
            { "GT_WeatherStationAlt_S",   New(RoleWeather,   CapEnv | CapSun | CapWx, "SG_SensorPod2", PowerWeather) },

            { "GT_BioScanner",            New(RoleBio,       CapEnv | CapBio, null, PowerBio) },
            { "GT_BioScanner_S",          New(RoleBio,       CapEnv | CapBio, null, PowerBio) },
            { "GT_BioScannerAlt",         New(RoleBio,       CapEnv | CapBio, null, PowerBio) },
            { "GT_BioScannerAlt_S",       New(RoleBio,       CapEnv | CapBio, null, PowerBio) },

            // The Rotating Antenna is the tactical sensor (docs/TACTICAL_PLAN.md D0.1).
            // It used to be absent from this table, as a vanilla antenna riding along in
            // the pack. It now measures something - contacts - so it is in, with
            // CapRadar ALONE (D6): it takes no environment, sun, seal, radiation, weather
            // or bio readings, and an instrument that skips a reading holds zeros, not
            // -1s. Keeping those bits off is what stops any consumer from asking it.
            //
            // No subpart: SpinningAntenna names the dish's own and spins it. No power:
            // a RadioAntenna has its own draw and its own IsWorking (D0.2), which is why
            // InstrumentPower is not bound to it - hence SelfPowered.
            //
            // Its GT_ terminal properties stay deferred (D0.3): TerminalApi registers on
            // IMyUpgradeModule only, so being in this table publishes nothing new.
            { "GT_RotatingRadarDish",     SelfPowered(RoleRadar, CapRadar) },
            { "GT_RotatingRadarDish_S",   SelfPowered(RoleRadar, CapRadar) },
        };

        private static Info New(int role, int caps, string subpart, float powerKW)
        {
            return new Info { Role = role, Capabilities = caps, Subpart = subpart, PowerKW = powerKW };
        }

        private static Info SelfPowered(int role, int caps)
        {
            return new Info { Role = role, Capabilities = caps, Subpart = null, PowerKW = 0f, SelfPowered = true };
        }

        public static bool TryGet(string subtype, out Info info)
        {
            if (string.IsNullOrEmpty(subtype)) { info = default(Info); return false; }
            return _table.TryGetValue(subtype, out info);
        }

        public static bool Is(IMyCubeBlock block)
        {
            Info unused;
            return block != null && TryGet(block.BlockDefinition.SubtypeName, out unused);
        }

        public static int RoleOf(string subtype)
        {
            Info i;
            return TryGet(subtype, out i) ? i.Role : RoleNone;
        }

        // Kilowatts drawn while enabled, 0 for anything not ours.
        public static float PowerKWOf(string subtype)
        {
            Info i;
            return TryGet(subtype, out i) ? i.PowerKW : 0f;
        }

        /// <summary>
        /// Table entries that no game logic component attaches to. Any result is a bug:
        /// such a block has no power draw and never registers for seal sync, which is
        /// invisible until someone stands in front of the panel.
        ///
        /// SelfPowered entries are skipped, and only those: the dish draws its own power
        /// as a RadioAntenna and InstrumentPower is deliberately not bound to it. The
        /// check stays exactly as loud as before for every UpgradeModule instrument,
        /// which is where real drift happens.
        /// </summary>
        public static List<string> SubtypesWithoutComponent(string[] attachedTo)
        {
            var missing = new List<string>();
            foreach (var kv in _table)
            {
                if (kv.Value.SelfPowered) continue;
                var key = kv.Key;
                bool found = false;
                for (int i = 0; i < attachedTo.Length; i++)
                    if (attachedTo[i] == key) { found = true; break; }
                if (!found) missing.Add(key);
            }
            return missing;
        }

        public static int CapabilitiesOf(string subtype)
        {
            Info i;
            return TryGet(subtype, out i) ? i.Capabilities : 0;
        }
    }
}
