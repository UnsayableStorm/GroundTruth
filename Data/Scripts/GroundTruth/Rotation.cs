using System;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
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
    // WHY THIS IS A HELPER AND NOT A GAME LOGIC COMPONENT
    //
    // It used to be one, with its own descriptor on the four Weather Station subtypes.
    // InstrumentPower is also bound to those subtypes, and two descriptors on one
    // type+subtype make the engine wrap both in a composite component and log
    // "Possible entity type script logic collision" for each - harmless, but it is the
    // kind of F11 noise players report (Workshop, 2026-10-04). So the spin is now driven
    // by whichever component already owns the block: InstrumentPower for instruments,
    // SpinningAntenna for the dish, which nothing else binds to. One component per
    // subtype, no composites, no notice.
    //
    // The spin is presentation. It stops when the block loses power because that is
    // true, but nothing about the rotation rate is derived from or implies a reading.
    public sealed class SubpartSpinner
    {
        private const float DegreesPerTick = 1.0f;
        private const float AccelPerTick = 0.05f;
        private const float DecelPerTick = 0.01f;
        private const double MaxDistanceSq = 1000.0 * 1000.0;

        private static readonly Vector3 RotationAxis = Vector3.Up;

        private readonly IMyFunctionalBlock _block;
        private readonly string _subpartName;
        private bool _firstFind = true;
        private bool _failed;
        private Matrix _local;
        private float _speed;
        private int _checkIn;
        private bool _operational;

        private SubpartSpinner(IMyFunctionalBlock block, string subpartName)
        {
            _block = block;
            _subpartName = subpartName;
        }

        /// <summary>
        /// A spinner for this block, or null when there is nothing to animate here: no
        /// subpart named, no physics yet, or a dedicated server, which has no camera.
        /// The caller asks for EACH_FRAME only when this returns non-null.
        /// </summary>
        public static SubpartSpinner TryCreate(IMyEntity entity, string subpartName)
        {
            if (MyAPIGateway.Utilities.IsDedicated) return null;
            if (string.IsNullOrEmpty(subpartName)) return null;

            var block = entity as IMyFunctionalBlock;
            if (block == null || block.CubeGrid == null || block.CubeGrid.Physics == null) return null;

            return new SubpartSpinner(block, subpartName);
        }

        public void Tick()
        {
            if (_failed) return;

            try
            {
                // Operational, not IsWorking: an UpgradeModule's IsWorking ignores power, so
                // an instrument kept spinning on a dead grid (Instruments.OfflineReason).
                // Checked once a second, not every frame - the spin-down is gradual anyway.
                if (--_checkIn <= 0) { _checkIn = 60; _operational = Instruments.IsOperational(_block); }
                bool shouldSpin = _operational;
                if (!shouldSpin && Math.Abs(_speed) < 0.00001f) return;

                if (shouldSpin && _speed < 1f) _speed = Math.Min(_speed + AccelPerTick, 1f);
                else if (!shouldSpin && _speed > 0f) _speed = Math.Max(_speed - DecelPerTick, 0f);

                var cam = MyAPIGateway.Session.Camera.WorldMatrix.Translation;
                if (Vector3D.DistanceSquared(cam, _block.GetPosition()) > MaxDistanceSq) return;

                MyEntitySubpart subpart;
                if (!_block.TryGetSubpart(_subpartName, out subpart)) return;  // absent while building

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
                // Stop spinning, not the host: InstrumentPower carries this too, and a
                // broken animation must not take the power sink down with it.
                MyLog.Default.WriteLineAndConsole("GroundTruth SubpartSpinner: " + e);
                _failed = true;
            }
        }
    }

    // The dish is the tactical sensor and is in the Instruments table as RoleRadar, but
    // with no Subpart there: InstrumentPower is not bound to it (a RadioAntenna draws its
    // own power), so its subpart is named here instead. This is the only component bound
    // to it - put any future dish game logic in here rather than adding a second
    // descriptor, which would bring back the script logic collision notice.
    // Instruments spin from InstrumentPower instead.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_RadioAntenna), false,
        "GT_RotatingRadarDish", "GT_RotatingRadarDish_S")]
    public class SpinningAntenna : MyGameLogicComponent
    {
        private SubpartSpinner _spinner;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            var block = Entity as IMyCubeBlock;
            if (block == null) return;

            _spinner = SubpartSpinner.TryCreate(Entity, SubpartFor(block.BlockDefinition.SubtypeName));
            if (_spinner != null) NeedsUpdate = MyEntityUpdateEnum.EACH_FRAME;
        }

        public override void UpdateBeforeSimulation()
        {
            if (_spinner != null) _spinner.Tick();
        }

        private static string SubpartFor(string subtype)
        {
            if (subtype == "GT_RotatingRadarDish") return "RotateRadar";
            if (subtype == "GT_RotatingRadarDish_S") return "SG_RotateRadar";
            return null;
        }
    }
}
