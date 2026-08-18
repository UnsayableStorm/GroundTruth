using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace GroundTruth
{
    // Ground Truth - Environmental Instruments
    // Threshold Dynamics, Survey and Assessment
    //
    // v0.1: detail-pane readouts only. Terminal properties, LCD apps and Event
    // Controller events come next, on top of the same computation.
    //
    // Values are recomputed on a fixed interval and served from cache. Nothing
    // recomputes on read - a Programmable Block polling every tick must not be able to
    // trigger a raycast sixty times a second.
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class GroundTruthSession : MySessionComponentBase
    {
        private const int TicksPerSecond = 60;
        private const int RecomputeIntervalTicks = TicksPerSecond;   // 1s

        // Subtype identity lives in Instruments, not here. These constants used to be
        // the source of truth and were duplicated into four switch statements.

        // A sphere query at sync distance (3-15km) is by far the most expensive thing
        // this mod does. Fauna do not move fast enough for a 1s refresh to be worth it,
        // and at 5s a 60-entry ring gives a clean 5 minute window.
        private const int BioScanIntervalSeconds = 5;
        private const int BioWindowSamples = 60;

        private int _tick;
        private bool _registered;

        /// <summary>
        /// Frames since load, for anything that needs a cadence faster than the
        /// one-second recompute - currently only the corner strip's breach blink.
        /// Deliberately a plain counter rather than a clock API: it needs no whitelist
        /// question answered and cannot drift from the frame the panel is drawn on.
        /// </summary>
        public static int Frames;

        private readonly Dictionary<long, BlockState> _state = new Dictionary<long, BlockState>();

        // Public because TerminalApi reads it. Never mutated from outside.
        public class BlockState
        {
            public Readings.Environment Env;
            public Readings.Radiation Rad;
            public bool Airtight;
            // What the pressurisation system said, kept so a panel can explain WHY it
            // is not sealed: no room at all reads -1 / 0, a real but open room reads a
            // level and a block count.
            public float RoomOxygen;
            public int RoomBlocks;
            public int SealStatus;
            public bool SealLogged;
            public bool WasAirtight;
            public bool Breached;
            public double SealedSeconds;

            public bool BodyHasWeather;
            public string Weather = "";
            public float WeatherIntensity;
            public float PrevIntensity = -1;
            public float Trend;
            public double WeatherElapsed;
            public double PeakElapsed = -1;
            public bool Peaked;

            // Did this instrument watch the storm start?
            //
            // The time-to-clear estimate is derived from the rise being symmetric with
            // the decay, so it is only meaningful if the rise was observed. Two cases
            // where it was not: an admin forces weather, which begins at FULL intensity
            // with no ramp at all, and a player arrives mid-storm.
            //
            // Without an observed onset there is no honest estimate to give. Saying so
            // is the instrument reporting what it knows; producing a number anyway
            // would be inventing one.
            public bool OnsetObserved;

            // The planet's own declared duration range for the current effect, from
            // WeatherCatalog. Available the instant weather appears - including
            // admin-forced weather that never ramped - where the observed estimate
            // needs to have watched the onset.
            public int DeclaredMinLength;
            public int DeclaredMaxLength;
            public int PlanetWeatherTypes;

            // What the active effect IS, from the global definition table - as opposed
            // to what it is doing right now, which is the live multipliers above.
            // Carries the hazard declarations, which have no measured equivalent.
            public WeatherCatalog.Effect Effect;

            // MaxWindSpeed x windMultiplier. Both terms are real game values: the
            // planet's own declared ceiling (80 on every vanilla world) and the weather
            // coefficient. Verified against station turbines on two planets - output is
            // exactly k x airDensity x windMultiplier, so density belongs to turbine
            // efficiency and the multiplier alone carries the wind.
            public float WindSpeed;
            public float MaxWindSpeed;

            public float SolarMult = 1f;
            public float OxygenMult = 1f;
            public float TempMult = 1f;
            public float WindMult = 1f;

            public Readings.Bio Bio;
            public int BioCountdown;
            // Live count and a windowed statistic are different measurements, not a raw
            // value and a prettified one. Both are published; the window is declared.
            public readonly int[] BioRing = new int[BioWindowSamples];
            public int BioRingLen;
            public int BioRingPos;

            public float BioAvg;
            public int BioPeak;

            public void PushBioSample(int count)
            {
                BioRing[BioRingPos] = count;
                BioRingPos = (BioRingPos + 1) % BioWindowSamples;
                if (BioRingLen < BioWindowSamples) BioRingLen++;

                int sum = 0, peak = 0;
                for (int i = 0; i < BioRingLen; i++)
                {
                    sum += BioRing[i];
                    if (BioRing[i] > peak) peak = BioRing[i];
                }
                BioAvg = BioRingLen > 0 ? (float)sum / BioRingLen : 0f;
                BioPeak = peak;
            }

            public double LastComputeSeconds = -1;
            public double AgeSeconds;

            // Reported as a reading, not used to gate anything. A real anemometer on a
            // moving ship reports apparent wind; it does not refuse to answer because
            // the platform is under way. Timing predictions are measured the same way
            // regardless of motion - if the operator does not know his own platform is
            // moving, that is the operator's problem, not the instrument's.
            //
            // Exposed as GT_WxSpeed so a consumer can apply its own judgement.
            public float Speed;

            // Rise and decay are symmetric - measured 33/34 and 26/26 samples across two
            // natural events, and a prediction from the decay landed within one sample
            // on a 12 minute storm. So once intensity has turned over, the time left is
            // very close to the time already elapsed. Unknowable while still building.
            public double SecondsToClear()
            {
                if (string.IsNullOrEmpty(Weather) || !Peaked || PeakElapsed <= 0) return -1;
                // No observed onset, no estimate. The symmetry rule has nothing to
                // measure against, and a plausible-looking number would be a lie.
                if (!OnsetObserved) return -1;

                double sincePeak = WeatherElapsed - PeakElapsed;
                return Math.Max(0, PeakElapsed - sincePeak);
            }
        }

        private static GroundTruthSession _instance;
        private double _seconds;

        // Blocks whose terminal was just opened. They are refreshed on the very next
        // frame rather than waiting for the one-second recompute, because otherwise the
        // first click shows an empty pane and only the second shows data.
        private readonly List<long> _pending = new List<long>();

        public override void LoadData()
        {
            _instance = this;
            _registered = true;

            // Subscribing is safe this early - it was AddControl in TerminalApi.Create
            // that corrupted control lists at LoadData, not this event hook. Create()
            // stays in BeforeStart.
            MyAPIGateway.TerminalControls.CustomControlGetter += OnCustomControlGetter;

            // As early as we get to run. Whatever the base control list holds NOW is
            // what we can hand back if another mod empties it later - see ControlRepair.
            ControlRepair.Capture();

            SealSync.Init();

            // Announce the drift rather than waiting for someone to notice a dead panel.
            var orphans = Instruments.SubtypesWithoutComponent(InstrumentPower.AttachedTo);
            for (int i = 0; i < orphans.Count; i++)
                MyLog.Default.WriteLineAndConsole(
                    "GroundTruth BUG: " + orphans[i] + " is in the Instruments table but no "
                    + "component attaches to it - no power draw, no seal sync.");
        }

        // Entry point for TerminalApi. Returns null for any block that is not one of
        // ours, so foreign blocks get sentinels rather than plausible-looking lies.
        //
        // Creates state on first access and computes once, after which the block joins
        // the one-second refresh loop. That means a script polling a block nobody has
        // ever opened still gets a real reading, and still only pays for one raycast a
        // second no matter how hard it polls.
        public static BlockState StateFor(IMyTerminalBlock block)
        {
            if (_instance == null || block == null) return null;
            if (!IsOurs(block)) return null;

            BlockState s;
            if (_instance._state.TryGetValue(block.EntityId, out s)) return s;

            s = new BlockState();
            _instance._state[block.EntityId] = s;
            _instance.Recompute(block, s);
            return s;
        }

        protected override void UnloadData()
        {
            if (!_registered) return;
            MyAPIGateway.TerminalControls.CustomControlGetter -= OnCustomControlGetter;
            SealSync.Close();
            _state.Clear();
            _instance = null;
            _registered = false;
        }

        // Terminal registration happens HERE, not in LoadData.
        //
        // LoadData runs before the terminal system has built its own control lists.
        // Registering there does not merely fail - it corrupts the control list of
        // whatever interface is targeted. On IMyFunctionalBlock that cost every powered
        // block in the game its On/Off action; narrowing to IMyOreDetector moved the
        // same damage onto our own blocks, which came up blank.
        //
        // BeforeStart runs after definitions and the terminal system are up, and before
        // the player can open anything. Nanobot Build and Repair does the equivalent
        // from its first block's Init - same timing, same reason.
        //
        // Deliberately NOT lazy-on-first-terminal-open: a Programmable Block must be
        // able to read GT_ properties on a grid whose terminal nobody has opened.
        //
        // It IS conditional on the game having built the upgrade module control list
        // first - see TerminalApi. On a client joining a server that list is empty at
        // BeforeStart, and registering into it makes us its creator, which permanently
        // costs every upgrade module in the world its vanilla controls.
        public override void BeforeStart()
        {
            base.BeforeStart();
            TerminalApi.Create();
            PanelControls.Create();
        }

        // Attaching the info writer lazily, the first time a player opens one of our
        // blocks in the terminal, avoids hooking every block in the world at load.
        private void OnCustomControlGetter(IMyTerminalBlock block, List<Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControl> controls)
        {
            // LCDs and cockpits first - they are not instruments, so this has to happen
            // before the IsOurs guard. PanelControls adds nothing to a block whose
            // surface is not running one of our apps.
            PanelControls.Inject(block, controls);

            // Diagnostic, one shot per session: the first time a terminal builds an
            // upgrade module's control list. Deliberately BEFORE the IsOurs guard - the
            // block we most need to hear about is a VANILLA upgrade module, which is
            // not ours by definition.
            TerminalApi.LogFirstTerminalOpen(block);

            if (!IsOurs(block)) return;

            block.AppendingCustomInfo -= WriteInfo;
            block.AppendingCustomInfo += WriteInfo;

            // Seed the state, and queue an immediate refresh for the next frame.
            //
            // NOT RefreshCustomInfo() - that runs while SE is building the control list
            // and is re-entrant. But without something here the pane stayed empty: the
            // update loop only walks blocks already in _state, and the only thing that
            // ever added them was WriteInfo, which is what we are trying to trigger.
            // StateFor breaks the circle without touching the terminal.
            StateFor(block);
            if (!_pending.Contains(block.EntityId)) _pending.Add(block.EntityId);
        }

        // Public form for TerminalApi's Visible/Enabled predicates.
        public static bool IsInstrument(IMyTerminalBlock block)
        {
            return IsOurs(block);
        }

        private static bool IsOurs(IMyTerminalBlock block)
        {
            if (block == null || block.SlimBlock == null) return false;
            return Instruments.Is(block);
        }

        public override void UpdateAfterSimulation()
        {
            Frames++;

            // Freshly opened panes first, on the next frame. Not inside the control
            // getter itself - refreshing while SE builds the control list is re-entrant.
            if (_pending.Count > 0)
            {
                for (int i = 0; i < _pending.Count; i++)
                {
                    BlockState s;
                    if (!_state.TryGetValue(_pending[i], out s)) continue;

                    var b = MyAPIGateway.Entities.GetEntityById(_pending[i]) as IMyTerminalBlock;
                    if (b == null || b.Closed) continue;

                    Recompute(b, s);
                    b.RefreshCustomInfo();
                }
                _pending.Clear();
            }

            if (++_tick < RecomputeIntervalTicks) return;
            _tick = 0;
            _seconds += 1.0;

            // Custom Event Controller events, driven on the same one-second cadence as
            // the readings they watch. An event component gets no update callback of its
            // own - the stock ones hook block events, and a weather reading has none.
            // Registration may have been deferred at BeforeStart because the game had
            // not built the upgrade module control list yet - normal on a client that
            // joins before any grid streams in. Retry until it has.
            //
            // Repair runs first and once: if the list is missing its vanilla controls
            // because another mod emptied the shared one, put them back before we add
            // ours on top. Doing it in this order means a repaired list then satisfies
            // the registration gate normally.
            ControlRepair.Repair();
            TerminalApi.TryCreateDeferred();

            TickEvents();

            // Seal state is the one reading only the server can take, and it must run
            // whether or not anybody has opened a panel - a breach alarm cannot wait for
            // someone to look at a screen. Sends only what changed.
            SealSync.ServerTick();

            // Nothing to do until a player has actually opened one of our blocks.
            if (_state.Count == 0) return;

            var stale = new List<long>();
            foreach (var pair in _state)
            {
                var block = MyAPIGateway.Entities.GetEntityById(pair.Key) as IMyTerminalBlock;
                if (block == null || block.Closed) { stale.Add(pair.Key); continue; }
                Recompute(block, pair.Value);
                block.RefreshCustomInfo();
            }
            foreach (var id in stale) _state.Remove(id);
        }

        private static void TickEvents()
        {
            for (int i = GTEventWeatherIntensity.Live.Count - 1; i >= 0; i--)
            {
                try { GTEventWeatherIntensity.Live[i].Tick(); }
                catch (Exception e) { MyLog.Default.WriteLineAndConsole("GT event tick: " + e); }
            }

            for (int i = GTEventWeatherType.Live.Count - 1; i >= 0; i--)
            {
                try { GTEventWeatherType.Live[i].Tick(); }
                catch (Exception e) { MyLog.Default.WriteLineAndConsole("GT event tick: " + e); }
            }

            // Each family shares one list and one loop.
            for (int i = GTBooleanEvent.Live.Count - 1; i >= 0; i--)
            {
                try { GTBooleanEvent.Live[i].Tick(); }
                catch (Exception e) { MyLog.Default.WriteLineAndConsole("GT event tick: " + e); }
            }

            for (int i = GTThresholdEvent.Live.Count - 1; i >= 0; i--)
            {
                try { GTThresholdEvent.Live[i].Tick(); }
                catch (Exception e) { MyLog.Default.WriteLineAndConsole("GT event tick: " + e); }
            }
        }

        private void Recompute(IMyTerminalBlock block, BlockState s)
        {
            s.AgeSeconds = s.LastComputeSeconds < 0 ? 0 : _seconds - s.LastComputeSeconds;
            s.LastComputeSeconds = _seconds;

            s.Env = Readings.ReadEnvironment(block);
            Readings.ReadSeal(block, out s.Airtight, out s.RoomOxygen, out s.RoomBlocks, out s.SealStatus);

            // WHOSE VIEW OF THE ROOMS IS THIS?
            //
            // A panel renders on the client, and pressurisation is simulated on the
            // server. If the room graph is server-side only, a dedicated-server client
            // sees no rooms and reports NO ROOM HERE on a pressurised ship - which is
            // exactly what Long Haul reported, while single player was always fine
            // because there the client IS the server.
            //
            // So log the answer once per block per side. Comparing the server log with
            // the panel settles it without another walk to a sealed room.
            if (!s.SealLogged && Instruments.RoleOf(block.BlockDefinition.SubtypeName) == Instruments.RoleHabitat)
            {
                s.SealLogged = true;
                bool isServer = MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer;
                MyLog.Default.WriteLineAndConsole(string.Format(
                    "GT SEAL [{0}] {1}: status={2} airtight={3} room={4} cells o2={5:F2}",
                    isServer ? "SERVER" : "CLIENT", block.CustomName,
                    s.SealStatus, s.Airtight, s.RoomBlocks, s.RoomOxygen));
            }
            s.Rad = Readings.ReadRadiation(block, s.Env, s.Airtight);

            // Breach latches once lost, and clears when the seal is restored.
            if (s.WasAirtight && !s.Airtight) s.Breached = true;
            if (s.Airtight) { s.Breached = false; s.SealedSeconds += 1.0; }
            else s.SealedSeconds = 0;
            s.WasAirtight = s.Airtight;

            try
            {
                var phys = block.CubeGrid.Physics;
                s.Speed = phys == null ? 0f : (float)phys.LinearVelocity.Length();
            }
            catch { s.Speed = 0f; }

            // Only the Bio Scanner pays for the sphere query, and only every 5 seconds.
            if (Instruments.RoleOf(block.BlockDefinition.SubtypeName) == Instruments.RoleBio)
            {
                if (--s.BioCountdown <= 0)
                {
                    s.BioCountdown = BioScanIntervalSeconds;
                    // A swallowed exception here would look identical to "no life
                    // detected", which is the wrong thing for an instrument to imply.
                    try { s.Bio = Readings.ScanBio(block); }
                    catch (Exception ex) { s.Bio.Valid = true; s.Bio.Error = ex.GetType().Name + ": " + ex.Message; }
                    s.PushBioSample(s.Bio.Count);
                }
            }

            s.PlanetWeatherTypes = WeatherCatalog.TypeCount(s.Env.Planet);

            var wx = MyAPIGateway.Session.WeatherEffects;
            if (wx != null)
            {
                var pos = block.GetPosition();
                string name = null;
                try { name = wx.GetWeather(pos); } catch { }
                float intensity = 0f;
                try { intensity = wx.GetWeatherIntensity(pos); } catch { }

                // Airless bodies have no weather system at all - Moon and Europa carry
                // no weather definitions. Saying so beats reporting Clear forever.
                s.BodyHasWeather = s.Env.InGravityWell;

                try
                {
                    s.MaxWindSpeed = (float)s.Env.MaxWindSpeed;
                    s.SolarMult = wx.GetSolarMultiplier(pos);
                    s.OxygenMult = wx.GetOxygenMultiplier(pos);
                    s.TempMult = wx.GetTemperatureMultiplier(pos);
                    s.WindMult = wx.GetWindMultiplier(pos);
                    s.WindSpeed = s.MaxWindSpeed * s.WindMult;
                }
                catch { }

                bool active = !string.IsNullOrEmpty(name) && name != "Clear";
                if (!active)
                {
                    s.DeclaredMinLength = 0; s.DeclaredMaxLength = 0;
                    s.Effect = default(WeatherCatalog.Effect);
                    s.Weather = ""; s.WeatherIntensity = 0; s.WeatherElapsed = 0;
                    s.PeakElapsed = -1; s.Peaked = false; s.PrevIntensity = -1;
                    s.Trend = 0;
                }
                else
                {
                    if (s.Weather != name)
                    {
                        s.WeatherElapsed = 0;
                        s.PeakElapsed = -1;
                        s.Peaked = false;

                        // Caught it below a tenth strength: we are watching it build.
                        // Anything higher on first sight means we missed the start.
                        s.OnsetObserved = intensity <= 0.1f;
                    }
                    s.Weather = name;
                    s.WeatherElapsed += 1.0;

                    s.Effect = WeatherCatalog.ForName(name);

                    if (!WeatherCatalog.Duration(s.Env.Planet, name,
                            out s.DeclaredMinLength, out s.DeclaredMaxLength))
                    {
                        s.DeclaredMinLength = 0;
                        s.DeclaredMaxLength = 0;
                    }

                    // Rise and decay are symmetric - measured 33/34 and 26/26 samples on
                    // two natural events. So once intensity turns over, the remaining
                    // time is very close to the time already elapsed.
                    // At full strength the event IS at its plateau, whether we watched
                    // it climb or it was forced there instantly. Waiting for a fall
                    // before admitting the peak left a maxed-out sandstorm reporting
                    // "building".
                    if (!s.Peaked && intensity >= 0.995f)
                    {
                        s.Peaked = true;
                        s.PeakElapsed = s.WeatherElapsed;
                    }

                    if (s.PrevIntensity >= 0 && intensity < s.PrevIntensity - 0.002f && !s.Peaked)
                    {
                        s.Peaked = true;
                        s.PeakElapsed = s.WeatherElapsed;
                    }
                    if (s.PrevIntensity < 0) s.Trend = 1f;
                    else if (intensity > s.PrevIntensity + 0.002f) s.Trend = 1f;
                    else if (intensity < s.PrevIntensity - 0.002f) s.Trend = -1f;
                    else s.Trend = 0f;

                    s.PrevIntensity = intensity;
                    s.WeatherIntensity = intensity;
                }
            }
        }

        private void WriteInfo(IMyTerminalBlock block, StringBuilder sb)
        {
            // An exception in here is swallowed by SE and renders as an EMPTY pane -
            // indistinguishable from "no data", which is the one thing this mod must
            // never show when something is actually wrong.
            try { WriteInfoInner(block, sb); }
            catch (Exception e) { sb.AppendLine("Ground Truth error:").AppendLine(e.Message); }
        }

        private void WriteInfoInner(IMyTerminalBlock block, StringBuilder sb)
        {
            BlockState s;
            if (!_state.TryGetValue(block.EntityId, out s))
            {
                s = new BlockState();
                _state[block.EntityId] = s;
                Recompute(block, s);
            }

            // NOT sb.Clear(). This was harmless when the blocks were bare
            // FunctionalBlocks that produced no info of their own. As OreDetectors they
            // generate their own custom info, and clearing the shared builder in the
            // middle of that sequence loses our text.
            switch (Instruments.RoleOf(block.BlockDefinition.SubtypeName))
            {
                case Instruments.RoleRadiation: WriteRadiation(sb, s); break;
                case Instruments.RoleHabitat: WriteHabitat(sb, s); break;
                case Instruments.RoleWeather: WriteWeather(sb, s); break;
                case Instruments.RoleBio: WriteBio(sb, s); break;
            }
        }

        private static void WriteRadiation(StringBuilder sb, BlockState s)
        {
            if (!s.Rad.Enabled || s.Rad.IntensitySetting <= 0)
            {
                sb.AppendLine("RADIATION DISABLED");
                sb.AppendLine("This world has solar radiation switched off.");
                return;
            }

            sb.AppendLine(string.Format("Exposure      {0:F4} /s", s.Rad.Total));
            sb.AppendLine(string.Format("  solar       {0:F4}", s.Rad.Solar));
            sb.AppendLine(string.Format("  planetary   {0:F4}", s.Rad.Planetary));
            sb.AppendLine();

            if (!s.Rad.Accumulates)
            {
                sb.AppendLine("NOT ACCUMULATING");
                sb.AppendLine("Below the registering threshold.");
                sb.AppendLine("Safe indefinitely at this position.");
            }
            else
            {
                var t = TimeSpan.FromSeconds(s.Rad.SecondsToCritical);
                sb.AppendLine("ACCUMULATING");
                sb.AppendLine(string.Format("Critical in   {0:hh\\:mm\\:ss} unprotected", t));
            }

            sb.AppendLine();
            switch (s.Rad.ShelterState)
            {
                case 3:
                    sb.AppendLine("Shelter       ATMOSPHERE");
                    sb.AppendLine("Air alone blocks solar here.");
                    break;
                case 2:
                    sb.AppendLine("Shelter       SEALED");
                    sb.AppendLine("Airtight volume. Full protection.");
                    break;
                case 1:
                    sb.AppendLine("Shelter       SUN OCCLUDED");
                    sb.AppendLine(string.Format("Blocked by {0}{1}.", s.Env.SunBlockedBy,
                        s.Env.SunBlockedDistance >= 0 ? string.Format(" at {0:F1}m", s.Env.SunBlockedDistance) : ""));
                    sb.AppendLine("Solar blocked.");
                    sb.AppendLine("Planetary sources are NOT.");
                    break;
                default:
                    sb.AppendLine("Shelter       EXPOSED");
                    sb.AppendLine("Direct line of sight to the sun.");
                    break;
            }

            sb.AppendLine();
            sb.AppendLine(string.Format("Air density   {0:F3}", s.Env.AirDensity));
            sb.AppendLine(s.Rad.AtmosphericShielding >= 1.0
                ? "Shielding     TOTAL"
                : string.Format("Shielding     {0:P0}", s.Rad.AtmosphericShielding));
            sb.AppendLine(string.Format("World setting {0:F2}", s.Rad.IntensitySetting));

            WriteEnvironment(sb, s);
        }

        private static void WriteHabitat(StringBuilder sb, BlockState s)
        {
            if (s.Airtight)
            {
                sb.AppendLine("Seal          INTACT");
                sb.AppendLine(string.Format("Held for      {0:hh\\:mm\\:ss}", TimeSpan.FromSeconds(s.SealedSeconds)));
                sb.AppendLine();
                sb.AppendLine("Sealed volumes block");
                sb.AppendLine("planetary radiation.");
            }
            else
            {
                sb.AppendLine(s.Breached ? "*** SEAL BREACHED ***" : "Seal          NONE");
                sb.AppendLine();
                if (s.Breached)
                {
                    sb.AppendLine("This volume was sealed");
                    sb.AppendLine("and is no longer.");
                }
                else
                {
                    sb.AppendLine("Not inside an airtight volume.");
                }
                sb.AppendLine("No protection from");
                sb.AppendLine("planetary radiation.");
            }

            WriteEnvironment(sb, s);
        }

        private static string BearingFrameName(int frame)
        {
            switch (frame)
            {
                case Readings.FramePlanetNorth: return "(true north)";
                case Readings.FrameGridForward: return "(rel. grid fwd)";
                default: return "";
            }
        }

        private static void WriteBio(StringBuilder sb, BlockState s)
        {
            if (!s.Bio.Valid)
            {
                sb.AppendLine("Scanning...");
                return;
            }

            sb.AppendLine(string.Format("Biosignatures {0}", s.Bio.Count));
            if (s.Bio.Contacts > 0)
                sb.AppendLine(string.Format("Non-biological contacts {0}", s.Bio.Contacts));
            sb.AppendLine(string.Format("  {0:F1} avg / {1} peak over 5m", s.BioAvg, s.BioPeak));
            sb.AppendLine(string.Format("Range         {0:F0} m", s.Bio.Radius));
            if (!string.IsNullOrEmpty(s.Bio.Error))
                sb.AppendLine("  ERROR " + s.Bio.Error);
            sb.AppendLine();

            if (s.Bio.Count == 0 && s.Bio.Contacts == 0)
            {
                sb.AppendLine("No organisms detected.");
                return;
            }

            sb.AppendLine(string.Format("Nearest       {0}", s.Bio.Nearest));
            sb.AppendLine(string.Format("              {0:F0} m  bearing {1:F0} relative",
                s.Bio.NearestDist, s.Bio.NearestBearingRel));
            if (s.Bio.NearestBearingFrame == Readings.FramePlanetNorth)
                sb.AppendLine(string.Format("                       {0:F0} true north", s.Bio.NearestBearing));
            sb.AppendLine();

            foreach (var kv in s.Bio.Species)
                sb.AppendLine(string.Format("  {0,-24} {1}", kv.Key, kv.Value));

            WriteEnvironment(sb, s);
        }

        private static void WriteWeather(StringBuilder sb, BlockState s)
        {
            if (MyAPIGateway.Session.WeatherEffects == null || !s.Env.InGravityWell)
            {
                sb.AppendLine("NO WEATHER SYSTEM");
                sb.AppendLine("No atmosphere at this position.");
                WriteEnvironment(sb, s);
                return;
            }

            bool active = !string.IsNullOrEmpty(s.Weather);

            sb.AppendLine(string.Format("Conditions      {0}", active ? s.Weather : "CLEAR"));

            if (active)
            {
                sb.AppendLine(string.Format("Intensity       {0:P0}   {1}", s.WeatherIntensity,
                    s.Peaked ? "falling" : "rising"));

                // One source of truth - the pane and GT_WxSecondsToClear must never disagree.
                double remaining = s.SecondsToClear();
                sb.AppendLine(remaining >= 0
                    ? string.Format("Clears in       ~{0:mm\\:ss}", TimeSpan.FromSeconds(remaining))
                    : (s.OnsetObserved
                        ? "Clears in       BUILDING - no estimate until peak"
                        : "Clears in       unknown - onset not observed"));
            }
            else
            {
                // A clear reading is still a reading. The effect figures below are what
                // the site is producing RIGHT NOW, which is exactly what a player sizing
                // a solar array or a turbine wants, and it used to be hidden behind the
                // single word CLEAR.
                sb.AppendLine("Intensity       none");
            }

            if (active && (s.Effect.HasRadiation || s.Effect.HasHealth))
            {
                sb.AppendLine();
                sb.AppendLine("HAZARD");

                // Declared by the effect, not measured. Both hazards apply only above a
                // stated intensity, so the threshold is shown rather than left for the
                // player to discover by being hurt.
                //
                // "ACTIVE" is the important word on this panel: it means the storm has
                // reached the intensity at which this hazard starts, right now.

                if (s.Effect.HasHealth)
                {
                    bool live = s.WeatherIntensity >= s.Effect.HealthMinIntensity;
                    sb.AppendLine(string.Format("  INJURY        {0:F0}-{1:F0} damage{2}",
                        s.Effect.DamageMin, s.Effect.DamageMax, live ? "   ACTIVE" : ""));
                    sb.AppendLine(string.Format("                {0} {1:P0} intensity",
                        live ? "above" : "starts at", s.Effect.HealthMinIntensity));
                }

                if (s.Effect.HasRadiation)
                {
                    bool live = s.WeatherIntensity >= s.Effect.RadiationMinIntensity;

                    // A negative gain is SHELTER, not a hazard - rain and thunderstorms
                    // declare -0.60, which reduces exposure. Calling that a hazard would
                    // be exactly backwards, and it is the kind of sign error a player
                    // would never think to question.
                    if (s.Effect.RadiationGain < 0)
                        sb.AppendLine(string.Format("  RADIATION     {0:F2}/s SHELTER{1}",
                            s.Effect.RadiationGain, live ? "   ACTIVE" : ""));
                    else
                        sb.AppendLine(string.Format("  RADIATION     +{0:F2}/s{1}",
                            s.Effect.RadiationGain, live ? "   ACTIVE" : ""));

                    sb.AppendLine(string.Format("                {0} {1:P0} intensity",
                        live ? "above" : "starts at", s.Effect.RadiationMinIntensity));
                }
            }

            sb.AppendLine();
            sb.AppendLine("EFFECT ON SYSTEMS");

            // Consequences rather than coefficients - a player cares that the panels are
            // down to a quarter, not that the multiplier is 0.255.
            //
            // But this is what the WEATHER is doing to sunlight, not what the panels are
            // producing. At night it reads 100% and the panels make nothing, which is
            // true of the weather and a lie about the grid. The sun state below is what
            // reconciles them; the label says which quantity this is.
            sb.AppendLine(string.Format("  Solar (wx)    {0:P0}", s.SolarMult));
            sb.AppendLine(string.Format("  Wind (wx)     {0:P0}", s.WindMult));

            // Turbine output is k x airDensity x windMultiplier - measured exactly
            // against station turbines on two planets, k being per-turbine and per
            // altitude. We cannot know k, but density x multiplier IS knowable and is
            // the whole environmental part. Published as a factor against nominal, not
            // as a wattage we are in no position to claim.
            //
            // Turbines also only function on STATION grids, so this figure describes the
            // site rather than promising a ship anything.
            sb.AppendLine(string.Format("  Turbine site  {0:P0} of nominal",
                s.Env.AirDensity * s.WindMult));
            // The weather's CONTRIBUTION to breathable oxygen, not the level itself.
            // The level is in the SITE footer and already has this multiplier applied -
            // measured, not assumed. Multiplying them would double count.
            sb.AppendLine(string.Format("  Oxygen (wx)   {0:P0}{1}", s.OxygenMult,
                s.OxygenMult < 0.99f ? "   REDUCED" : ""));
            // A multiplier with genuinely strange semantics: negative for snow, 8.00 for
            // ElectricStorm. The survey established that GetOutsideTemperature does not
            // follow it by any simple relation - a higher multiplier produced a LOWER
            // temperature - so this is reported as the coefficient it is, and never
            // converted into degrees we would be inventing.
            sb.AppendLine(string.Format("  Temp (wx)     {0:F2}x", s.TempMult));

            sb.AppendLine();
            sb.AppendLine("WIND");
            if (s.MaxWindSpeed > 0)
            {
                sb.AppendLine(string.Format("  Speed         {0:F0} m/s", s.WindSpeed));
                sb.AppendLine(string.Format("  Planet max    {0:F0} m/s", s.MaxWindSpeed));
            }
            else
            {
                sb.AppendLine(string.Format("  Multiplier    {0:P0}", s.WindMult));
                sb.AppendLine("  Planet max    not published");
            }

            WriteEnvironment(sb, s);
        }

        // Shared footer. The same three lines on every instrument, so a player who has
        // learned to read one pane can read all of them, and so a reading is never
        // presented without the conditions it was taken in.
        private static void WriteEnvironment(StringBuilder sb, BlockState s)
        {
            sb.AppendLine();
            sb.AppendLine("SITE");
            sb.AppendLine(string.Format("  Body          {0}",
                s.Env.Planet != null ? s.Env.Planet.Generator.Id.SubtypeName : "none / space"));
            sb.AppendLine(string.Format("  Air density   {0:P0}", s.Env.AirDensity));

            if (s.Env.Oxygen >= 0)
            {
                // The number that decides whether you can breathe here. Weather is
                // already applied. Air density is separate and does not move with it.
                string note = s.Env.Oxygen <= 0.001 ? "  SUIT REQUIRED"
                            : (s.Env.Oxygen < 0.5 ? "  thin" : "");
                sb.AppendLine(string.Format("  Oxygen        {0:P0}{1}", s.Env.Oxygen, note));
            }

            // Night is the reason a 100% solar figure can sit beside dead panels.
            if (s.Env.SunElevation > -900)
                sb.AppendLine(string.Format("  Sun           {0:F0} deg  {1}",
                    s.Env.SunElevation,
                    s.Env.SunElevation < 0 ? "BELOW HORIZON" :
                    (s.Env.SunLosClear ? "up, unobstructed" : "up, obstructed")));
            sb.AppendLine(string.Format("  Reading age   {0:F0}s", s.AgeSeconds));
        }

    }
}
