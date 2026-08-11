using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;

namespace PaneProbe
{
    // One component, four block types, one question: does the detail info pane exist?
    //
    // The writing path here is deliberately IDENTICAL to Ground Truth's - subscribe to
    // AppendingCustomInfo, then RefreshCustomInfo to make the game ask for it. If the
    // text appears for a type, Ground Truth's readouts will work on that type.
    //
    // See CubeBlocks_PaneProbe.sbc for what each block is and how to read the result.

    public abstract class PaneProbeBase : MyGameLogicComponent
    {
        private IMyTerminalBlock _block;
        private int _ticks;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate = MyEntityUpdateEnum.EACH_100TH_FRAME;
        }

        public override void UpdateBeforeSimulation100()
        {
            if (_block == null)
            {
                _block = Entity as IMyTerminalBlock;
                if (_block == null) return;

                _block.AppendingCustomInfo += Append;
                MyLog.Default.WriteLineAndConsole("PANEPROBE hooked " + Label());
            }

            // Keep refreshing - a pane that only fills on the second click would
            // otherwise read as a failure.
            _ticks++;
            _block.RefreshCustomInfo();
        }

        private void Append(IMyTerminalBlock block, StringBuilder sb)
        {
            sb.AppendLine("PANE OK - " + Label());
            sb.AppendLine("refresh #" + _ticks);
            sb.AppendLine("If you can read this, this block type renders the pane.");

            // Logged as well, so "the writer ran but nothing drew" is distinguishable
            // from "the writer was never called".
            if (_ticks <= 3)
                MyLog.Default.WriteLineAndConsole("PANEPROBE writer called for " + Label());
        }

        public override void Close()
        {
            if (_block != null) _block.AppendingCustomInfo -= Append;
        }

        protected abstract string Label();
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false, "PROBE_UpgradeModule")]
    public class PaneProbeUpgrade : PaneProbeBase
    {
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
