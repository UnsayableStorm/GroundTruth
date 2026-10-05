using System;
using VRageMath;
namespace GroundTruth {
public static class MathTest {
  static int fails;
  static void Eq(string n, double got, double want) { bool ok = Math.Abs(got - want) < 1e-6; if (!ok) fails++; Console.WriteLine((ok ? "PASS " : "FAIL ") + n + " got=" + got + " want=" + want); }
  static void Is(string n, bool got, bool want) { if (got != want) fails++; Console.WriteLine((got == want ? "PASS " : "FAIL ") + n + " got=" + got); }
  // Readings.BearingFrom's sign rule, copied verbatim. It turns the OTHER way: a target off
  // the right reads 270. Kept here as the evidence until that v1 code is fixed.
  static double ReadingsRule(Vector3D fwd, Vector3D to, Vector3D up) {
    fwd = Vector3D.Normalize(fwd); to = Vector3D.Normalize(to);
    double ang = Math.Acos(MathHelper.Clamp(Vector3D.Dot(fwd, to), -1.0, 1.0)) * 180.0 / Math.PI;
    if (Vector3D.Dot(Vector3D.Cross(fwd, to), up) < 0) ang = 360.0 - ang;
    return ang;
  }
  public static int Main() {
    var m = MatrixD.Identity;
    Console.WriteLine("Identity Forward=" + m.Forward + " Right=" + m.Right + " Up=" + m.Up);
    Console.WriteLine("Readings rule, target to the RIGHT: " + ReadingsRule(m.Forward, m.Right, m.Up));
    var O = Vector3D.Zero; var up = m.Up; var f = m.Forward;
    Eq("bearing ahead", TacticalMath.Bearing(O, f * 100, up, f), 0);
    Eq("bearing right", TacticalMath.Bearing(O, m.Right * 100, up, f), 90);
    Eq("bearing behind", TacticalMath.Bearing(O, m.Backward * 100, up, f), 180);
    Eq("bearing left", TacticalMath.Bearing(O, m.Left * 100, up, f), 270);
    Eq("bearing right+above ignores height", TacticalMath.Bearing(O, m.Right * 100 + up * 500, up, f), 90);
    Eq("bearing straight up undefined", TacticalMath.Bearing(O, up * 100, up, f), -1);
    Eq("bearing zero up undefined", TacticalMath.Bearing(O, f, Vector3D.Zero, f), -1);
    Eq("bearing from offset origin", TacticalMath.Bearing(new Vector3D(10,0,0), new Vector3D(10,0,-50), up, f), 0);
    Eq("elev level", TacticalMath.Elevation(O, f * 100, up), 0);
    Eq("elev 45 up", TacticalMath.Elevation(O, f * 100 + up * 100, up), 45);
    Eq("elev straight down", TacticalMath.Elevation(O, -up * 5, up), -90);
    Eq("elev same point", TacticalMath.Elevation(O, O, up), 0);
    Eq("closing head-on 10", TacticalMath.ClosingRate(O, Vector3D.Zero, f * 1000, -f * 10), 10);
    Eq("opening 10", TacticalMath.ClosingRate(O, Vector3D.Zero, f * 1000, f * 10), -10);
    Eq("both moving same velocity", TacticalMath.ClosingRate(O, f * 50, f * 1000, f * 50), 0);
    Eq("crossing traffic", TacticalMath.ClosingRate(O, Vector3D.Zero, f * 1000, m.Right * 30), 0);
    Eq("observer closing at 20", TacticalMath.ClosingRate(O, f * 20, f * 1000, Vector3D.Zero), 20);
    Is("in range edge", TacticalMath.WithinRange(O, f * 2000, 2000), true);
    Is("out of range", TacticalMath.WithinRange(O, f * 2000.1, 2000), false);
    Is("zero radius", TacticalMath.WithinRange(O, O, 0), false);
    Eq("range", TacticalMath.Range(O, new Vector3D(3,4,0)), 5);
    Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
    return fails;
  }
}}
