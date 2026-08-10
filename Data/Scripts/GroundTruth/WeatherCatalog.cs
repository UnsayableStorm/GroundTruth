using System.Collections.Generic;
using Sandbox.Game.Entities;

namespace GroundTruth
{
    // What weather a planet can actually produce, read from its own definition.
    //
    // The game declares this per planet generator: which effects exist, how likely each
    // is, and how long it runs. That is better than anything we can infer, and it covers
    // modded planets and modded weather we have never seen - a hardcoded list of the 13
    // vanilla effects would silently omit them.
    //
    // CACHED PER GENERATOR, not per planet. Definitions do not change during a session,
    // and every Earthlike shares one generator, so the walk happens once per planet TYPE
    // no matter how many planets or instruments exist.
    //
    // Weather generators are keyed by voxel material, so an Earthlike's snow biome can
    // offer effects its grass biome never will. Names() returns the union - what this
    // world can throw at you somewhere - while ForBiome() answers the narrower question.
    public static class WeatherCatalog
    {
        public struct Entry
        {
            public string Name;
            public int Weight;        // relative likelihood within its biome
            public int MinLength;     // seconds, declared by the definition
            public int MaxLength;
        }

        private class PlanetInfo
        {
            public readonly List<Entry> All = new List<Entry>();
            public readonly Dictionary<string, List<Entry>> ByBiome =
                new Dictionary<string, List<Entry>>();
        }

        private static readonly Dictionary<string, PlanetInfo> _cache =
            new Dictionary<string, PlanetInfo>();

        private static PlanetInfo Get(MyPlanet planet)
        {
            if (planet == null || planet.Generator == null) return null;

            var key = planet.Generator.Id.SubtypeName;
            PlanetInfo info;
            if (_cache.TryGetValue(key, out info)) return info;

            info = new PlanetInfo();
            try
            {
                var gens = planet.Generator.WeatherGenerators;
                if (gens != null)
                {
                    foreach (var gen in gens)
                    {
                        if (gen.Weathers == null) continue;

                        var biome = gen.Voxel ?? "";
                        List<Entry> list;
                        if (!info.ByBiome.TryGetValue(biome, out list))
                        {
                            list = new List<Entry>();
                            info.ByBiome[biome] = list;
                        }

                        foreach (var w in gen.Weathers)
                        {
                            if (string.IsNullOrEmpty(w.Name)) continue;

                            var e = new Entry
                            {
                                Name = w.Name,
                                Weight = w.Weight,
                                MinLength = w.MinLength,
                                MaxLength = w.MaxLength
                            };
                            list.Add(e);

                            // Union across biomes, first definition wins. A duplicate
                            // name in two biomes is the same effect with different odds.
                            bool seen = false;
                            for (int i = 0; i < info.All.Count; i++)
                                if (info.All[i].Name == e.Name) { seen = true; break; }
                            if (!seen) info.All.Add(e);
                        }
                    }
                }
            }
            catch { }   // a malformed or modded generator must not take the mod down

            _cache[key] = info;
            return info;
        }

        // Every effect this planet can produce, anywhere on it.
        public static List<Entry> All(MyPlanet planet)
        {
            var info = Get(planet);
            return info == null ? new List<Entry>() : info.All;
        }

        // Declared duration for a named effect, or false if the planet does not list it.
        // This is the game's own figure, and unlike the observed estimate it is available
        // the instant weather appears - including admin-forced weather with no ramp.
        public static bool Duration(MyPlanet planet, string weather, out int min, out int max)
        {
            min = max = 0;
            var info = Get(planet);
            if (info == null || string.IsNullOrEmpty(weather)) return false;

            for (int i = 0; i < info.All.Count; i++)
            {
                if (info.All[i].Name != weather) continue;
                min = info.All[i].MinLength;
                max = info.All[i].MaxLength;
                return max > 0;
            }
            return false;
        }

        public static int TypeCount(MyPlanet planet)
        {
            var info = Get(planet);
            return info == null ? 0 : info.All.Count;
        }


        // ------------------------------------------------------------------
        // The global effect table.
        //
        // A planet's declaration says what happens there naturally. This says what an
        // effect IS - and the two are different sets: the admin panel forced
        // ElectricStorm onto an EarthLike that never declares it.
        //
        // Read from MyWeatherEffectDefinition, so it covers every effect in the game
        // including modded ones nobody has forced by hand. The weather survey measured
        // this table one effect at a time; the game has been carrying it all along.
        //
        // Built once on first use and kept - definitions do not change during a session.

        public struct Effect
        {
            public bool Known;
            public string Name;

            // Declared multipliers. The LIVE values from IMyWeatherEffects are what the
            // instrument reports; these are what the effect will do at full strength,
            // which is a different and equally real thing - it can be stated before the
            // storm arrives.
            public float Solar;
            public float Wind;
            public float Temperature;
            public float Oxygen;

            // Hazards. Neither has any equivalent in the measured table: the survey
            // established that weather does not drive radiation through the solar
            // multiplier, so a declared radiation source is a separate mechanism.
            public bool HasRadiation;
            public float RadiationGain;
            public float RadiationMinIntensity;

            public bool HasHealth;
            public float HealthMinIntensity;
            public float DamageMin;
            public float DamageMax;
        }

        private static Dictionary<string, Effect> _effects;

        // WHY THIS TABLE IS STATIC
        //
        // MyWeatherEffectDefinition compiles fine and is never delivered. Asking the
        // definition manager for a weather effect by id returns an object, but it is a
        // plain MyDefinitionBase - the concrete type does not materialise for mod code,
        // so the cast fails and every field is unreachable. Enumeration finds zero of
        // them for the same reason. Verified in game 2026-08-10:
        //
        //   WeatherDefProbe: enumerated=0  weather='Hailstorm'
        //                    byId(Hailstorm)=found but wrong type: MyDefinitionBase
        //
        // The data itself is not secret - it is in Content/Data/WeatherEffects.sbc. So
        // the vanilla table is generated from that file and compiled in, rather than
        // pretended away or left as a feature that silently never works.
        //
        // THE COST, stated plainly: modded weather effects are NOT in this table and
        // report Known = false. Every consumer sees a sentinel rather than a wrong
        // number, and the panels simply omit a hazard section they cannot vouch for.
        //
        // Generated from vanilla WeatherEffects.sbc. If Keen adds effects, regenerate.
        private static void BuildEffects()
        {
            _effects = new Dictionary<string, Effect>();

            E("AlienExtremeHeat", 1.75f, 0.10f, 11.00f, 1.00f, 0.00f, 0.00f, 0f, 0f, 0.00f);
            E("AlienFogHeavy", 0.15f, 0.10f, 0.30f, 0.00f, 0.40f, 0.30f, 0f, 0f, 0.00f);
            E("AlienFogLight", 0.20f, 0.20f, 0.30f, 0.00f, 0.30f, 0.30f, 0f, 0f, 0.00f);
            E("AlienHeatWave", 1.35f, 0.75f, 8.00f, 1.00f, 0.00f, 0.00f, 0f, 0f, 0.00f);
            E("AlienRainHeavy", 0.30f, 1.45f, 0.40f, 1.00f, -0.60f, 0.50f, 5f, 9f, 0.50f);
            E("AlienRainLight", 0.60f, 1.20f, 0.70f, 1.00f, -0.60f, 0.50f, 3f, 7f, 0.50f);
            E("AlienSandStormHeavy", 0.10f, 2.25f, 11.00f, 1.00f, 0.00f, 0.00f, 5f, 40f, 0.50f);
            E("AlienSandStormLight", 0.20f, 1.75f, 8.00f, 1.00f, 0.00f, 0.00f, 5f, 40f, 0.50f);
            E("AlienThunderstormHeavy", 0.30f, 1.75f, 0.40f, 1.00f, -0.60f, 0.50f, 5f, 9f, 0.50f);
            E("AlienThunderstormLight", 0.60f, 1.20f, 0.70f, 1.00f, -0.60f, 0.50f, 3f, 7f, 0.50f);
            E("ColdFront", 0.80f, 1.25f, 0.30f, 1.00f, 0.00f, 0.00f, 0f, 0f, 0.00f);
            E("Dust", 0.80f, 1.25f, 1.60f, 1.00f, 0.00f, 0.00f, 0f, 0f, 0.00f);
            E("ElectricStorm", 0.10f, 2.25f, 8.00f, 0.25f, 0.60f, 0.35f, 5f, 40f, 0.50f);
            E("ExtremeCold", 0.80f, 1.55f, -1.00f, 1.00f, 0.00f, 0.00f, 0f, 0f, 0.00f);
            E("ExtremeHeat", 1.75f, 0.10f, 2.00f, 1.00f, 0.00f, 0.00f, 0f, 0f, 0.00f);
            E("FogHeavy", 0.15f, 0.10f, 0.30f, 1.00f, 0.00f, 0.00f, 0f, 0f, 0.00f);
            E("FogLight", 0.20f, 0.20f, 0.30f, 1.00f, 0.00f, 0.00f, 0f, 0f, 0.00f);
            E("Hailstorm", 0.25f, 2.00f, 0.10f, 1.00f, 0.00f, 0.00f, 5f, 40f, 0.50f);
            E("HeatWave", 1.35f, 0.65f, 1.55f, 1.00f, 0.00f, 0.00f, 0f, 0f, 0.00f);
            E("HighWinds", 1.00f, 1.45f, 1.00f, 1.00f, 0.00f, 0.00f, 0f, 0f, 0.00f);
            E("LowWinds", 1.00f, 0.30f, 1.00f, 1.00f, 0.00f, 0.00f, 0f, 0f, 0.00f);
            E("MarsSnow", 0.75f, 0.20f, 0.25f, 1.00f, 0.00f, 0.00f, 0f, 0f, 0.00f);
            E("MarsStormHeavy", 0.10f, 2.50f, 1.00f, 0.25f, 0.00f, 0.00f, 5f, 40f, 0.50f);
            E("MarsStormLight", 0.30f, 1.75f, 1.00f, 0.50f, 0.00f, 0.00f, 5f, 40f, 0.50f);
            E("RainHeavy", 0.30f, 1.45f, 0.40f, 1.00f, -0.60f, 0.50f, 0f, 0f, 0.00f);
            E("RainLight", 0.60f, 1.20f, 0.70f, 1.00f, -0.60f, 0.50f, 0f, 0f, 0.00f);
            E("SandStormHeavy", 0.10f, 2.25f, 3.00f, 0.25f, 0.00f, 0.00f, 5f, 40f, 0.50f);
            E("SandStormLight", 0.20f, 1.75f, 2.00f, 0.50f, 0.00f, 0.00f, 5f, 40f, 0.50f);
            E("SnowHeavy", 0.10f, 2.00f, -2.00f, 1.00f, 0.00f, 0.00f, 0f, 0f, 0.00f);
            E("SnowLight", 0.75f, 0.20f, 0.25f, 1.00f, 0.00f, 0.00f, 0f, 0f, 0.00f);
            E("ThunderstormHeavy", 0.30f, 1.75f, 0.40f, 1.00f, -0.60f, 0.50f, 0f, 0f, 0.00f);
            E("ThunderstormLight", 0.60f, 1.20f, 0.70f, 1.00f, -0.60f, 0.50f, 0f, 0f, 0.00f);
        }

        private static void E(string name, float solar, float wind, float temp, float oxy,
                              float radGain, float radMin, float dmgMin, float dmgMax, float hpMin)
        {
            _effects[name] = new Effect
            {
                Known = true,
                Name = name,
                Solar = solar,
                Wind = wind,
                Temperature = temp,
                Oxygen = oxy,

                // NOT > 0. Rain declares -0.60, which is shelter and must register as a
                // known radiation term rather than as no term at all.
                HasRadiation = radGain != 0f,
                RadiationGain = radGain,
                RadiationMinIntensity = radMin,

                HasHealth = dmgMax > 0f,
                DamageMin = dmgMin,
                DamageMax = dmgMax,
                HealthMinIntensity = hpMin
            };
        }

        // What this effect is, whether or not any planet declares it.
        // Unknown names - modded effects - return Known = false, and every consumer
        // treats that as "no information" rather than as zeros.
        public static Effect ForName(string weather)
        {
            if (string.IsNullOrEmpty(weather)) return default(Effect);
            if (_effects == null) BuildEffects();

            Effect e;
            return _effects.TryGetValue(weather, out e) ? e : default(Effect);
        }

        public static int EffectCount
        {
            get
            {
                if (_effects == null) BuildEffects();
                return _effects.Count;
            }
        }

        public static void Clear()
        {
            _cache.Clear();
            _effects = null;
        }
    }
}
