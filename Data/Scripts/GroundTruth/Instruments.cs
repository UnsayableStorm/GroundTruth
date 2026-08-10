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

        public const int CapEnv = 1;
        public const int CapSun = 2;
        public const int CapRad = 4;
        public const int CapWx = 8;
        public const int CapHab = 16;
        public const int CapBio = 64;

        public struct Info
        {
            public int Role;
            public int Capabilities;
            public string Subpart;   // rotating subpart dummy name, null if static
        }

        // Alt and small-grid variants are the SAME instrument in a different shell:
        // same role, same capabilities, same code path. They differ only in silhouette
        // and, for the animated ones, in the name of the subpart that spins.
        private static readonly Dictionary<string, Info> _table = new Dictionary<string, Info>
        {
            { "GT_RadiationMonitor",      New(RoleRadiation, CapEnv | CapSun | CapRad, null) },
            { "GT_RadiationMonitor_S",    New(RoleRadiation, CapEnv | CapSun | CapRad, null) },
            { "GT_RadiationMonitorAlt",   New(RoleRadiation, CapEnv | CapSun | CapRad, null) },
            { "GT_RadiationMonitorAlt_S", New(RoleRadiation, CapEnv | CapSun | CapRad, null) },

            { "GT_HabitatMonitor",        New(RoleHabitat,   CapEnv | CapHab | CapRad, null) },
            { "GT_HabitatMonitor_S",      New(RoleHabitat,   CapEnv | CapHab | CapRad, null) },

            { "GT_WeatherStation",        New(RoleWeather,   CapEnv | CapSun | CapWx, "SensorPod") },
            { "GT_WeatherStation_S",      New(RoleWeather,   CapEnv | CapSun | CapWx, "SG_SensorPod") },
            { "GT_WeatherStationAlt",     New(RoleWeather,   CapEnv | CapSun | CapWx, "SensorPod2") },
            { "GT_WeatherStationAlt_S",   New(RoleWeather,   CapEnv | CapSun | CapWx, "SG_SensorPod2") },

            { "GT_BioScanner",            New(RoleBio,       CapEnv | CapBio, null) },
            { "GT_BioScanner_S",          New(RoleBio,       CapEnv | CapBio, null) },
        };

        // Deliberately NOT in the table: GT_RotatingRadarDish. It is a vanilla radio
        // antenna riding along in the pack, not an instrument, and claiming it here
        // would make the API assert that it measures something.
        private static Info New(int role, int caps, string subpart)
        {
            return new Info { Role = role, Capabilities = caps, Subpart = subpart };
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

        public static int CapabilitiesOf(string subtype)
        {
            Info i;
            return TryGet(subtype, out i) ? i.Capabilities : 0;
        }
    }
}
