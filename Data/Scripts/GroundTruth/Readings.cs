using System;
using System.Collections.Generic;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace GroundTruth
{
    // Environmental measurement.
    //
    // Every formula here was derived empirically and validated in the field. The
    // supporting data lives in the project's WEATHER_RESULTS.md and ROADMAP.md; the
    // short version is below so nobody has to guess later.
    //
    //   solar     = BASE x max(0, 1 - protectionFactor x airDensity)
    //               zero unless there is clear line of sight to the sun
    //   planetary = the planet's RadiationGain
    //               zero inside an airtight pressurised volume
    //   weather   = the active effect's RadiationGain, if any
    //               NOT the solar multiplier - verified, that governs panels only
    //   BASE      = SolarRadiationPerSecond x SolarRadiationIntensity
    //
    // Confirmed across five worlds, four protection factors, and a continuous
    // air-density sweep from 0.92 to vacuum.
    public static class Readings
    {
        // The hazard component applies exposure every 100 ticks. Derived independently
        // from stat quantisation on Europa: a 0.6/s source produced exact 1.0 steps.
        public const double HazardUpdateSeconds = 100.0 / 60.0;

        // Exposure below SourcesIgnoredExposureLowerThreshold PER UPDATE is discarded
        // entirely and decay wins, so a site below it is safe indefinitely. The SBC
        // comment says "per second" and is wrong: measured suppression at 0.0511/s
        // (0.0852 per update) and normal accumulation at 0.0603/s (0.1005 per update).
        public const double IgnoredExposurePerUpdate = 0.1;

        // Radiation stat thresholds. 0.745 and 1.0 of a 100 maximum.
        public const double CriticalLevel = 74.5;
        public const double DamageLevel = 100.0;

        // Fallback only - the real value is read from the hazard definition where
        // possible, because it is moddable.
        public const double DefaultSolarPerSecond = 0.5;

        // Lift the raycast origin clear of whatever the block is mounted on. Casting
        // from the block position produced false "sheltered" readings at low sun angles
        // because the ray grazed the ground within a metre. 1.5m clears that while
        // staying inside a 2.5m large-grid room, so an interior block is not lifted
        // through its own ceiling into false daylight.
        private const double SunRayOriginLift = 1.5;
        private const double SunRayLength = 1000.0;

        public struct Environment
        {
            public bool InGravityWell;
            public MyPlanet Planet;
            public double AirDensity;
            public double ProtectionFactor;
            public double PlanetRadiationGain;
            public double MaxWindSpeed;
            public bool SunLosClear;

            // Degrees of the sun above the local horizon. Negative means below it -
            // night, where a solar figure of 100% is true of the WEATHER and false of
            // the panels. -999 when there is no horizon to measure against (in space).
            public double SunElevation;

            // Breathable oxygen at this position, 0-1.
            //
            // MEASURED 2026-08-10: this value ALREADY INCLUDES the weather multiplier.
            // planetOxygen / oxygenMultiplier held constant at 0.8407 across clear,
            // MarsStormHeavy (0.25) and AlienFog (0.00) - so it is base x weather, and
            // showing it beside the multiplier as separate factors would double count.
            //
            // This is the number that decides whether a player can breathe. The weather
            // multiplier is the CONTRIBUTION to it, not a second thing to multiply by.
            public double Oxygen;
            public string SunBlockedBy;
            public double SunBlockedDistance;
        }

        public struct Radiation
        {
            public bool Enabled;
            public double IntensitySetting;
            public double Base;
            public double Solar;
            public double Planetary;
            public double Weather;
            public double Total;
            public bool Accumulates;
            public double SecondsToCritical;   // -1 = never
            // 0 exposed, 1 sun occluded, 2 sealed, 3 shielded by atmosphere.
            //
            // State 3 exists because "EXPOSED" beside an exposure of 0.0000 reads as a
            // contradiction. On Earthlike you are geometrically wide open, and the air
            // is doing all the work: protection 1.80 x density 0.89 = 1.602, which
            // blocks solar entirely.
            public int ShelterState;

            // protectionFactor x airDensity, clamped to 1. At 1 the atmosphere alone is
            // sufficient. Reported as a conclusion rather than as two factors the reader
            // has to multiply.
            public double AtmosphericShielding;
        }

        // ------------------------------------------------------------------

        public static Environment ReadEnvironment(IMyCubeBlock block)
        {
            var env = new Environment { SunBlockedBy = "", SunBlockedDistance = -1 };
            Vector3D pos = block.GetPosition();

            float interference;
            Vector3D gravity = Vector3D.Zero;
            try { gravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(pos, out interference); }
            catch { }

            // Natural gravity cuts off cleanly at the well boundary. Without this gate
            // GetClosestPlanet happily returns a planet from 126km away and every
            // deep-space reading falsely reports that planet's atmosphere.
            env.InGravityWell = gravity.LengthSquared() > 0.000001;

            try { env.Planet = MyGamePruningStructure.GetClosestPlanet(pos); }
            catch { }

            if (env.Planet != null && env.InGravityWell)
            {
                try { env.AirDensity = env.Planet.GetAirDensity(pos); } catch { }
                try { env.ProtectionFactor = env.Planet.Generator.SolarRadiationProtectionFactor; } catch { }
                try { env.PlanetRadiationGain = env.Planet.Generator.RadiationGain; } catch { }
                try { env.MaxWindSpeed = env.Planet.Generator.Atmosphere.MaxWindSpeed; } catch { }
            }

            try
            {
                env.Oxygen = env.Planet != null ? env.Planet.GetOxygenForPosition(pos) : 0.0;
            }
            catch { env.Oxygen = -1; }

            ReadSunLineOfSight(pos, gravity, env.Planet, ref env);
            return env;
        }

        private static void ReadSunLineOfSight(Vector3D pos, Vector3D gravity, MyPlanet planet, ref Environment env)
        {
            Vector3D sunDir;
            try { sunDir = Vector3D.Normalize((Vector3D)MyVisualScriptLogicProvider.GetSunDirection()); }
            catch { env.SunLosClear = true; env.SunElevation = -999; return; }

            // Elevation needs a horizon, which needs a planet.
            env.SunElevation = -999;
            if (planet != null)
            {
                try
                {
                    var up = Vector3D.Normalize(pos - planet.PositionComp.GetPosition());
                    env.SunElevation = Math.Asin(MathHelper.Clamp(
                        Vector3D.Dot(up, sunDir), -1.0, 1.0)) * 180.0 / Math.PI;
                }
                catch { env.SunElevation = -999; }
            }

            // Cheap geometric test first: is the planet body itself between us and the
            // sun? That is true night, which a 1km raycast would never reach.
            if (planet != null)
            {
                Vector3D toCenter = planet.PositionComp.GetPosition() - pos;
                double along = Vector3D.Dot(toCenter, sunDir);
                if (along > 0 && (toCenter - along * sunDir).Length() < planet.AverageRadius)
                {
                    env.SunLosClear = false;
                    env.SunBlockedBy = "planetary night";
                    return;
                }
            }

            try
            {
                Vector3D origin = gravity.LengthSquared() > 0.000001
                    ? pos + Vector3D.Normalize(-gravity) * SunRayOriginLift
                    : pos + sunDir * SunRayOriginLift;

                var hits = new List<IHitInfo>();
                MyAPIGateway.Physics.CastRay(origin, origin + sunDir * SunRayLength, hits);

                IHitInfo nearest = null;
                foreach (var h in hits)
                {
                    if (h.HitEntity == null) continue;
                    if (nearest == null || h.Fraction < nearest.Fraction) nearest = h;
                }

                if (nearest == null)
                {
                    env.SunLosClear = true;
                }
                else
                {
                    env.SunLosClear = false;
                    env.SunBlockedDistance = Vector3D.Distance(origin, nearest.Position);
                    var voxel = nearest.HitEntity as IMyVoxelBase;
                    env.SunBlockedBy = voxel != null ? "terrain" : "structure";
                }
            }
            catch
            {
                env.SunLosClear = true;   // fail open rather than claim false shelter
            }
        }

        // ------------------------------------------------------------------

        public static Radiation ReadRadiation(IMyCubeBlock block, Environment env, bool airtight)
        {
            var rad = new Radiation { SecondsToCritical = -1 };

            try
            {
                rad.Enabled = MyAPIGateway.Session.SessionSettings.EnableRadiation;
                // This is the multiplier itself, a float - off 0, low 0.5, medium 1.0,
                // hard 2.0, and a server may set anything including above 1. Read every
                // update; admins change it.
                rad.IntensitySetting = MyAPIGateway.Session.SessionSettings.SolarRadiationIntensity;
            }
            catch { }

            if (!rad.Enabled || rad.IntensitySetting <= 0)
                return rad;

            rad.Base = DefaultSolarPerSecond * rad.IntensitySetting;

            // Solar: attenuated by atmosphere, then hard-gated on line of sight.
            if (env.SunLosClear)
            {
                double shielding = env.ProtectionFactor * env.AirDensity;
                rad.Solar = rad.Base * Math.Max(0.0, 1.0 - shielding);
            }

            // Planetary: geometry does nothing. Only a sealed volume stops it.
            if (!airtight)
                rad.Planetary = env.PlanetRadiationGain;

            rad.Weather = ReadWeatherRadiation(block);

            rad.Total = Math.Max(0.0, rad.Solar + rad.Planetary + rad.Weather);

            rad.Accumulates = (rad.Total * HazardUpdateSeconds) >= IgnoredExposurePerUpdate;
            if (rad.Accumulates && rad.Total > 0)
                rad.SecondsToCritical = CriticalLevel / rad.Total;

            rad.AtmosphericShielding = Math.Min(1.0, env.ProtectionFactor * env.AirDensity);

            if (airtight) rad.ShelterState = 2;
            else if (!env.SunLosClear) rad.ShelterState = 1;
            else if (rad.AtmosphericShielding >= 1.0) rad.ShelterState = 3;
            else rad.ShelterState = 0;

            return rad;
        }

        // Weather contributes only through an effect's explicit RadiationHazard block.
        // Reading that from the definition at runtime is the correct approach but its
        // whitelist status is unconfirmed, so v1 returns zero and the radiation display
        // notes the limitation rather than silently being wrong.
        private static double ReadWeatherRadiation(IMyCubeBlock block)
        {
            return 0.0;
        }

        public struct Bio
        {
            public bool Valid;
            public int Count;          // organisms only - fauna
            public int Contacts;       // humanoid or robotic; NOT life
            public double Radius;
            public string Nearest;
            public double NearestDist;
            public float NearestBearing;          // degrees, 0-360, in NearestBearingFrame
            public int NearestBearingFrame;       // Readings.Frame* - what 0 means
            public float NearestBearingRel;       // always relative to grid forward
            public List<KeyValuePair<string, int>> Species;   // sorted, most numerous first

            // Diagnostics. A zero count has several possible causes and they are not
            // distinguishable from the count alone.
            public int EntitiesSeen;
            public int CharsSeen;
            public int PlayersSkipped;
            public int PlayerEntries;      // everything GetPlayers returned
            public int HumansOnline;       // of those, the ones with a SteamUserId
            public int DeadSkipped;
            public string Error;
        }

        // Detects living characters and classifies them by definition subtype.
        //
        // Two things learned the hard way, 2026-08-09:
        //
        //  - IMyCharacter.IsBot is FALSE for MES-spawned fauna. Fourteen animals were
        //    present and IsBot reported zero. Filtering on it would exclude every
        //    creature this instrument exists to find. Players are excluded by comparing
        //    against MyAPIGateway.Players instead.
        //  - Definition.Id.SubtypeName does return real subtypes - Horse_Bot, Cow_Bot -
        //    with no cast to MyCharacterDefinition needed.
        //
        // Range is bounded by entity streaming, not by choice: characters beyond sync
        // distance are not streamed and do not exist to query.
        private static readonly List<IMyPlayer> _players = new List<IMyPlayer>();

        // Is this subtype a machine or an armed humanoid rather than wildlife?
        //
        // Matching is on specific names, not on the "_Bot" suffix - the fauna are
        // Cow_Bot, Horse_Bot and Sheep_Bot, so a suffix rule would classify a herd of
        // cows as a combat patrol.
        //
        // An unrecognised subtype stays an ORGANISM. That is the honest default for a
        // biological sensor: it found something alive it cannot name. The failure it
        // must never make is the opposite one - calling five armed soldiers wildlife.
        public static bool IsContact(string subtype)
        {
            if (string.IsNullOrEmpty(subtype)) return false;
            var s = subtype.ToLowerInvariant();

            return s == "police_bot" || s == "boss_bot" || s == "drone_bot"
                || s == "target_dummy" || s.Contains("astronaut") || s.Contains("soldier");
        }

        public static Bio ScanBio(IMyCubeBlock block)
        {
            var bio = new Bio { Species = new List<KeyValuePair<string, int>>(), Nearest = "", NearestDist = -1, NearestBearing = -1 };

            double radius = 3000;
            try { radius = MyAPIGateway.Session.SessionSettings.SyncDistance; } catch { }
            if (radius < 100) radius = 3000;
            bio.Radius = radius;

            Vector3D pos = block.GetPosition();

            _players.Clear();
            try { MyAPIGateway.Players.GetPlayers(_players); } catch { }

            int humans = 0;
            for (int i = 0; i < _players.Count; i++)
                if (!_players[i].IsBot && _players[i].SteamUserId != 0) humans++;
            bio.HumansOnline = humans;
            bio.PlayerEntries = _players.Count;

            var sphere = new BoundingSphereD(pos, radius);
            var nearby = MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere);
            bio.EntitiesSeen = nearby == null ? -1 : nearby.Count;

            var tally = new Dictionary<string, int>();
            double best = double.MaxValue;
            IMyCharacter nearestChar = null;

            foreach (var entity in nearby)
            {
                var c = entity as IMyCharacter;
                if (c == null) continue;
                bio.CharsSeen++;
                if (c.IsDead) { bio.DeadSkipped++; continue; }

                bool isHuman = false;
                for (int i = 0; i < _players.Count; i++)
                {
                    // IMyPlayer.IsBot is the discriminator - note this is NOT
                    // IMyCharacter.IsBot, which reads false for fauna and started
                    // this whole mess. SteamUserId does not work: SE gives bot
                    // players a non-zero id (they share the host's, with a different
                    // serial), so that test classified 29 animals as 29 people.
                    if (_players[i].IsBot || _players[i].SteamUserId == 0) continue;

                    var pc = _players[i].Character;
                    if (pc != null && pc.EntityId == c.EntityId) { isHuman = true; break; }
                }
                if (isHuman) { bio.PlayersSkipped++; continue; }

                string sub;
                try { sub = c.Definition.Id.SubtypeName; }
                catch { sub = "Unknown"; }
                if (string.IsNullOrEmpty(sub)) sub = "Unknown";

                if (IsContact(sub)) bio.Contacts++;
                else bio.Count++;

                int n;
                tally[sub] = tally.TryGetValue(sub, out n) ? n + 1 : 1;

                double d = Vector3D.Distance(pos, c.GetPosition());
                if (d < best) { best = d; bio.Nearest = sub; nearestChar = c; }
            }

            if (best < double.MaxValue)
            {
                bio.NearestDist = best;
                int frame;
                var np = nearestChar.GetPosition();
                bio.NearestBearing = Bearing(block, np, out frame);
                bio.NearestBearingFrame = frame;

                // Relative bearing is published unconditionally beside the absolute one.
                // The stock game surfaces no compass, so a true-north figure is only
                // legible to players running a compass mod - but "45 degrees off your
                // nose" is readable by anyone sitting in a cockpit. Neither is more
                // correct; they answer different questions, so both ship.
                int relFrame;
                bio.NearestBearingRel = BearingFrom(block, np,
                    block.CubeGrid.WorldMatrix.Forward, out relFrame);
            }

            foreach (var kv in tally) bio.Species.Add(kv);
            bio.Species.Sort((a, b2) => b2.Value.CompareTo(a.Value));

            bio.Valid = true;
            return bio;
        }

        // Degrees clockwise from the block's forward, about local up. -1 if undefined.
        // Angle from an explicit reference direction. Shared by both bearing readings
        // so the sign convention can never differ between them.
        private static float BearingFrom(IMyCubeBlock block, Vector3D target, Vector3D reference, out int frame)
        {
            frame = FrameGridForward;
            try
            {
                Vector3D pos = block.GetPosition();
                Vector3D up = block.WorldMatrix.Up;
                float interference;
                var g = MyAPIGateway.Physics.CalculateNaturalGravityAt(pos, out interference);
                if (g.LengthSquared() > 0.000001) up = -Vector3D.Normalize(g);

                Vector3D fwd = reference - up * Vector3D.Dot(reference, up);
                Vector3D to = target - pos;
                to = to - up * Vector3D.Dot(to, up);
                if (fwd.LengthSquared() < 1e-6 || to.LengthSquared() < 1e-6)
                {
                    frame = FrameNone;
                    return -1f;
                }

                fwd = Vector3D.Normalize(fwd);
                to = Vector3D.Normalize(to);
                double ang = Math.Acos(MathHelper.Clamp(Vector3D.Dot(fwd, to), -1.0, 1.0)) * 180.0 / Math.PI;
                if (Vector3D.Dot(Vector3D.Cross(fwd, to), up) < 0) ang = 360.0 - ang;
                return (float)ang;
            }
            catch { frame = FrameNone; return -1f; }
        }

        // Bearing reference frames. Published alongside the angle, because the same
        // number means different things in each and a consumer must be able to tell.
        public const int FrameNone = 0;          // no bearing available
        public const int FramePlanetNorth = 1;   // degrees clockwise from planetary north
        public const int FrameGridForward = 2;   // degrees clockwise from grid forward

        // Degrees clockwise about local up, measured from planetary north when in a
        // gravity well and from grid forward otherwise. -1 if undefined.
        //
        // Block forward was the original reference and was wrong: it is whatever
        // direction the builder happened to mount the antenna, so two instruments on
        // one grid could report different bearings to the same target. Grid forward at
        // least agrees across the grid; planetary north is absolute, so two players in
        // different places agree on what 090 means.
        //
        // North is WORLD +Y projected onto the local horizon - not the planet's own
        // axis. The two agree only when a planet is spawned axis-aligned, which SE
        // normally does but does not guarantee.
        //
        // World +Y is chosen deliberately over the more principled planet axis: the
        // stock game has no compass at all, so the de facto standard is whatever the
        // popular compass mods use, and HUD Compass (1469072169) uses world +Y. A
        // bearing the player can read off their own HUD beats one that is arguably
        // more correct and disagrees with what they see.
        //
        // It is arbitrary either way - SE planets do not rotate, the sun orbits them -
        // so north is a shared convention rather than a physical fact. That is exactly
        // why agreeing with everyone else matters more than deriving it ourselves.
        //
        // Undefined at the poles, where the projection collapses; the frame falls back
        // to grid forward there rather than returning a number that looks authoritative
        // and is noise. Undefined off-planet for the same reason - no horizon.
        private static float Bearing(IMyCubeBlock block, Vector3D target, out int frame)
        {
            frame = FrameNone;
            try
            {
                var m = block.WorldMatrix;
                Vector3D pos = block.GetPosition();

                Vector3D up = m.Up;
                float interference;
                var g = MyAPIGateway.Physics.CalculateNaturalGravityAt(pos, out interference);
                bool inWell = g.LengthSquared() > 0.000001;
                if (inWell) up = -Vector3D.Normalize(g);

                // Reference direction: planetary north if we can establish one.
                Vector3D reference = Vector3D.Zero;

                if (inWell)
                {
                    Vector3D axis = new Vector3D(0, 1, 0);
                    Vector3D north = axis - up * Vector3D.Dot(axis, up);

                    // Near the poles world +Y is parallel to local up and the
                    // projection vanishes. Anything under ~5 degrees of separation
                    // is too unstable to report.
                    if (north.LengthSquared() > 0.0075)
                    {
                        reference = north;
                        frame = FramePlanetNorth;
                    }
                }

                if (frame == FrameNone)
                {
                    reference = block.CubeGrid.WorldMatrix.Forward;
                    frame = FrameGridForward;
                }

                Vector3D fwd = reference - up * Vector3D.Dot(reference, up);
                Vector3D to = target - pos;
                to = to - up * Vector3D.Dot(to, up);
                if (fwd.LengthSquared() < 1e-6 || to.LengthSquared() < 1e-6)
                {
                    frame = FrameNone;
                    return -1f;
                }

                fwd = Vector3D.Normalize(fwd);
                to = Vector3D.Normalize(to);
                double ang = Math.Acos(MathHelper.Clamp(Vector3D.Dot(fwd, to), -1.0, 1.0)) * 180.0 / Math.PI;
                if (Vector3D.Dot(Vector3D.Cross(fwd, to), up) < 0) ang = 360.0 - ang;
                return (float)ang;
            }
            catch { frame = FrameNone; return -1f; }
        }

        // Is this instrument looking into a sealed volume?
        //
        // ASK ABOUT THE CELL THE BLOCK FACES, NOT THE ONE IT OCCUPIES.
        //
        // The first version passed block.Position, which is the cell the block itself
        // fills. There is no room there to be airtight - a block is not air - so the
        // answer was NO in a perfectly sealed base, while the air vents beside it
        // reported pressurised. Reported from the Long Haul server 2026-08-11; it had
        // never worked anywhere, and every earlier test happened to be in a genuinely
        // unsealed space, which is what made it look correct.
        //
        // Vanilla air vents do the same thing this now does: they test the cell in
        // front of themselves.
        //
        // Facing is what makes this usable for BOTH consumers, which want opposite
        // answers from the same call:
        //
        //   Habitat Monitor on a room wall  - faces inward  -> sealed, correct
        //   Radiation Monitor on the hull   - faces outward -> vacuum, correct
        //
        // A neighbour-scan would have been wrong here: a radiation monitor bolted to
        // the outside of a sealed ship touches a pressurised cell on its inner face and
        // would have claimed shelter it does not have.
        /// <summary>
        /// What the pressurisation system says about the volume this block sits in.
        /// Oxygen is -1 and blocks 0 when there is no room at all.
        /// </summary>
        public static void ReadSeal(IMyCubeBlock block, out bool airtight,
                                    out float oxygen, out int roomBlocks)
        {
            airtight = false;
            oxygen = -1f;
            roomBlocks = 0;

            try
            {
                var grid = block.CubeGrid;
                var gas = grid.GasSystem;
                if (gas == null) return;

                // Ask for the ROOM, not for a yes/no.
                //
                // IsRoomAtPositionAirtight is a wrapper that collapses three different
                // situations - no room here, a room that is not sealed, and a sealed
                // room - into one "false", which is why two rounds of testing could not
                // tell them apart. The room object distinguishes them, and OxygenLevel
                // is what the air vents themselves report, so the panel and the vent
                // can no longer disagree without saying why.
                //
                // Cell order matters: the block's own cell first, then the cell it
                // faces. A wall panel occupies a cell that IS part of the room in SE's
                // model - blocks that do not seal a face do not evict the air - and the
                // faced cell covers anything mounted so its own cell is solid.
                var own = block.Position;
                var room = gas.GetOxygenRoomForCubeGridPosition(ref own);

                if (room == null)
                {
                    var faced = block.Position + Base6Directions.GetIntVector(block.Orientation.Forward);
                    room = gas.GetOxygenRoomForCubeGridPosition(ref faced);
                }

                if (room == null) return;

                roomBlocks = room.BlockCount;
                airtight = room.IsAirtight;

                // OxygenLevel is a METHOD taking the grid's cube size, not a property -
                // the room stores an absolute amount and needs the cell size to turn it
                // into a fraction. Reading it as a property is a compile error, which
                // cost a server round trip on 2026-08-12.
                oxygen = room.OxygenLevel(block.CubeGrid.GridSize);
            }
            catch { }
        }

        public static bool IsAirtight(IMyCubeBlock block)
        {
            bool airtight; float oxygen; int blocks;
            ReadSeal(block, out airtight, out oxygen, out blocks);
            return airtight;
        }
    }
}
