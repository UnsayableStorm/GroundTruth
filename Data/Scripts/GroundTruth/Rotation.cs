using System;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace GroundTruth
{
    // Spinning subparts for the models that have them.
    //
    // Adapted from Digi's public spinning-subpart example, by way of MTGraves' Naval
    // Theme Prop Pack. His copies cannot be reused directly: MyEntityComponentDescriptor
    // binds a block TYPE plus subtype, and his are keyed to MyObjectBuilder_RadioAntenna
    // with his subtype names, so they never fire on ours.
    //
    // The third descriptor argument is params string[], so one class covers every
    // animated subtype of a given type instead of the six near-identical files the
    // source pack carries. The subpart name comes from the Instruments table.
    //
    // The spin is presentation. It stops when the block loses power because that is
    // true, but nothing about the rotation rate is derived from or implies a reading.
    public abstract class SpinningSubpart : MyGameLogicComponent
    {
        private const float DegreesPerTick = 1.0f;
        private const float AccelPerTick = 0.05f;
        private const float DecelPerTick = 0.01f;
        private const double MaxDistanceSq = 1000.0 * 1000.0;

        private static readonly Vector3 RotationAxis = Vector3.Up;

        private IMyFunctionalBlock _block;
        private string _subpartName;
        private bool _firstFind = true;
        private Matrix _local;
        private float _speed;

        protected abstract string SubpartFor(string subtype);

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            // Nothing to animate on a dedicated server - there is no camera there.
            if (MyAPIGateway.Utilities.IsDedicated) return;

            _block = Entity as IMyFunctionalBlock;
            if (_block == null || _block.CubeGrid == null || _block.CubeGrid.Physics == null) return;

            _subpartName = SubpartFor(_block.BlockDefinition.SubtypeName);
            if (string.IsNullOrEmpty(_subpartName)) return;

            NeedsUpdate = MyEntityUpdateEnum.EACH_FRAME;
        }

        public override void UpdateBeforeSimulation()
        {
            try
            {
                bool shouldSpin = _block.IsWorking;
                if (!shouldSpin && Math.Abs(_speed) < 0.00001f) return;

                if (shouldSpin && _speed < 1f) _speed = Math.Min(_speed + AccelPerTick, 1f);
                else if (!shouldSpin && _speed > 0f) _speed = Math.Max(_speed - DecelPerTick, 0f);

                var cam = MyAPIGateway.Session.Camera.WorldMatrix.Translation;
                if (Vector3D.DistanceSquared(cam, _block.GetPosition()) > MaxDistanceSq) return;

                MyEntitySubpart subpart;
                if (!Entity.TryGetSubpart(_subpartName, out subpart)) return;  // absent while building

                // Subparts are recreated on repaint and lose their orientation, so the
                // matrix is cached here rather than read back from the subpart.
                if (_firstFind)
                {
                    _firstFind = false;
                    _local = subpart.PositionComp.LocalMatrixRef;
                }

                if (_speed > 0f)
                {
                    _local *= Matrix.CreateFromAxisAngle(RotationAxis, MathHelper.ToRadians(_speed * DegreesPerTick));
                    _local = Matrix.Normalize(_local);
                }

                subpart.PositionComp.SetLocalMatrix(ref _local);
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("GroundTruth SpinningSubpart: " + e);
                NeedsUpdate = MyEntityUpdateEnum.NONE;
            }
        }
    }

    // Two descriptors, because the blocks no longer share an object builder.
    //
    // Instruments are UpgradeModule; the general purpose antenna is a real
    // RadioAntenna. A descriptor binds a TYPE plus subtypes, so one class cannot cover
    // both - but the spinning logic is identical, so both derive from SpinningSubpart
    // and only the subpart lookup differs.
    //
    // See the header of CubeBlocks_GroundTruth.sbc for why instruments stopped being
    // antennas.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false,
        "GT_WeatherStation", "GT_WeatherStation_S",
        "GT_WeatherStationAlt", "GT_WeatherStationAlt_S")]
    public class SpinningInstrument : SpinningSubpart
    {
        protected override string SubpartFor(string subtype)
        {
            Instruments.Info info;
            return Instruments.TryGet(subtype, out info) ? info.Subpart : null;
        }
    }

    // The dish is not an instrument and is deliberately absent from the Instruments
    // table, so its subpart is named here.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_RadioAntenna), false,
        "GT_RotatingRadarDish", "GT_RotatingRadarDish_S")]
    public class SpinningAntenna : SpinningSubpart
    {
        protected override string SubpartFor(string subtype)
        {
            if (subtype == "GT_RotatingRadarDish") return "RotateRadar";
            if (subtype == "GT_RotatingRadarDish_S") return "SG_RotateRadar";
            return null;
        }
    }
}
