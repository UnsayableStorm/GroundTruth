using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.Utils;

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

        // THE ROOT CAUSE, CONFIRMED 2026-08-18.
        //
        // Everything above this comment used to be a proactive scheme: call
        // GetControls<IMyUpgradeModule> on our own schedule (BeforeStart, then once a
        // second) to check whether the game had built the list yet, and register once
        // it looked safe. That was wrong in a way three earlier "fixes" did not reach,
        // because the CHECK ITSELF was the disease:
        //
        //   build   GT loaded, registration AND the diagnostic GetControls both off -> BROKEN
        //   build   GT loaded, registration off, but a diagnostic GetControls still
        //           running at LoadData (in ControlRepair, since deleted)            -> BROKEN
        //   build   GT loaded, EVERY call into MyAPIGateway.TerminalControls removed  -> FIXED
        //   world   GT removed entirely                                              -> FIXED
        //
        // The first two builds still called GetControls<IMyUpgradeModule> or
        // GetControls<IMyTerminalBlock> from OUR code, on OUR timer, before any player
        // had opened a terminal. A bare READ, not even AddControl, was enough to
        // reproduce the corruption. Only the build that made ZERO calls into that API
        // came back clean.
        //
        // So the fix is not a better-timed poll. It is: never call
        // MyAPIGateway.TerminalControls for anything at all until the GAME has already
        // decided to build that block's control list on its own - which is exactly
        // what CustomControlGetter tells us, since Keen only invokes it once the list
        // it hands us already exists. RegisterReactively, below, is called from that
        // hook instead of from a timer, and needs no probing of the list's contents
        // because by the time it runs the timing question is already settled.
        //
        // The cost, accepted deliberately: a Programmable Block on a grid where NOBODY
        // has ever opened ANY upgrade module's terminal - ours or vanilla, anywhere in
        // the session - sees no GT_ properties until someone does. That is a real
        // narrowing from the original design ("a PB must read a grid nobody has
        // opened"), and it is a far smaller cost than corrupting every upgrade module
        // in the game, including other mods' warp drives and shield generators, which
        // is what the proactive version actually did.
        public static void RegisterReactively()
        {
            if (_created) return;
            _created = true;
            RegisterAll();
        }

        // ---- the server half: an explicit request, not a timer ----
        //
        // CustomControlGetter is a UI hook. A dedicated server has no UI, so it never
        // fires there and the reactive path above never runs - which left the API dead
        // on every DS. Measured 2026-08-18 with tools/pb_property_probe.cs:
        //
        //   GT instruments on grid (by subtype): 5
        //   Blocks exposing GT_ properties:      0     <- API absent server-side
        //   Upgrade modules on grid:            39
        //     ...still have 'Name':             39     <- vanilla list intact
        //
        // Programmable Blocks execute server-side in multiplayer, so that output is the
        // SERVER's view: its control list is healthy and our properties are not in it.
        //
        // The obvious fix was a server-side timer - wait until the list looks built,
        // then register. That is rejected on purpose. A timer is precisely the shape
        // that broke every upgrade module in the game four builds running: we decide
        // when to touch a shared list, on our schedule rather than the game's, on
        // machines whose owner never asked us to. Being fairly confident it is harmless
        // where no terminal is rendered is not the same as knowing, and the blast radius
        // is other people's servers and other mods' blocks.
        //
        // So the server half is opt-in. Nothing here runs until a Programmable Block
        // asks for it, by writing GT_API_ENABLE into the Custom Data of any Ground Truth
        // instrument. IMyTerminalBlock.CustomDataChanged makes that free - a push, no
        // polling, no scanning - and InstrumentPower subscribes every instrument to it,
        // which SealSync already proves reaches blocks on a server where no pane has
        // ever been opened.
        //
        // The result is that Ground Truth calls into MyAPIGateway.TerminalControls in
        // exactly two situations, and both are somebody else's decision: the game built
        // a control list and handed it to us, or a script author explicitly asked for
        // the API. There is no third path, and no unattended server is ever touched.
        //
        // The cost is that PB authors have to know to ask. That is documented in
        // docs/API_SURFACE.md and in tools/pb_property_probe.cs, and it is a fair trade
        // for never again corrupting a terminal nobody consented to have us touch.
        public const string EnableToken = "GT_API_ENABLE";
        public const string ReadyToken = "GT_API_READY";

        /// <summary>True once the properties exist in this process.</summary>
        public static bool Registered { get { return _created; } }

        /// <summary>
        /// Subscribed to CustomDataChanged on every instrument by InstrumentPower.
        /// </summary>
        public static void OnInstrumentCustomDataChanged(IMyTerminalBlock block)
        {
            ConsiderRequest(block, "CustomDataChanged");
        }

        /// <summary>
        /// The handshake decision, reachable from either mechanism.
        ///
        /// TWO MECHANISMS, ON PURPOSE. CustomDataChanged was the whole design - a push
        /// event, no polling - and on a live server it did not fire for a PB-driven
        /// write. Tested 2026-08-18: the probe requested the API on run 1 and run 2 still
        /// reported zero blocks exposing properties.
        ///
        /// Rather than establish exactly which writes raise that event on a dedicated
        /// server, SealSync also polls the Custom Data of the instruments it already
        /// tracks, once a second, and ONLY while unregistered. That list exists anyway,
        /// it is a handful of blocks, and the loop stops permanently the moment anyone
        /// asks. Paying a trivial known cost beats depending on an engine behaviour that
        /// has already been observed not to happen.
        ///
        /// The `via` argument survives into the log so the next reader can see which one
        /// actually did the work, instead of inheriting the same uncertainty.
        /// </summary>
        public static void ConsiderRequest(IMyTerminalBlock block, string via)
        {
            if (block == null) return;

            string data = block.CustomData;
            if (string.IsNullOrEmpty(data)) return;
            if (data.IndexOf(EnableToken, StringComparison.OrdinalIgnoreCase) < 0) return;

            if (!_created)
            {
                _created = true;
                // Logged either way rather than gated on: the request is explicit, and
                // refusing it would leave the author with a silent failure and nothing
                // to diagnose. If the list is not built by the time a PB is running,
                // something emptied it, and that is worth logging - but it is not a
                // reason to withhold the API, and it is not by itself evidence about
                // WHICH mod did it. An earlier build of this mod logged that same
                // condition and blamed Animation Engine for it; the culprit turned out
                // to be this mod. Log the observation, name no names.
                MyLog.Default.WriteLineAndConsole(VanillaListIsBuilt()
                    ? "GT TERMINAL: API registered on request from " + block.CustomName
                      + " via " + via + " (control list already built by the game)."
                    : "GT TERMINAL: API registered on request from " + block.CustomName
                      + " via " + via + " - WARNING: the upgrade module control list has "
                      + "no 'Name' control, so something emptied it before we were "
                      + "asked. Ground Truth no longer touches that list unasked, so it "
                      + "is not us; which mod it is, this log line does not establish.");
                RegisterAll();
            }

            // Acknowledge in place, so the script can confirm the handshake and so the
            // Custom Data reads afterwards as a record of what happened rather than as a
            // magic word. Replaces only the token, leaving any panel selection
            // PanelControls wrote alongside it untouched. This re-enters through
            // CustomDataChanged once more, finds no ENABLE token, and stops.
            block.CustomData = ReplaceToken(data, EnableToken, ReadyToken);
        }

        private static string ReplaceToken(string data, string from, string to)
        {
            int i = data.IndexOf(from, StringComparison.OrdinalIgnoreCase);
            return i < 0 ? data : data.Substring(0, i) + to + data.Substring(i + from.Length);
        }

        // Name specifically, not merely a non-empty list: another mod's additions are no
        // proof that the vanilla inheritance chain has run. Diagnostic only - nothing
        // branches on the result except the wording of a log line.
        private static bool VanillaListIsBuilt()
        {
            try
            {
                List<IMyTerminalControl> list;
                MyAPIGateway.TerminalControls.GetControls<IMyUpgradeModule>(out list);
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].Id == "Name") return true;
                return false;
            }
            catch { return false; }
        }

        private static void RegisterAll()
        {
            LogControls("BEFORE our 76");

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

            LogControls("AFTER our 76");
            RememberBaseline();

        }

        // ------------------------------------------------------------------

        private static float Role(IMyTerminalBlock b)
        {
            return Instruments.RoleOf(b.BlockDefinition.SubtypeName);
        }


        // Snapshot of what IMyUpgradeModule's control list holds right now. Ids only -
        // enough to see whether Name, ShowInTerminal and ShowInToolbarConfig are there,
        // which is the entire question.
        private static void LogControls(string when)
        {
            try
            {
                List<IMyTerminalControl> list;
                MyAPIGateway.TerminalControls.GetControls<IMyUpgradeModule>(out list);

                var sb = new StringBuilder();
                sb.Append("GT TERMINAL [").Append(when).Append("] IMyUpgradeModule controls: ");
                if (list == null) { sb.Append("NULL LIST"); }
                else
                {
                    sb.Append(list.Count).Append(" -> ");
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(list[i] == null ? "?" : list[i].Id);
                        if (i >= 39) { sb.Append(", ..."); break; }
                    }
                }
                MyLog.Default.WriteLineAndConsole(sb.ToString());
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("GT TERMINAL [" + when + "] threw: " + e.Message);
            }
        }


        // What the control list held once we finished registering. Snapshot 4 compares
        // against this, so the log names WHAT WAS LOST rather than printing 104 ids and
        // leaving the diff to a human at 2am.
        private static readonly List<string> _baseline = new List<string>();
        // First THREE opens, not the first one. Our own instruments are upgrade modules
        // as well, so a one-shot would be spent on a GT block before a vanilla one was
        // ever opened - and the vanilla block is the whole question.
        private static int _openLogs;

        private static void RememberBaseline()
        {
            try
            {
                List<IMyTerminalControl> list;
                MyAPIGateway.TerminalControls.GetControls<IMyUpgradeModule>(out list);
                _baseline.Clear();
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null) _baseline.Add(list[i].Id);
            }
            catch { }
        }

        /// <summary>
        /// Snapshot 4: taken the first time a terminal actually builds an upgrade
        /// module's controls, which is the moment the reported symptom is visible and
        /// long after Create() has finished.
        ///
        /// Snapshots 1-3 all happen inside Create() and therefore cannot see a mod that
        /// registers - or removes - controls later. This one can. If the count here is
        /// lower than the baseline, something took controls away after we were done, and
        /// the missing ids name what.
        /// </summary>
        public static void LogFirstTerminalOpen(IMyTerminalBlock block)
        {
            if (_openLogs >= 3 || block == null) return;
            if (!(block is IMyUpgradeModule)) return;
            _openLogs++;

            try
            {
                List<IMyTerminalControl> list;
                MyAPIGateway.TerminalControls.GetControls<IMyUpgradeModule>(out list);

                var now = new List<string>();
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null) now.Add(list[i].Id);

                var sb = new StringBuilder();
                sb.Append("GT TERMINAL [FIRST OPEN: ").Append(block.BlockDefinition.SubtypeName)
                  .Append("] ").Append(now.Count).Append(" controls, baseline was ")
                  .Append(_baseline.Count);

                var missing = new List<string>();
                for (int i = 0; i < _baseline.Count; i++)
                    if (!now.Contains(_baseline[i])) missing.Add(_baseline[i]);

                var added = new List<string>();
                for (int i = 0; i < now.Count; i++)
                    if (!_baseline.Contains(now[i])) added.Add(now[i]);

                sb.Append(" | MISSING SINCE REGISTRATION: ");
                sb.Append(missing.Count == 0 ? "none" : string.Join(", ", missing.ToArray()));
                sb.Append(" | ADDED SINCE: ");
                sb.Append(added.Count == 0 ? "none" : string.Join(", ", added.ToArray()));

                MyLog.Default.WriteLineAndConsole(sb.ToString());

                // The vanilla controls the report named, checked by name, so the log
                // answers the actual question without anyone reading a list.
                string[] watch = { "Name", "ShowInTerminal", "ShowInToolbarConfig", "OnOff", "CustomData" };
                var absent = new List<string>();
                for (int i = 0; i < watch.Length; i++)
                    if (!now.Contains(watch[i])) absent.Add(watch[i]);

                MyLog.Default.WriteLineAndConsole("GT TERMINAL [FIRST OPEN] vanilla controls absent: "
                    + (absent.Count == 0 ? "NONE - list is intact" : string.Join(", ", absent.ToArray())));
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("GT TERMINAL [FIRST OPEN] threw: " + e.Message);
            }
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
