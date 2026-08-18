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
    // THIS LIST MUST MATCH THE Instruments TABLE. An attribute takes compile-time
    // constants only, so it cannot read the table, and the two drift silently.
    //
    // They drifted on 2026-08-11: the four Mk2 subtypes were added to the table, the
    // definitions, the category and the variant group - and not here. Those blocks
    // therefore had no power draw and, once seal state moved to a server sync that
    // registers from this component, never received a seal reading at all. The panel sat
    // on AWAITING SERVER while the plain Habitat Monitor beside it read a 1656-cell
    // sealed room.
    //
    // Instruments.SubtypesWithoutComponent() checks this at load and logs loudly, so the
    // next drift announces itself instead of waiting to be noticed in game.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false,
        "GT_RadiationMonitor", "GT_RadiationMonitor_S",
        "GT_RadiationMonitorAlt", "GT_RadiationMonitorAlt_S",
        "GT_WeatherStation", "GT_WeatherStation_S",
        "GT_WeatherStationAlt", "GT_WeatherStationAlt_S",
        "GT_BioScanner", "GT_BioScanner_S",
        "GT_BioScannerAlt", "GT_BioScannerAlt_S",
        "GT_HabitatMonitor", "GT_HabitatMonitor_S",
        "GT_HabitatMonitorAlt", "GT_HabitatMonitorAlt_S")]
    public class InstrumentPower : MyGameLogicComponent
    {
        /// <summary>
        /// Mirror of the descriptor above. Kept adjacent so the two are edited together,
        /// and compared against the Instruments table at load.
        /// </summary>
        public static readonly string[] AttachedTo =
        {
            "GT_RadiationMonitor", "GT_RadiationMonitor_S",
            "GT_RadiationMonitorAlt", "GT_RadiationMonitorAlt_S",
            "GT_WeatherStation", "GT_WeatherStation_S",
            "GT_WeatherStationAlt", "GT_WeatherStationAlt_S",
            "GT_BioScanner", "GT_BioScanner_S",
            "GT_BioScannerAlt", "GT_BioScannerAlt_S",
            "GT_HabitatMonitor", "GT_HabitatMonitor_S",
            "GT_HabitatMonitorAlt", "GT_HabitatMonitorAlt_S"
        };

        private MyResourceSinkComponent _sink;
        private float _megawatts;
        private IMyFunctionalBlock _block;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            _block = Entity as IMyFunctionalBlock;
            if (_block == null) return;

            _megawatts = Instruments.PowerKWOf(_block.BlockDefinition.SubtypeName) / 1000f;
            if (_megawatts <= 0f) return;

            // DO NOT ADD COMPONENTS DURING Init.
            //
            // The sink used to be attached here, which mutates the block while the grid
            // is still building. Other mods watch the grid for exactly that and rebuild
            // their own systems in response - and at least one of them, WarpDrive, has a
            // constructor that dereferences its block BEFORE its own null check:
            //
            //     if (block.Block.BlockDefinition.SubtypeId == "PrototechFSDriveLarge")
            //     if (block == null || block.Block == null)   // one statement too late
            //
            // Their bug, our trigger. Warp drives are UpgradeModule blocks too, so the
            // instruments only started sharing a grid update path with them when they
            // moved to that base type on 2026-08-11 - and Long Haul started crashing
            // clients on join at the same time, having run that mod for months.
            //
            // Waiting one frame costs nothing: the sink is registered with the
            // distributor by Set + Update below either way.
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            // The server keeps a list of instruments so it can answer the one question
            // clients cannot - see SealSync. Registering here rather than in Init for
            // the same reason the sink attaches here.
            SealSync.Register(Entity as IMyCubeBlock);

            // There used to be a TerminalApi nudge here, on the theory that a block
            // existing proves its type's control list is already built. Disproven
            // 2026-08-18: registering GT_ properties reactively, purely from
            // CustomControlGetter (see GroundTruthSession), is the only timing that did
            // not corrupt every upgrade module's terminal controls in testing. Calling
            // into TerminalApi from here would be exactly the proactive pattern that
            // caused the damage - on our own schedule rather than the game's - so
            // nothing calls it from this file at all.

            if (_megawatts <= 0f) return;

            try
            {
                if (_sink == null)
                {
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
                }

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

        public override void Close()
        {
            SealSync.Unregister(Entity as IMyCubeBlock);
        }

        // Off means off. A disabled instrument costs nothing, which is the whole point
        // of being able to switch one off on a battery-tight outpost.
        private float Required()
        {
            return (_block != null && _block.Enabled) ? _megawatts : 0f;
        }
    }
}
