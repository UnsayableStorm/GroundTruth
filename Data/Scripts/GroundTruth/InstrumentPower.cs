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
    //   - WRONG, corrected 2026-10-10: "a sink attached this way gates the block -
    //     cutting grid power drops IsWorking to false". It does not. An UpgradeModule's
    //     IsWorking is enabled-and-functional only (MyFunctionalBlock.CheckIsWorking,
    //     decompiled); the engine never consults this sink. The August observation was
    //     most likely the probe's OreDetector clone, which has a power check of its
    //     own. Nor was "every reading served behind IsWorking" true - nothing gated
    //     readings at all, so an instrument on a dead grid kept measuring (Jason's
    //     report, after the Tactical release was staged). The gate now exists:
    //     Instruments.OfflineReason checks power explicitly - from the grid's
    //     distributor, not this sink (trap 22) - and readings, properties, events,
    //     panels and the spin all go through it.
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

        // This is the only component bound to instrument subtypes, so it also drives
        // the spin for the ones that have a rotating subpart. See SubpartSpinner for
        // why that is not a second component.
        private SubpartSpinner _spinner;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            _block = Entity as IMyFunctionalBlock;
            if (_block == null) return;

            _megawatts = Instruments.PowerKWOf(_block.BlockDefinition.SubtypeName) / 1000f;
            if (_megawatts <= 0f) return;

            // THE SINK IS ATTACHED HERE, IN Init, AND NOWHERE LATER.
            //
            // The grid hands a block's sink to its power distributor in exactly one place,
            // MyCubeGridSystems.RegisterInSystems, once, when the block is added (and again
            // on a merge or split). GameLogic.Init runs inside MySlimBlock.Init, BEFORE
            // that call, in both of MyCubeGrid's add paths (decompiled 2026-10-15). A sink
            // attached any later is never registered: never supplied, IsPowered false
            // forever, and its draw missing from the grid's power use. AddSink is not on
            // the ModAPI, so there is no registering late (ENGINE_TRAPS trap 22).
            //
            // From 2026-08-11 to 2026-10-15 the attach sat in UpdateOnceBeforeFrame, and the
            // instruments drew nothing at all for those two months. It had been moved there
            // on the theory that attaching here "mutates the block while the grid is still
            // building" and tripped a WarpDrive crash on Long Haul. Read against WarpDrive's
            // own source (2640137506, GridSystem.cs) that theory does not hold: its grid
            // system listens to OnBlockAdded, OnBlockRemoved, OnGridSplit, OnStaticChanged
            // and OnClose - never to a block's components - and this runs before the block
            // is even added. The crash that is actually on record (2026-08-28) is a null
            // GameLogic in WarpSystem's BlockCounter predicate during a split, which no sink
            // timing touches. The base-type change of 2026-08-11 coincided with the
            // crashes; the sink timing was blamed with it. Re-tested against WarpDrive on
            // 2026-10-15 before shipping - see docs/HANDOFF.md.
            try
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

                // SetRequired + Update: a sink that asks for nothing is always satisfied
                // (trap 13). Update re-reads Required(), so off costs nothing from the start.
                var id = MyResourceDistributorComponent.ElectricityId;
                _sink.SetMaxRequiredInputByType(id, _megawatts);
                _sink.SetRequiredInputByType(id, _megawatts);
                _sink.Update();
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("GroundTruth InstrumentPower init: " + e);
            }

            // Everything else still waits a frame: SealSync, the PB handshake, property
            // registration and the spinner all want a block that is on its grid.
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            // The server keeps a list of instruments so it can answer the one question
            // clients cannot - see SealSync. Registered here rather than in Init so the
            // block is already on its grid.
            SealSync.Register(Entity as IMyCubeBlock);

            // The PB handshake that gives dedicated servers the property API - see the
            // long note in TerminalApi. Subscribing costs nothing until a script writes
            // the token, and it is the only route to these blocks on a machine where no
            // terminal is ever opened. Subscribed on clients too: harmless there, and a
            // script written against a server should behave identically in single
            // player.
            var terminal = Entity as IMyTerminalBlock;
            if (terminal != null)
            {
                terminal.CustomDataChanged -= TerminalApi.OnInstrumentCustomDataChanged;
                terminal.CustomDataChanged += TerminalApi.OnInstrumentCustomDataChanged;
            }

            // The GT_ property API registers here, at the first instrument's first
            // frame, on every machine. This block's own BeforeGameLogicInit has already
            // built every control list involved, which is the one condition that makes
            // touching them safe.
            //
            // There was a nudge here once before, removed 2026-08-18 as "disproven" -
            // wrongly: the builds that broke also read GetControls at LoadData, before
            // any block existed, and that read was the real cause. Settled by decompile
            // and by probes/RegTimingProbe (SP, DS server + client, Animation Engine,
            // all PASS 2026-10-07). Full account in TerminalApi.RegisterAtLoad.
            // Wrapped: whatever happens here must not cost the instrument its power sink below.
            try { TerminalApi.RegisterAtLoad(terminal); }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole("GroundTruth RegisterAtLoad: " + e); }

            Instruments.Info instrument;
            if (_block != null && Instruments.TryGet(_block.BlockDefinition.SubtypeName, out instrument))
            {
                _spinner = SubpartSpinner.TryCreate(Entity, instrument.Subpart);
                if (_spinner != null) NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
            }

            // Switching off must drop the draw at once. RequiredInputFunc is only consulted
            // when the sink is told to Update; nothing in the engine does that for an
            // UpgradeModule's Enabled, so it is done here.
            //
            // No retry here when Init failed to attach: a sink attached this late is never
            // registered (see Init), so it would only report a draw that is not happening.
            if (_sink != null && _block != null)
            {
                _block.EnabledChanged -= OnEnabledChanged;
                _block.EnabledChanged += OnEnabledChanged;
                try { _sink.Update(); }
                catch (Exception e) { MyLog.Default.WriteLineAndConsole("GroundTruth InstrumentPower update: " + e); }
            }
        }

        private void OnEnabledChanged(IMyTerminalBlock unused)
        {
            try { if (_sink != null) _sink.Update(); }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole("GroundTruth InstrumentPower enabled: " + e); }
        }

        public override void UpdateBeforeSimulation()
        {
            if (_spinner != null) _spinner.Tick();
        }

        public override void Close()
        {
            SealSync.Unregister(Entity as IMyCubeBlock);
            if (_block != null) _block.EnabledChanged -= OnEnabledChanged;

            var terminal = Entity as IMyTerminalBlock;
            if (terminal != null)
                terminal.CustomDataChanged -= TerminalApi.OnInstrumentCustomDataChanged;
        }

        // Off means off. A disabled instrument costs nothing, which is the whole point
        // of being able to switch one off on a battery-tight outpost.
        private float Required()
        {
            return (_block != null && _block.Enabled) ? _megawatts : 0f;
        }
    }
}
