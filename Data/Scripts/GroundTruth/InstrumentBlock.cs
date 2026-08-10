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
    // Being an antenna is a means, not a purpose. Broadcasting is switched off once,
    // when the block is first placed, so an instrument does not announce a grid to the
    // galaxy just because of how it is implemented. MaxBroadcastRadius is 1 anyway, so
    // this is belt and braces - but it also keeps the terminal reading honestly "off"
    // rather than showing a broadcast that does nothing.
    //
    // Deliberately ONCE, not enforced. A player who turns broadcasting on has decided
    // something; the mod does not get to override that every tick.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_RadioAntenna), false,
        "GT_RadiationMonitor", "GT_RadiationMonitor_S",
        "GT_RadiationMonitorAlt", "GT_RadiationMonitorAlt_S",
        "GT_WeatherStation", "GT_WeatherStation_S",
        "GT_WeatherStationAlt", "GT_WeatherStationAlt_S",
        "GT_BioScanner", "GT_BioScanner_S",
        "GT_HabitatMonitor", "GT_HabitatMonitor_S")]
    public class InstrumentBlock : MyGameLogicComponent
    {
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            var antenna = Entity as IMyRadioAntenna;
            if (antenna == null || antenna.CubeGrid == null || antenna.CubeGrid.Physics == null)
                return;

            // Only on a freshly placed block. A loaded one keeps whatever the player set.
            if (antenna.EnableBroadcasting)
                antenna.EnableBroadcasting = false;
        }
    }
}
