using System;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;

namespace PaneProbe
{
    // Two questions, four blocks.
    //
    // 1. Does this block type render the terminal's detail info pane? The writing path
    //    here is identical to Ground Truth's - subscribe to AppendingCustomInfo, then
    //    RefreshCustomInfo so the game asks for the text.
    //
    //    ANSWERED 2026-08-10: all four render it, INCLUDING OreDetector, which an
    //    earlier finding said rendered none. That finding was wrong. The likely cause
    //    is that the original test never called RefreshCustomInfo, so the writer was
    //    never asked for text - the pane was there and empty, not absent.
    //
    // 2. Can an UpgradeModule be given a power draw? This matters because instruments
    //    are moving to UpgradeModule to escape the antenna's HUD marker, and
    //    MyObjectBuilder_UpgradeModuleDefinition has exactly one field, Upgrades. No
    //    ResourceSinkGroup, no RequiredPowerInput. Whatever power draw these blocks get
    //    has to be attached in code.
    //
    //    What the pane reports for PROBE_UpgradeModule:
    //
    //      sink added      the component was created and attached at all
    //      required        what we are asking the grid for
    //      current         what the grid is actually giving us - if this stays 0 while
    //                      required is not, the sink was never registered with the
    //                      grid's distributor and the draw is decorative
    //      IsWorking       the thing that matters. Cut power at the reactor: if this
    //                      goes false, the sink genuinely gates the block. If it stays
    //                      true on a dead grid, an UpgradeModule cannot be power-gated
    //                      by a sink bolted on afterwards.
    //
    // Read the log for "PANEPROBE".

    public abstract class PaneProbeBase : MyGameLogicComponent
    {
        protected IMyTerminalBlock Block;
        private int _ticks;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate = MyEntityUpdateEnum.EACH_100TH_FRAME;
        }

        public override void UpdateBeforeSimulation100()
        {
            if (Block == null)
            {
                Block = Entity as IMyTerminalBlock;
                if (Block == null) return;

                Block.AppendingCustomInfo += Append;
                MyLog.Default.WriteLineAndConsole("PANEPROBE hooked " + Label());
            }

            _ticks++;
            Block.RefreshCustomInfo();
        }

        private void Append(IMyTerminalBlock block, StringBuilder sb)
        {
            sb.AppendLine("PANE OK - " + Label());
            sb.AppendLine("refresh #" + _ticks);
            Extra(sb);

            if (_ticks <= 3)
                MyLog.Default.WriteLineAndConsole("PANEPROBE writer called for " + Label());
        }

        public override void Close()
        {
            if (Block != null) Block.AppendingCustomInfo -= Append;
        }

        protected abstract string Label();
        protected virtual void Extra(StringBuilder sb) { }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false, "PROBE_UpgradeModule")]
    public class PaneProbeUpgrade : PaneProbeBase
    {
        private const float Draw = 0.02f;   // 20 kW, about what a small instrument should cost

        private MyResourceSinkComponent _sink;
        private string _sinkNote = "not attempted";

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);

            // Attach the sink as early as possible - the grid builds its power graph
            // from what a block has when it joins.
            try
            {
                _sink = Entity.Components.Get<MyResourceSinkComponent>();
                if (_sink != null)
                {
                    _sinkNote = "already had one";
                    return;
                }

                var info = new MyResourceSinkInfo
                {
                    ResourceTypeId = MyResourceDistributorComponent.ElectricityId,
                    MaxRequiredInput = Draw,
                    RequiredInputFunc = Required
                };

                _sink = new MyResourceSinkComponent();
                _sink.Init(MyStringHash.GetOrCompute("Utility"), info);
                Entity.Components.Add<MyResourceSinkComponent>(_sink);
                _sinkNote = "added in Init";
            }
            catch (Exception e)
            {
                _sinkNote = "THREW: " + e.Message;
                MyLog.Default.WriteLineAndConsole("PANEPROBE sink failed: " + e);
            }
        }

        private float Required()
        {
            var fb = Entity as IMyFunctionalBlock;
            return (fb != null && fb.Enabled) ? Draw : 0f;
        }

        protected override void Extra(StringBuilder sb)
        {
            sb.AppendLine("--- power ---");
            sb.AppendLine("sink: " + _sinkNote);

            if (_sink != null)
            {
                try
                {
                    var id = MyResourceDistributorComponent.ElectricityId;
                    sb.AppendLine(string.Format("required {0:F4} MW", _sink.RequiredInputByType(id)));
                    sb.AppendLine(string.Format("current  {0:F4} MW", _sink.CurrentInputByType(id)));
                    sb.AppendLine("powered: " + _sink.IsPoweredByType(id));
                }
                catch (Exception e)
                {
                    sb.AppendLine("sink read threw: " + e.Message);
                }
            }

            var fb = Entity as IMyFunctionalBlock;
            if (fb != null)
            {
                sb.AppendLine("Enabled " + fb.Enabled + " / IsWorking " + fb.IsWorking
                              + " / IsFunctional " + fb.IsFunctional);
                sb.AppendLine("CUT GRID POWER: does IsWorking go false?");
            }
        }

        protected override string Label() { return "UpgradeModule"; }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_SensorBlock), false, "PROBE_Sensor")]
    public class PaneProbeSensor : PaneProbeBase
    {
        protected override string Label() { return "SensorBlock"; }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_CameraBlock), false, "PROBE_Camera")]
    public class PaneProbeCamera : PaneProbeBase
    {
        protected override string Label() { return "CameraBlock"; }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_OreDetector), false, "PROBE_OreDetector")]
    public class PaneProbeOreDetector : PaneProbeBase
    {
        protected override string Label() { return "OreDetector (control)"; }
    }
}
