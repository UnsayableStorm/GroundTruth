using System;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.ObjectBuilders;

namespace GroundTruth
{
    // Attached to every instrument.
    //
    // The blocks are RadioAntenna object builders because that is the narrowest
    // interface that both scopes our terminal properties and renders a detail info
    // panel - see the header of CubeBlocks_GroundTruth.sbc for how we arrived there.
    //
    // Being an antenna is a means, not a purpose, so three antenna defaults are wrong
    // for an instrument and are cleared when the block is first created:
    //
    //   EnableBroadcasting  an instrument should not announce a grid to the galaxy
    //                       just because of how it is implemented
    //   ShowOnHUD           twelve instruments on a ship put twelve markers on the
    //                       HUD, which is the bug this was written to fix
    //   ShowShipName        same marker, other source - vanilla antennas default it on
    //
    // The HUD markers appear with broadcasting OFF, because neither flag has anything
    // to do with broadcasting. MaxBroadcastRadius 1 makes the radio side inert; it does
    // not make the block stop drawing itself.
    //
    // ONCE, AND ACTUALLY ONCE
    //
    // This used to run every load behind "if (EnableBroadcasting) turn it off", which
    // cannot tell a vanilla default from a player who deliberately switched it on - so
    // it silently undid that choice at every session load. The flag below is written
    // into the block's mod storage and saved with the world, so first-creation defaults
    // are applied exactly once in the block's lifetime and every setting after that
    // belongs to the player.
    //
    // A block placed before this version has no flag, so it gets the defaults on next
    // load. That is the intended migration - it is what clears the markers off a ship
    // that is already built.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_RadioAntenna), false,
        "GT_RadiationMonitor", "GT_RadiationMonitor_S",
        "GT_RadiationMonitorAlt", "GT_RadiationMonitorAlt_S",
        "GT_WeatherStation", "GT_WeatherStation_S",
        "GT_WeatherStationAlt", "GT_WeatherStationAlt_S",
        "GT_BioScanner", "GT_BioScanner_S",
        "GT_HabitatMonitor", "GT_HabitatMonitor_S")]
    public class InstrumentBlock : MyGameLogicComponent
    {
        // Random, fixed, and ours. Changing it re-runs the defaults on every block.
        private static readonly Guid DefaultsApplied =
            new Guid("6f2a1c94-8b3d-4e57-9a10-2c7e5d84b0f3");

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            var antenna = Entity as IMyRadioAntenna;
            if (antenna == null || antenna.CubeGrid == null || antenna.CubeGrid.Physics == null)
                return;

            // Server owns the decision; the flags replicate to clients on their own.
            if (MyAPIGateway.Multiplayer != null && !MyAPIGateway.Multiplayer.IsServer)
                return;

            if (Entity.Storage == null)
                Entity.Storage = new MyModStorageComponent();

            string done;
            if (Entity.Storage.TryGetValue(DefaultsApplied, out done))
                return;

            Entity.Storage[DefaultsApplied] = "1";

            antenna.EnableBroadcasting = false;
            antenna.ShowOnHUD = false;
            antenna.ShowShipName = false;
        }
    }
}
