using System.Text;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;
using VRageMath;

namespace EventApiProbe
{
    // Two questions about oxygen, and they need different kinds of answer.
    //
    // 1. WHITELIST - can a mod call MyPlanet.GetOxygenForPosition and the related
    //    atmosphere reads? Compile-time, answered by this file existing.
    //
    // 2. BEHAVIOUR - does that value already include the weather's oxygen multiplier?
    //    Cannot be answered by compiling. If it does, a panel showing planet oxygen AND
    //    the weather multiplier double-counts the sandstorm. So this probe also RUNS,
    //    printing both numbers, and the comparison settles it.
    //
    // Question 2 is why this probe is not guarded like the others. It reads only, writes
    // nothing, and runs once every 5 seconds while a player is in a cockpit or on foot.
    //
    // USAGE: load, stand on a planet, note the numbers. Then force SandStormHeavy, which
    // is the only vanilla effect that moves oxygen (to 0.25), and compare.
    //
    //   planetOxygen unchanged by the storm  -> independent, show both, no double count
    //   planetOxygen drops with the storm    -> already includes it, show ONE
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class OxygenProbeSession : MySessionComponentBase
    {
        private int _tick;

        public override void UpdateAfterSimulation()
        {
            if (++_tick < 300) return;   // every 5 seconds
            _tick = 0;

            try { Sample(); }
            catch (System.Exception e)
            {
                MyLog.Default.WriteLineAndConsole("OxygenProbe FAILED: " + e);
            }
        }

        private void Sample()
        {
            var player = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.Player;
            if (player == null || player.Character == null) return;

            Vector3D pos = player.Character.GetPosition();

            var planet = MyGamePruningStructure.GetClosestPlanet(pos);
            if (planet == null) return;

            var sb = new StringBuilder("OxygenProbe: ");

            // --- the value in question ---
            float planetOxygen = -1f;
            try { planetOxygen = planet.GetOxygenForPosition(pos); }
            catch { sb.Append("GetOxygenForPosition THREW  "); }
            sb.Append("planetOxygen=").Append(planetOxygen.ToString("F4")).Append("  ");

            // --- what we already publish, for comparison ---
            float airDensity = -1f;
            try { airDensity = planet.GetAirDensity(pos); }
            catch { }
            sb.Append("airDensity=").Append(airDensity.ToString("F4")).Append("  ");

            // --- the weather half ---
            string weather = "";
            float oxygenMult = -1f, intensity = -1f;
            var wx = MyAPIGateway.Session.WeatherEffects;
            if (wx != null)
            {
                try { weather = wx.GetWeather(pos) ?? ""; } catch { }
                try { oxygenMult = wx.GetOxygenMultiplier(pos); } catch { }
                try { intensity = wx.GetWeatherIntensity(pos); } catch { }
            }
            sb.Append("weather=").Append(string.IsNullOrEmpty(weather) ? "clear" : weather);
            sb.Append("  oxyMult=").Append(oxygenMult.ToString("F4"));
            sb.Append("  intensity=").Append(intensity.ToString("F2")).Append("  ");

            // --- the arithmetic that decides it ---
            // If planetOxygen already includes the weather, planetOxygen will fall when
            // oxyMult falls, and planetOxygen / oxyMult will hold steady at the clear
            // value. If it is independent, planetOxygen holds steady and the ratio moves.
            if (oxygenMult > 0.001f && planetOxygen >= 0f)
                sb.Append("oxygen/mult=").Append((planetOxygen / oxygenMult).ToString("F4"));

            // --- does the character agree? this is what actually keeps you alive ---
            try
            {
                var oxyComp = player.Character.Components.Get<MyCharacterOxygenComponent>();
                if (oxyComp != null)
                {
                    sb.Append("  charEnv=").Append(oxyComp.EnvironmentOxygenLevel.ToString("F4"));
                    sb.Append("  charAtLoc=").Append(
                        oxyComp.OxygenLevelAtCharacterLocation.ToString("F4"));
                }
            }
            catch { sb.Append("  charOxygen unavailable"); }

            MyLog.Default.WriteLineAndConsole(sb.ToString());
        }
    }
}
