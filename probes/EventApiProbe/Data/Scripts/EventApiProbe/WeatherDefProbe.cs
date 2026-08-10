using System.Collections.Generic;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using VRageMath;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;

namespace EventApiProbe
{
    // Compile-time whitelist probe for the GLOBAL weather effect table.
    //
    // WeatherCatalog reads what a PLANET declares, which is correct for "what happens
    // here naturally" but is not the set of effects a player can encounter - the admin
    // panel forced ElectricStorm onto an EarthLike that never declares it.
    //
    // MyWeatherEffectDefinition is the complete list, and it carries the modifiers the
    // weather survey measured by hand:
    //
    //   SolarOutputModifier, WindOutputModifier, TemperatureModifier,
    //   OxygenLevelModifier, RadiationHazard
    //
    // If mods can read it, the whole table becomes runtime data - correct for modded
    // effects we have never seen, and no longer dependent on somebody forcing each
    // effect in turn and writing the numbers down.
    //
    // The question is MyDefinitionManager, which has never been probed.
    //
    // Everything is behind a const-false guard: nothing executes, the whitelist check
    // happens at compile time regardless. Zero gameplay risk.
    //
    // Read the log for "MOD_ERROR: EventApiProbe".
    //   no error  -> the table is readable; build it into WeatherCatalog
    //   blocked   -> stay with per-planet declarations and the measured table
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class WeatherDefProbeSession : MySessionComponentBase
    {
        private const bool NeverRun = false;
        private int _tick;

        public override void LoadData()
        {
            base.LoadData();
            if (NeverRun)
            {
                ProbeDefinitionManager();
                ProbeEffectDefinition();
            }
        }

        // RUNTIME probe, added 2026-08-10. The compile-time answer was "allowed", but
        // Ground Truth still gets nothing back for Hailstorm - which declares 40 damage.
        // So the question is no longer whether the API is reachable but whether either
        // lookup path actually returns a weather effect. This tries BOTH and says which.
        public override void UpdateAfterSimulation()
        {
            if (++_tick < 300) return;
            _tick = 0;

            try { RuntimeCheck(); }
            catch (System.Exception e)
            {
                MyLog.Default.WriteLineAndConsole("WeatherDefProbe FAILED: " + e);
            }
        }

        private void RuntimeCheck()
        {
            var player = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.Player;
            if (player == null || player.Character == null) return;
            Vector3D pos = player.Character.GetPosition();

            var sb = new StringBuilder("WeatherDefProbe: ");

            // 1. how many weather effects does enumeration find?
            int enumerated = 0;
            try
            {
                foreach (var d in MyDefinitionManager.Static.GetAllDefinitions())
                    if (d is MyWeatherEffectDefinition) enumerated++;
            }
            catch (System.Exception e) { sb.Append("ENUM THREW ").Append(e.GetType().Name).Append("  "); }
            sb.Append("enumerated=").Append(enumerated).Append("  ");

            // 2. what is the active weather called?
            string name = "";
            var wx = MyAPIGateway.Session.WeatherEffects;
            if (wx != null) { try { name = wx.GetWeather(pos) ?? ""; } catch { } }
            sb.Append("weather='").Append(name).Append("'  ");

            // 3. by-id lookup, both for the live weather and for a known-good name
            sb.Append("byId(").Append(string.IsNullOrEmpty(name) ? "Hailstorm" : name).Append(")=");
            sb.Append(Describe(string.IsNullOrEmpty(name) ? "Hailstorm" : name));

            MyLog.Default.WriteLineAndConsole(sb.ToString());
        }

        private static string Describe(string subtype)
        {
            try
            {
                var id = new MyDefinitionId(typeof(MyObjectBuilder_WeatherEffectDefinition), subtype);
                var def = MyDefinitionManager.Static.GetDefinition(id);
                if (def == null) return "NULL";

                var wd = def as MyWeatherEffectDefinition;
                if (wd == null) return "found but wrong type: " + def.GetType().Name;

                return string.Format("OK solar={0:F2} dmgMax={1:F0} radGain={2:F2}",
                    wd.SolarOutputModifier,
                    wd.HealthHazard == null ? -1f : wd.HealthHazard.DamageAmountMax,
                    wd.RadiationHazard == null ? 0f : wd.RadiationHazard.RadiationGain);
            }
            catch (System.Exception e) { return "THREW " + e.GetType().Name; }
        }

        // 1. Can a mod reach the definition manager at all, and enumerate definitions?
        private void ProbeDefinitionManager()
        {
            var mgr = MyDefinitionManager.Static;
            var all = mgr.GetAllDefinitions();
            foreach (var d in all)
            {
                var id = d.Id;
                var ctx = d.Context;
            }
        }

        // 2. The effect definition itself, and every field the survey measured.
        private void ProbeEffectDefinition()
        {
            MyWeatherEffectDefinition def = null;

            float solar = def.SolarOutputModifier;
            float wind = def.WindOutputModifier;
            float temp = def.TemperatureModifier;
            float oxy = def.OxygenLevelModifier;

            var id = def.Id;
            var name = def.Id.SubtypeName;

            // Radiation hazard is the one field with no measured equivalent - the survey
            // established that weather does NOT drive radiation via the solar multiplier,
            // so a declared hazard source would be new information.
            var hazard = def.RadiationHazard;

            // And the lookup path a real implementation would use.
            var byId = MyDefinitionManager.Static.GetDefinition(
                new MyDefinitionId(typeof(MyObjectBuilder_WeatherEffectDefinition), "ElectricStorm"));

            var list = new List<MyWeatherEffectDefinition>();
            list.Add(def);
        }
    }
}
