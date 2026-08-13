using System;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;

namespace GroundTruth
{
    // The public API surface.
    //
    // Readings are exposed as terminal properties rather than a mod-message API, so
    // anything can consume them with no cooperation from us: Programmable Block scripts
    // via GetValueFloat/GetValueBool, other mods the same way, LCD apps, Event
    // Controller events later. Nobody needs our permission and nobody declares a
    // dependency.
    //
    // THESE NAMES ARE A PUBLISHED CONTRACT. Once a script reads GT_RadRate, renaming it
    // breaks that script silently. Add freely; never rename, never retype, never recycle.
    // The full specification lives in the project's API_SURFACE.md.
    //
    // Conventions:
    //   - float and bool only. String properties are unverified and awkward from a PB,
    //     so anything textual is exposed as a numeric enum with the readable version in
    //     the detail pane.
    //   - Read-only. These are instruments, not controls.
    //   - -1 means "no reading" for quantities whose real range is non-negative.
    //     0.0 always means a genuine measured zero. Never NaN - it is correct and
    //     nobody handles it.
    //   - Values are served from the cache the session refreshes once a second. A getter
    //     never computes, so a script polling every tick cannot trigger a raycast per
    //     tick.
    public static class TerminalApi
    {
        // Published as GT_SysApiVersion on every instrument.
        //
        // MAJOR.MINOR as a float. Minor increments are additive - new properties, new
        // roles, new capability bits - and a consumer written against 1.0 keeps working
        // on 1.7 untouched. Major increments mean an existing property changed meaning,
        // which is a promise not to do lightly: consumer code is out there, it cannot be
        // fixed by us, and a silently changed unit is worse than a missing property.
        //
        // A consumer that needs a newer field should test for the field (-1 sentinel
        // means absent) rather than gate on the version number. The version is for
        // deciding "can I rely on the old contract", not for feature detection.
        //
        // 1.1 added GT_HabSealKnown. Additive by the rule above, so anything written
        // against 1.0 is unaffected.
        public const float ApiVersion = 1.1f;

        // Roles are permanent and never recycled. 1-99 core, 100-999 future expansion,
        // 1000+ reserved for third parties adopting this contract.
        // Kept as float-typed aliases because the published properties are floats.
        public const float RoleRadiation = Instruments.RoleRadiation;
        public const float RoleHabitat = Instruments.RoleHabitat;
        public const float RoleWeather = Instruments.RoleWeather;
        public const float RoleBio = Instruments.RoleBio;

        // Capability bits - what a block actually populates. Branch on THIS, not on
        // role: a script testing (capabilities & CapWeather) keeps working when new
        // instruments appear or an existing one gains a namespace.
        public const float CapEnv = Instruments.CapEnv;
        public const float CapSun = Instruments.CapSun;
        public const float CapRad = Instruments.CapRad;
        public const float CapWx = Instruments.CapWx;
        public const float CapHab = Instruments.CapHab;
        public const float CapBio = Instruments.CapBio;

        private static bool _created;

        public static void Create()
        {
            if (_created) return;
            _created = true;

            // ---- GT_Sys : metadata, present on every instrument ----
            Num("GT_SysApiVersion", (b, s) => ApiVersion);
            Num("GT_SysBlockRole", (b, s) => Role(b));
            Num("GT_SysCapabilities", (b, s) => Capabilities(b));
            Num("GT_SysAge", (b, s) => (float)s.AgeSeconds);
            Flag("GT_SysOperational", (b, s) => b.IsWorking);

            // ---- GT_Env : cheap, universally useful context ----
            Flag("GT_EnvInGravityWell", (b, s) => s.Env.InGravityWell);
            Num("GT_EnvAirDensity", (b, s) => (float)s.Env.AirDensity);
            Num("GT_EnvSolarProtection", (b, s) => s.Env.InGravityWell ? (float)s.Env.ProtectionFactor : -1f);
            Num("GT_EnvPlanetRadGain", (b, s) => s.Env.InGravityWell ? (float)s.Env.PlanetRadiationGain : -1f);

            // ---- GT_Sun ----
            Flag("GT_SunLosClear", (b, s) => s.Env.SunLosClear);
            Num("GT_SunBlockedDistance", (b, s) => (float)s.Env.SunBlockedDistance);

            // Degrees above the local horizon; negative is night. -999 in space, where
            // there is no horizon to measure against. A consumer combining this with
            // GT_WxSolarMult gets actual expected output; either alone does not.
            // Breathable oxygen 0-1, WEATHER ALREADY APPLIED. Do not multiply this by
            // GT_WxOxygenMult - measured 2026-08-10, planetOxygen / oxygenMult held
            // constant across clear, sandstorm and alien fog, so the multiplier is
            // already in here. GT_WxOxygenMult is the weather's contribution, published
            // so a consumer can attribute the change, not so it can be reapplied.
            Num("GT_EnvOxygen", (b, s) => (float)s.Env.Oxygen);
            Flag("GT_EnvBreathable", (b, s) => s.Env.Oxygen > 0.5);

            Num("GT_SunElevation", (b, s) => (float)s.Env.SunElevation);
            Flag("GT_SunUp", (b, s) => s.Env.SunElevation > 0);

            // ---- GT_Rad ----
            Flag("GT_RadEnabled", (b, s) => s.Rad.Enabled);
            Num("GT_RadIntensitySetting", (b, s) => (float)s.Rad.IntensitySetting);
            Num("GT_RadBaseRate", (b, s) => (float)s.Rad.Base);
            Num("GT_RadRate", (b, s) => (float)s.Rad.Total);
            Num("GT_RadRateSolar", (b, s) => (float)s.Rad.Solar);
            Num("GT_RadRatePlanetary", (b, s) => (float)s.Rad.Planetary);
            Num("GT_RadRateWeather", (b, s) => (float)s.Rad.Weather);
            Flag("GT_RadAccumulates", (b, s) => s.Rad.Accumulates);
            // -1 means never, at the current rate. Not infinity, not a huge number.
            Num("GT_RadTimeToCritical", (b, s) => (float)s.Rad.SecondsToCritical);
            // 0 exposed, 1 sun occluded only, 2 sealed, 3 shielded by atmosphere alone.
            // The most useful branch value we publish: solar dies to any occluder,
            // planetary only to a seal, and on a thick-atmosphere world neither is
            // needed.
            Num("GT_RadShelterState", (b, s) => s.Rad.ShelterState);
            Flag("GT_RadSunBlocked", (b, s) => !s.Env.SunLosClear);
            Flag("GT_RadAirtight", (b, s) => s.Airtight);
            // protectionFactor x airDensity clamped to 1. At 1 the air alone suffices.
            Num("GT_RadAtmosShielding", (b, s) => (float)s.Rad.AtmosphericShielding);

            // ---- GT_Hab ----
            //
            // GT_HabSealKnown exists because seal state is the ONLY reading in this mod
            // that does not originate on the machine reading it. A dedicated-server
            // client has no pressurisation data at all, so the server evaluates it and
            // pushes it; for a moment after joining, a client has simply not been told.
            //
            // Without this, "not sealed" and "not yet told" are the same false, and a
            // script driving a door cannot distinguish them. Added in API 1.1.
            Flag("GT_HabSealKnown", (b, s) => s.SealStatus != Readings.SealNoGasSystem
                                           && s.SealStatus != Readings.SealAwaitingServer);
            Flag("GT_HabAirtight", (b, s) => s.Airtight);
            Flag("GT_HabBreached", (b, s) => s.Breached);
            Num("GT_HabSealDuration", (b, s) => (float)s.SealedSeconds);

            // ---- GT_Wx ----
            Flag("GT_WxBodyHasWeather", (b, s) => s.BodyHasWeather);
            Flag("GT_WxActive", (b, s) => !string.IsNullOrEmpty(s.Weather));
            Num("GT_WxIntensity", (b, s) => s.WeatherIntensity);
            // -1 falling, 0 steady, +1 rising
            Num("GT_WxTrend", (b, s) => s.Trend);
            Num("GT_WxElapsed", (b, s) => string.IsNullOrEmpty(s.Weather) ? -1f : (float)s.WeatherElapsed);
            // Symmetry rule: once intensity turns over, remaining is very close to
            // elapsed. -1 while still building, because it is genuinely unknown then.
            Num("GT_WxTimeToClear", (b, s) => (float)s.SecondsToClear());
            Flag("GT_WxPeaked", (b, s) => s.Peaked);
            // Platform speed. Reported, never used to gate a reading - a moving sensor
            // measures apparent conditions, exactly as a real one would. A consumer that
            // cares can apply its own judgement.
            Num("GT_WxSpeed", (b, s) => s.Speed);
            // Ranges are NOT 0-1. Solar reaches 1.35 in a heat wave, temperature goes
            // to -2.0 in heavy snow, wind to 2.25 in a sandstorm.
            Num("GT_WxSolarMult", (b, s) => s.SolarMult);
            Num("GT_WxOxygenMult", (b, s) => s.OxygenMult);
            Num("GT_WxTempMult", (b, s) => s.TempMult);
            Num("GT_WxWindMult", (b, s) => s.WindMult);
            // Metres per second: MaxWindSpeed x windMultiplier, both real game values.
            Num("GT_WxWindSpeed", (b, s) => s.WindSpeed);
            Num("GT_WxMaxWindSpeed", (b, s) => s.MaxWindSpeed);

            // Declared by the planet definition, not measured. Available the moment
            // weather appears, including admin-forced weather that never ramped - where
            // GT_WxSecondsToClear must answer -1 because it has nothing to measure from.
            // A consumer wanting a number NOW uses these; one wanting THIS storm uses
            // the observed figure. 0 means the planet declares no range.
            Num("GT_WxDeclaredMinLength", (b, s) => s.DeclaredMinLength);
            Num("GT_WxDeclaredMaxLength", (b, s) => s.DeclaredMaxLength);

            // How many distinct effects this planet can produce anywhere on it.
            Num("GT_WxPlanetTypeCount", (b, s) => s.PlanetWeatherTypes);

            // Declared by the weather effect definition rather than measured. These are
            // what the effect does at FULL strength; the GT_Wx*Mult values above are what
            // it is doing right now. Both are real and they answer different questions.
            Num("GT_WxEffectSolar", (b, s) => s.Effect.Known ? s.Effect.Solar : -1f);
            Num("GT_WxEffectWind", (b, s) => s.Effect.Known ? s.Effect.Wind : -1f);
            Num("GT_WxEffectTemperature", (b, s) => s.Effect.Known ? s.Effect.Temperature : -1f);
            Num("GT_WxEffectOxygen", (b, s) => s.Effect.Known ? s.Effect.Oxygen : -1f);

            // Hazards. No measured equivalent - the survey established that weather does
            // not drive radiation through the solar multiplier, so a declared radiation
            // source is a separate mechanism entirely.
            Flag("GT_WxHazardRadiation", (b, s) => s.Effect.HasRadiation);
            Num("GT_WxHazardRadiationGain", (b, s) => s.Effect.HasRadiation ? s.Effect.RadiationGain : -1f);
            Num("GT_WxHazardMinIntensity", (b, s) => s.Effect.HasRadiation ? s.Effect.RadiationMinIntensity : -1f);
            Flag("GT_WxHazardInjury", (b, s) => s.Effect.HasHealth);
            Num("GT_WxHazardDamageMin", (b, s) => s.Effect.HasHealth ? s.Effect.DamageMin : -1f);
            Num("GT_WxHazardDamageMax", (b, s) => s.Effect.HasHealth ? s.Effect.DamageMax : -1f);
            Num("GT_WxHazardInjuryMinIntensity", (b, s) => s.Effect.HasHealth ? s.Effect.HealthMinIntensity : -1f);

            // TRUE when the storm has reached the intensity at which its hazard starts.
            // This is the one an alarm should watch - a declared hazard below its
            // threshold is not hurting anyone yet.
            Flag("GT_WxHazardActive", (b, s) =>
                (s.Effect.HasHealth && s.WeatherIntensity >= s.Effect.HealthMinIntensity) ||
                (s.Effect.HasRadiation && s.Effect.RadiationGain > 0 &&
                 s.WeatherIntensity >= s.Effect.RadiationMinIntensity));

            // Negative radiation gain is SHELTER: rain and thunderstorms declare -0.60,
            // which reduces exposure rather than adding to it.
            Flag("GT_WxRadiationShelter", (b, s) => s.Effect.HasRadiation && s.Effect.RadiationGain < 0);

            // Vanilla effects in the shipped table. NOT "every effect in the game":
            // MyWeatherEffectDefinition never materialises for mod code - the definition
            // manager hands back a plain MyDefinitionBase - so modded effects cannot be
            // read and report Known = false rather than zeros.
            Num("GT_WxEffectCount", (b, s) => WeatherCatalog.EffectCount);
            Flag("GT_WxEffectKnown", (b, s) => s.Effect.Known);

            // airDensity x windMultiplier - the environmental half of turbine output,
            // measured exactly as turbineOutput = k x airDensity x windMultiplier. The
            // per-turbine constant k is not ours to know, so this is a factor against
            // nominal rather than a wattage. Station grids only; turbines do not run on
            // ships, which is a property of the grid and not of this site.
            Num("GT_WxTurbineSiteFactor", (b, s) => (float)(s.Env.AirDensity * s.WindMult));

            // ---- GT_Bio ----
            // Live count and windowed statistic are both published. The window is
            // declared so the average is interpretable rather than mysterious.
            // GT_BioCount is organisms only. Contacts are published separately and
            // are never added in - a consumer that wants a total must ask for one.
            Num("GT_BioCount", (b, s) => s.Bio.Valid ? s.Bio.Count : -1f);
            Num("GT_BioContacts", (b, s) => s.Bio.Valid ? s.Bio.Contacts : -1f);
            Num("GT_BioCountAvg", (b, s) => s.Bio.Valid ? s.BioAvg : -1f);
            Num("GT_BioPeak", (b, s) => s.Bio.Valid ? s.BioPeak : -1f);
            Num("GT_BioWindow", (b, s) => 300f);
            Num("GT_BioScanRadius", (b, s) => (float)s.Bio.Radius);
            Num("GT_BioNearestDist", (b, s) => (float)s.Bio.NearestDist);
            Num("GT_BioNearestBearing", (b, s) => s.Bio.NearestBearing);
            // 0 none, 1 planetary north, 2 grid forward. The bearing is uninterpretable
            // without it, so it is published beside it rather than assumed.
            Num("GT_BioBearingFrame", (b, s) => s.Bio.NearestBearingFrame);
            // Always relative to grid forward, whatever the absolute frame is doing.
            // Readable without a compass mod, which the stock game does not provide.
            Num("GT_BioNearestBearingRel", (b, s) => s.Bio.NearestBearingRel);
            Num("GT_BioSpeciesCount", (b, s) => s.Bio.Species == null ? -1f : s.Bio.Species.Count);
        }

        // ------------------------------------------------------------------

        private static float Role(IMyTerminalBlock b)
        {
            return Instruments.RoleOf(b.BlockDefinition.SubtypeName);
        }

        private static float Capabilities(IMyTerminalBlock b)
        {
            return Instruments.CapabilitiesOf(b.BlockDefinition.SubtypeName);
        }

        // Properties attach to IMyUpgradeModule - the narrowest interface our blocks
        // implement, which is most of why they are UpgradeModule blocks at all.
        //
        // This was IMyFunctionalBlock, which every powered block in the game implements.
        // The result was 55 properties on every light, door and thruster, which corrupted
        // their terminal control lists - a light lost its On/Off action, in every world
        // this mod loaded into. Bisected 2026-08-09. Register narrow.
        //
        // Vanilla upgrade modules still receive these properties; that is unavoidable and
        // harmless. Every getter returns a sentinel unless the block is genuinely ours -
        // a productivity module asked for GT_RadTotal answers -1, not a plausible-looking
        // lie. Note the two Rotating Radar Dishes are real RadioAntenna blocks and so do
        // NOT carry these properties, which is correct: they are not instruments.
        private static void Num(string id, Func<IMyTerminalBlock, GroundTruthSession.BlockState, float> read)
        {
            var p = MyAPIGateway.TerminalControls.CreateProperty<float, IMyUpgradeModule>(id);
            p.Getter = b =>
            {
                var s = GroundTruthSession.StateFor(b);
                if (s == null) return -1f;
                try { return read(b, s); }
                catch { return -1f; }
            };
            p.Setter = (b, v) => { };   // read-only

            // NO Visible/Enabled predicates.
            //
            // A terminal PROPERTY is not a UI control - it has no widget to show or hide,
            // and these were only ever an attempt to scope the property to our blocks,
            // which they never did (SE registers per interface, and the predicates do not
            // filter enumeration). What they DID do was attach delegates that SE evaluates
            // while building the control and action lists of every functional block in the
            // game, and that left those lists half-built: a light lost its On/Off action.
            //
            // Scoping is handled where it actually works - the getter returns -1 for any
            // block that is not one of ours.
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(p);
        }

        private static void Flag(string id, Func<IMyTerminalBlock, GroundTruthSession.BlockState, bool> read)
        {
            var p = MyAPIGateway.TerminalControls.CreateProperty<bool, IMyUpgradeModule>(id);
            p.Getter = b =>
            {
                var s = GroundTruthSession.StateFor(b);
                if (s == null) return false;
                try { return read(b, s); }
                catch { return false; }
            };
            p.Setter = (b, v) => { };
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(p);
        }
    }
}
