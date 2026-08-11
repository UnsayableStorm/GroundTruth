using System;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.ObjectBuilders;
using VRage.Utils;

namespace GroundTruth
{
    // Gives instruments a power draw, because their block type has none.
    //
    // MyObjectBuilder_UpgradeModuleDefinition has exactly one field, Upgrades. No
    // ResourceSinkGroup, no RequiredPowerInput. That is a fair price for a base type
    // with no HUD marker, no broadcaster and no lightning rod - but it means an
    // instrument would otherwise run for free, which is wrong for a block whose whole
    // premise is that measuring the world costs something.
    //
    // Draw per subtype comes from the Instruments table, so a new variant declares its
    // cost on the same line as its role.
    //
    // WHAT THE PROBE ESTABLISHED, 2026-08-10 (probes/PaneProbe)
    //
    //   - Creating the sink and adding the component is NOT enough. The first attempt
    //     reported required=0.0000 while claiming powered=true: a sink that asks for
    //     nothing is always satisfied. SetRequiredInputByType plus Update is what
    //     actually enters the grid's power ledger.
    //   - A sink attached this way DOES gate the block. Cutting grid power drops
    //     IsWorking to false, verified in game. That matters beyond flavour: every
    //     reading is served behind IsWorking, and GT_SysOperational reports it, so an
    //     unpowered instrument stops answering instead of lying quietly.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false,
        "GT_RadiationMonitor", "GT_RadiationMonitor_S",
        "GT_RadiationMonitorAlt", "GT_RadiationMonitorAlt_S",
        "GT_WeatherStation", "GT_WeatherStation_S",
        "GT_WeatherStationAlt", "GT_WeatherStationAlt_S",
        "GT_BioScanner", "GT_BioScanner_S",
        "GT_HabitatMonitor", "GT_HabitatMonitor_S")]
    public class InstrumentPower : MyGameLogicComponent
    {
        private MyResourceSinkComponent _sink;
        private float _megawatts;
        private IMyFunctionalBlock _block;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            _block = Entity as IMyFunctionalBlock;
            if (_block == null) return;

            _megawatts = Instruments.PowerKWOf(_block.BlockDefinition.SubtypeName) / 1000f;
            if (_megawatts <= 0f) return;

            try
            {
                // Attach during Init - the grid builds its power graph from what a
                // block brings with it.
                _sink = Entity.Components.Get<MyResourceSinkComponent>();
                if (_sink == null)
                {
                    var info = new MyResourceSinkInfo
                    {
                        ResourceTypeId = MyResourceDistributorComponent.ElectricityId,
                        MaxRequiredInput = _megawatts,
                        RequiredInputFunc = Required
                    };

                    _sink = new MyResourceSinkComponent();
                    _sink.Init(MyStringHash.GetOrCompute("Utility"), info);
                    Entity.Components.Add<MyResourceSinkComponent>(_sink);
                }

                NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
            }
            catch (Exception e)
            {
                // A missing power draw is a nuisance; a throw here would take the
                // block with it. Log and carry on unpowered.
                _sink = null;
                MyLog.Default.WriteLineAndConsole("GroundTruth InstrumentPower init: " + e);
            }
        }

        public override void UpdateOnceBeforeFrame()
        {
            if (_sink == null) return;

            try
            {
                var id = MyResourceDistributorComponent.ElectricityId;
                _sink.SetMaxRequiredInputByType(id, _megawatts);
                _sink.SetRequiredInputByType(id, _megawatts);
                _sink.Update();
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("GroundTruth InstrumentPower update: " + e);
            }
        }

        // Off means off. A disabled instrument costs nothing, which is the whole point
        // of being able to switch one off on a battery-tight outpost.
        private float Required()
        {
            return (_block != null && _block.Enabled) ? _megawatts : 0f;
        }
    }
}
