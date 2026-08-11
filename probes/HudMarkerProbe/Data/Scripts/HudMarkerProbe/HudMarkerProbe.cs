using System.Collections.Generic;
using System.Text;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

namespace HudMarkerProbe
{
    // Antennas draw a HUD marker for their owner with every terminal box unchecked.
    // Ground Truth's instruments are RadioAntenna blocks, so a ship with twelve of them
    // gets twelve markers.
    //
    // WHAT THE FIRST PASS ESTABLISHED, 2026-08-10
    //
    //   - Confirmed on a VANILLA antenna with all boxes off. Stock behaviour, not ours.
    //   - Sandbox.Game.Entities.Cube.MyRadioBroadcaster is PROHIBITED to mods, and its
    //     ShowOnHud is get-only anyway, so there is no flag to flip even with access.
    //   - MyDataBroadcaster, the base component, IS reachable - it raised no whitelist
    //     error. Its ShowOnHud is also get-only and virtual.
    //   - Powering a block off removes its marker, across the board. So ShowOnHud is
    //     computed from the block working, not from any display setting. Nothing the
    //     player or the definition can set is an input to it.
    //   - MyObjectBuilder_RadioAntennaDefinition carries no HUD field.
    //
    // That leaves exactly one lever: if the marker comes from the broadcaster component
    // existing, take the component off the entity.
    //
    // THIS PASS
    //
    //   1. COMPILE TIME - is MyEntityComponentContainer.Remove<MyDataBroadcaster>()
    //      allowed? If this mod fails with MOD_ERROR, the answer is no and the marker
    //      cannot be suppressed from mod code at all. That is a real result: it means
    //      the choice is live with the markers or change the base block type.
    //
    //   2. RUN TIME - if it compiles, ten seconds in this strips the component from
    //      Ground Truth instruments ONLY, leaving every other antenna alone. The
    //      vanilla antenna is the control: its marker should stay while ours go.
    //
    // Watch for, in order of how bad they are: the terminal broadcast controls throwing
    // when opened, the block faulting on save, the component coming straight back.
    // Ground Truth's own blocks are the only thing touched, so nothing else in the
    // world is at risk.
    //
    // Read the log for "HUDPROBE".

    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class HudMarkerProbeSession : MySessionComponentBase
    {
        private int _ticks;
        private bool _done;

        public override void UpdateAfterSimulation()
        {
            if (_done) return;
            if (++_ticks < 600) return;   // ten seconds - past the loading screen
            _done = true;

            try { Walk(); }
            catch (System.Exception e) { MyLog.Default.WriteLineAndConsole("HUDPROBE threw: " + e); }
        }

        private void Walk()
        {
            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities);

            var sb = new StringBuilder();
            int ours = 0, stripped = 0, others = 0;

            foreach (var ent in entities)
            {
                var grid = ent as IMyCubeGrid;
                if (grid == null) continue;

                var ts = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);
                if (ts == null) continue;

                var blocks = new List<Sandbox.ModAPI.IMyRadioAntenna>();
                ts.GetBlocksOfType(blocks);

                foreach (var ant in blocks)
                {
                    var subtype = ant.BlockDefinition.SubtypeName;
                    bool mine = subtype.StartsWith("GT_") && !subtype.StartsWith("GT_RotatingRadarDish");

                    sb.Clear();
                    sb.Append("HUDPROBE ").Append(subtype);
                    sb.Append(" working=").Append(ant.IsWorking);
                    sb.Append(" broadcast=").Append(ant.EnableBroadcasting);
                    sb.Append(" hud=").Append(ant.ShowOnHUD);
                    sb.Append(" shipname=").Append(ant.ShowShipName);

                    if (!mine)
                    {
                        others++;
                        sb.Append(" | CONTROL - left alone");
                        MyLog.Default.WriteLineAndConsole(sb.ToString());
                        continue;
                    }

                    ours++;

                    var me = ant as MyEntity;
                    if (me == null)
                    {
                        sb.Append(" | not a MyEntity?");
                        MyLog.Default.WriteLineAndConsole(sb.ToString());
                        continue;
                    }

                    MyDataBroadcaster before = null;
                    me.Components.TryGet<MyDataBroadcaster>(out before);
                    sb.Append(" | broadcaster=").Append(before == null ? "none" : before.GetType().Name);

                    if (before != null)
                    {
                        me.Components.Remove<MyDataBroadcaster>();

                        MyDataBroadcaster after = null;
                        me.Components.TryGet<MyDataBroadcaster>(out after);

                        if (after == null) { stripped++; sb.Append(" -> REMOVED"); }
                        else sb.Append(" -> REMOVE FAILED, still ").Append(after.GetType().Name);
                    }

                    MyLog.Default.WriteLineAndConsole(sb.ToString());
                }
            }

            MyLog.Default.WriteLineAndConsole(string.Format(
                "HUDPROBE done: {0} instruments, {1} stripped, {2} controls left alone.",
                ours, stripped, others));

            MyAPIGateway.Utilities.ShowNotification(string.Format(
                "HUDPROBE: stripped {0} of {1} - instrument markers should be gone, vanilla antenna should remain",
                stripped, ours), 15000, MyFontEnum.Green);
        }
    }
}
