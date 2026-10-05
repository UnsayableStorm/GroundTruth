// Pure geometry for the tactical picture: vectors and numbers in, numbers out. No
// entity, no session, no logging - so it can be compiled and tested outside the game
// against the real VRage.Math.dll, which is how it was checked (plan 9.4). Anything
// here that needs the world belongs in Tactical.cs instead.
//
// BEARINGS ARE CLOCKWISE SEEN FROM ABOVE: a target off the right side of the reference
// reads 90. That is a compass convention and it is the one a player will check against.
// It was tested, not reasoned about, because the sign is exactly the kind of thing that
// reads correct on the page and is wrong in the world.
using System;
using VRageMath;

namespace GroundTruth
{
    public static class TacticalMath
    {
        // Bearing: degrees clockwise about `up` from `reference` to direction from->target,
        // viewed looking DOWN the up axis. Returns -1 when the projection is degenerate.
        public static double Bearing(Vector3D from, Vector3D target, Vector3D up, Vector3D reference)
        {
            // Guard against a zero up vector.
            if (up.LengthSquared() < 1e-12)
                return -1;

            Vector3D upN = Vector3D.Normalize(up);

            // Project reference and target direction onto the plane perpendicular to up.
            Vector3D to = target - from;
            Vector3D fwdProj = reference - upN * Vector3D.Dot(reference, upN);
            Vector3D toProj = to - upN * Vector3D.Dot(to, upN);

            // If either projection vanishes the bearing is undefined.
            if (fwdProj.LengthSquared() < 1e-6 || toProj.LengthSquared() < 1e-6)
                return -1;

            Vector3D fwdN = Vector3D.Normalize(fwdProj);
            Vector3D toN = Vector3D.Normalize(toProj);

            double d = Vector3D.Dot(fwdN, toN);
            double angle = Math.Acos(MathHelper.Clamp(d, -1.0, 1.0)) * (180.0 / Math.PI);

            // cross(fwd, to) aligned with +up means counter-clockwise seen from +up,
            // i.e. clockwise seen looking DOWN, so subtract from 360 to get clockwise.
            Vector3D c = Vector3D.Cross(fwdN, toN);
            if (Vector3D.Dot(c, upN) > 0)
                angle = 360.0 - angle;

            if (angle >= 360.0)
                angle = 0.0;

            return angle;
        }

        // Elevation: degrees above (+) or below (-) the plane perpendicular to up.
        // A target at the observer's own position has no elevation, so return 0.
        public static double Elevation(Vector3D from, Vector3D target, Vector3D up)
        {
            Vector3D to = target - from;
            if (to.LengthSquared() < 1e-6)
                return 0;

            if (up.LengthSquared() < 1e-12)
                return 0;

            Vector3D toN = Vector3D.Normalize(to);
            Vector3D upN = Vector3D.Normalize(up);

            double d = Vector3D.Dot(toN, upN);
            return Math.Asin(MathHelper.Clamp(d, -1.0, 1.0)) * (180.0 / Math.PI);
        }

        // ClosingRate: rate at which separation is decreasing (positive = closing).
        public static double ClosingRate(Vector3D observerPos, Vector3D observerVel, Vector3D targetPos, Vector3D targetVel)
        {
            Vector3D sep = targetPos - observerPos;
            if (sep.LengthSquared() < 1e-6)
                return 0;

            Vector3D sepN = Vector3D.Normalize(sep);
            Vector3D relVel = targetVel - observerVel;
            return -Vector3D.Dot(relVel, sepN);
        }

        // WithinRange: true if within `range` metres. Zero radius reaches nobody.
        public static bool WithinRange(Vector3D a, Vector3D b, double range)
        {
            if (range <= 0)
                return false;

            return Vector3D.DistanceSquared(a, b) <= range * range;
        }

        // Range: straight-line distance in metres.
        public static double Range(Vector3D a, Vector3D b)
        {
            return Vector3D.Distance(a, b);
        }
    }
}
