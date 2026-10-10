using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace GroundTruth
{
    // The tactical contact picture: what the Rotating Antenna can honestly say about the
    // ships around it. docs/TACTICAL_PLAN.md is the design; this file is Phase 1 of it.
    //
    // THE DISH IS A SIGNALS RECEIVER WITH A SHORT-RANGE RETURN, NOT A HULL RADAR (D1).
    // A grid is a contact if, and only if, it is broadcasting and the dish is inside the
    // broadcast radius, or it is ours, or it is inside the fixed dark-return range.
    // Anything else does not exist to the sensor. A player who switches off every
    // antenna and beacon is hiding, and beyond 2 km the hiding works.
    //
    // THE SCAN BELONGS TO THE SHIP, NOT THE DISH. Every dish on one mechanical grid group
    // shares one table, so two dishes do not scan twice and three panels do not scan at
    // all - they read the table. Range and bearing are therefore NOT stored: they are
    // computed when shown, from whichever dish the reader is looking at.
    //
    // AND IT ONLY SCANS WHILE SOMEBODY IS READING. Every Ground Truth panel's resolver
    // walks its grid and calls StateFor on every block with a role, which puts the dish
    // into the one-second recompute even when nothing displays radar. So recompute alone
    // never scans: a table is scanned only while a consumer has read it in the last few
    // seconds (TableFor). An unwatched dish on a parked ship costs one dictionary lookup.
    // Be clear what "watched" means in practice: once a player has opened the dish's
    // terminal, its info writer stays attached for the session and the one-second loop
    // refreshes it, so it keeps scanning from then on - exactly as every other instrument
    // keeps recomputing once opened. What the rule prevents is a weather panel on the
    // same grid quietly switching the radar on.
    //
    // Everything here rests on probe results, docs/PHASE0_PROBES.md section 7:
    //   - GetEntitiesInSphere returns remote grids on a DS client; Physics.LinearVelocity
    //     is right on them (0.1). Position deltas are NOT: a server correction faked
    //     31 m/s on a parked ship, so velocity is never derived from position here.
    //   - GetGridGroup(Mechanical).GetGrids works and keeps docked ships separate (0.2).
    //   - GetUserRelationToOwner on a block agrees between client and server (0.3).
    //   - Broadcast state, radius and HUD text are readable and sync within 2 s (0.4).
    //   - Projections come back from the sphere query with Physics == null, so they are
    //     filtered here explicitly (0.5).
    public static class Tactical
    {
        // D1 rule 3. A mod constant on purpose: a per-dish setting would let any player
        // type 50 km into it and delete stealth from the game.
        public const double DarkReturnRange = 2000.0;

        // Plan section 6. Two seconds is 200 m at 100 m/s - visible close in, irrelevant
        // at 5 km. A dish setting in a later phase; a constant until there is a setting.
        public const double ScanIntervalSeconds = 2.0;

        // How long a LOST contact keeps its last-known position (plan 5.2).
        public const double RetentionSeconds = 30.0;

        // Plan 5.2 bounds. Both are stated on the readout when they bite.
        public const int MaxContacts = 64;

        // Groups smaller than this are debris, not ships. A loose armour block drifting
        // past at 1 km is not a contact anyone wants listed.
        public const int MinBlocks = 5;

        // A table nobody has read for this long stops being scanned.
        private const double WatchSeconds = 5.0;

        // Per-grid caches are rebuilt on this cadence. Walking every block of every
        // streamed grid each scan would be the most expensive thing in the mod; once in
        // ten seconds is cheap, and broadcaster STATE is still read live every scan, so
        // switching an antenna off is seen at the next scan, not ten seconds later. Only
        // a broadcaster that is newly BUILT can take up to ten seconds to appear.
        private const double GridCacheSeconds = 10.0;

        // Anything untouched for this long is dropped - tables and grid caches both.
        private const double ForgetSeconds = 60.0;

        public const int DetectedByOurs = 1;
        public const int DetectedByBroadcast = 2;
        public const int DetectedByDarkReturn = 3;

        public const int StateDetected = 1;
        public const int StateLost = 2;
        // EXPIRED is not a stored state: an expired contact is removed from the table and
        // nothing keeps a reference to it. TRACKED (a selected contact) arrives in Phase 2.

        // The relationship of a dark return. Not one of the engine's values on purpose:
        // NoOwnership is a measurement ("nobody owns that grid"), and a dark return has
        // not been measured at all.
        public const int RelationUnknown = -1;

        public sealed class Broadcaster
        {
            public long BlockId;
            public string Label;      // exactly what the vanilla HUD shows
            public double Radius;
            public bool IsBeacon;
        }

        public sealed class Contact
        {
            public long Id;              // EntityId of the group's largest member
            public Vector3D Position;    // centre of the group's world bounding box
            public Vector3D Velocity;    // Physics.LinearVelocity of the Id grid
            public bool IsStatic;
            public bool IsLarge;
            public Vector3D Dimensions;  // LocalAABB extents of the Id grid, metres
            public int Blocks;           // whole group
            public int DetectedBy;
            public string Name;          // "" means UNIDENTIFIED
            public int Relation;         // (int)MyRelationsBetweenPlayerAndBlock, or RelationUnknown
            public readonly List<Broadcaster> Broadcasters = new List<Broadcaster>();
            public double FirstSeen;
            public double LastSeen;
            public int State;
        }

        public sealed class Table
        {
            public long Key;
            public readonly Dictionary<long, Contact> Contacts = new Dictionary<long, Contact>();
            public double LastScan = -1;
            public double LastRead = -1;
            public double LastTouched;
            public double ScanRadius;
            public bool Offline;         // the dish that last tried to scan was not working
            public bool LoggedOffline;
            public bool Truncated;       // MaxContacts bit on the last scan
            public bool UnownedDish;     // nothing can be "ours" to a dish nobody owns
            public double LastScanMs;
            public string Error;
        }

        private sealed class GridInfo
        {
            public double Built = -1;
            public double LastTouched;
            public int Blocks;
            public IMyCubeBlock OwnedBlock;   // a block that has an owner, for relations
            public readonly List<IMyTerminalBlock> Broadcasters = new List<IMyTerminalBlock>();
        }

        private static readonly Dictionary<long, Table> _tables = new Dictionary<long, Table>();
        private static readonly Dictionary<long, GridInfo> _grids = new Dictionary<long, GridInfo>();

        // Scratch, reused so a scan allocates as little as it can.
        private static readonly List<IMyCubeGrid> _group = new List<IMyCubeGrid>();
        private static readonly List<IMyCubeGrid> _ownGroup = new List<IMyCubeGrid>();
        private static readonly HashSet<long> _own = new HashSet<long>();
        private static readonly HashSet<long> _visited = new HashSet<long>();
        private static readonly HashSet<long> _seen = new HashSet<long>();
        private static readonly List<IMySlimBlock> _slims = new List<IMySlimBlock>();
        private static readonly List<long> _drop = new List<long>();
        private static readonly List<Contact> _sorted = new List<Contact>();

        public static void Clear()
        {
            _tables.Clear();
            _grids.Clear();
        }

        /// <summary>
        /// The contact table for this dish's ship, for reading. Marks it watched, which is
        /// what makes recompute scan it. Scans at once if it has never been scanned, so
        /// the first look shows contacts instead of an empty list.
        /// </summary>
        public static Table TableFor(IMyTerminalBlock dish, double now)
        {
            if (dish == null || dish.CubeGrid == null) return null;
            var t = TableOf(dish, now);
            t.LastRead = now;
            if (t.LastScan < 0) Scan(dish, t, now);
            return t;
        }

        /// <summary>
        /// Called from the one-second recompute for every dish in the state cache. Scans
        /// only when the table is being watched and the interval has passed.
        /// </summary>
        public static void Update(IMyTerminalBlock dish, double now)
        {
            if (dish == null || dish.CubeGrid == null) return;
            var t = TableOf(dish, now);
            if (t.LastRead < 0 || now - t.LastRead > WatchSeconds) return;
            if (t.LastScan >= 0 && now - t.LastScan < ScanIntervalSeconds) return;
            Scan(dish, t, now);
        }

        /// <summary>Drops tables and grid caches nobody has touched for a minute.</summary>
        public static void Forget(double now)
        {
            _drop.Clear();
            foreach (var kv in _tables)
                if (now - kv.Value.LastTouched > ForgetSeconds) _drop.Add(kv.Key);
            for (int i = 0; i < _drop.Count; i++) _tables.Remove(_drop[i]);

            _drop.Clear();
            foreach (var kv in _grids)
                if (now - kv.Value.LastTouched > ForgetSeconds) _drop.Add(kv.Key);
            for (int i = 0; i < _drop.Count; i++) _grids.Remove(_drop[i]);
        }

        // The table key is the lowest EntityId in the dish's mechanical group. Any stable
        // member would do; the lowest needs no block counts. If the group splits, the
        // halves get new keys and new tables, which is right - they are now two ships.
        private static Table TableOf(IMyTerminalBlock dish, double now)
        {
            MechanicalGroup(dish.CubeGrid, _ownGroup);
            long key = long.MaxValue;
            for (int i = 0; i < _ownGroup.Count; i++)
                if (_ownGroup[i].EntityId < key) key = _ownGroup[i].EntityId;
            if (key == long.MaxValue) key = dish.CubeGrid.EntityId;

            Table t;
            if (!_tables.TryGetValue(key, out t))
            {
                t = new Table { Key = key };
                _tables[key] = t;
            }
            t.LastTouched = now;
            return t;
        }

        private static void MechanicalGroup(IMyCubeGrid grid, List<IMyCubeGrid> into)
        {
            into.Clear();
            try
            {
                var g = grid.GetGridGroup(GridLinkTypeEnum.Mechanical);
                if (g != null) g.GetGrids(into);
            }
            catch { into.Clear(); }
            if (into.Count == 0) into.Add(grid);
        }

        private static void Scan(IMyTerminalBlock dish, Table t, double now)
        {
            t.LastScan = now;
            t.Error = null;
            // Stopwatch, because the probes timed the sphere query with it on a DS and in
            // game (0.36-2.61 ms) - it is proven to load, where a guess might not.
            var watch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                ScanInner(dish, t, now);
            }
            catch (Exception e)
            {
                // A swallowed exception would look exactly like an empty sky. Say so.
                t.Error = e.GetType().Name + ": " + e.Message;
            }

            t.LastScanMs = watch.Elapsed.TotalMilliseconds;
        }

        private static void ScanInner(IMyTerminalBlock dish, Table t, double now)
        {
            _seen.Clear();

            // D0.2: scanning is served behind IsWorking, like every other reading. An
            // offline sensor sees nothing, so its contacts age into LOST and EXPIRED
            // exactly as they would if every ship went silent - which is what it knows.
            t.Offline = !dish.IsWorking;
            long dishOwner = dish.OwnerId;
            t.UnownedDish = dishOwner == 0;

            // Range is the world's VIEW distance (or sync distance, if that is larger).
            //
            // This was sync distance, so two players on a server could never disagree
            // because one happened to stream more. Jason, 2026-10-07: in a full-modpack
            // world with sync 3 km and view 15 km, an IMDC corvette sat on the HUD at 5 km
            // and only reached the radar at 3 km - the sensor was blinder than the
            // helmet. View distance is what the player can already see. In SP and for a
            // host every grid is present, so this is the whole answer there; a dedicated-
            // server client still only sees what it has streamed (TACTICAL_PLAN §10).
            double radius = 3000;
            try
            {
                var settings = MyAPIGateway.Session.SessionSettings;
                radius = Math.Max(settings.ViewDistance, settings.SyncDistance);
            }
            catch { }
            if (radius < 100) radius = 3000;
            t.ScanRadius = radius;

            if (!t.Offline)
            {
                Vector3D dishPos = dish.GetPosition();

                MechanicalGroup(dish.CubeGrid, _ownGroup);
                _own.Clear();
                for (int i = 0; i < _ownGroup.Count; i++) _own.Add(_ownGroup[i].EntityId);

                // The client may hold grids past sync distance (probe 0.1 found one at
                // 12.3 km with sync at 10). The sphere is our range, not "whatever this
                // machine happens to have streamed".
                var sphere = new BoundingSphereD(dishPos, radius);
                var nearby = MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere);

                _visited.Clear();
                for (int n = 0; n < nearby.Count; n++)
                {
                    var grid = nearby[n] as IMyCubeGrid;
                    if (grid == null || grid.Closed || grid.MarkedForClose) continue;
                    if (_visited.Contains(grid.EntityId) || _own.Contains(grid.EntityId)) continue;

                    // Projections come back from the sphere (probe 0.5) and have no
                    // physics. Neither does anything else that is not really there.
                    if (grid.Physics == null) { _visited.Add(grid.EntityId); continue; }

                    MechanicalGroup(grid, _group);
                    bool touchesOwn = false;
                    for (int i = 0; i < _group.Count; i++)
                    {
                        _visited.Add(_group[i].EntityId);
                        if (_own.Contains(_group[i].EntityId)) touchesOwn = true;
                    }
                    if (touchesOwn) continue;

                    Consider(dishPos, dishOwner, t, now);
                }
            }

            Age(t, now, dish.GetPosition());
            Bound(t, dish.GetPosition());

            // One line when the sensor's own state flips, so an offline dish explains a
            // wave of LOST lines rather than looking like every ship went dark at once.
            if (t.Offline != t.LoggedOffline)
            {
                t.LoggedOffline = t.Offline;
                try
                {
                    MyLog.Default.WriteLineAndConsole(string.Format("GT TACTICAL t={0:F0} table={1} SENSOR {2}",
                        now, t.Key, t.Offline ? "OFFLINE (dish not working)" : "ONLINE"));
                }
                catch { }
            }
        }

        // One mechanical group, in _group. Decides whether it is a contact (D1) and what
        // the sensor knows about it (D2).
        private static void Consider(Vector3D dishPos, long dishOwner, Table t, double now)
        {
            IMyCubeGrid main = null;
            int mainBlocks = -1;
            int blocks = 0;
            var box = BoundingBoxD.CreateInvalid();

            for (int i = 0; i < _group.Count; i++)
            {
                var g = _group[i];
                if (g.Physics == null) continue;
                var info = InfoFor(g, now);
                blocks += info.Blocks;
                box.Include(g.WorldAABB);

                // The contact id rule probe 0.2 confirmed: most blocks, ties to the
                // lowest EntityId. On every fixture grid it picked the named hull.
                if (info.Blocks > mainBlocks || (info.Blocks == mainBlocks && main != null && g.EntityId < main.EntityId))
                {
                    main = g;
                    mainBlocks = info.Blocks;
                }
            }
            if (main == null || blocks < MinBlocks) return;

            var mainInfo = InfoFor(main, now);

            // D1, first match wins, in this order.
            int relation = RelationOf(mainInfo, dishOwner);
            bool ours = relation == (int)MyRelationsBetweenPlayerAndBlock.Owner
                     || relation == (int)MyRelationsBetweenPlayerAndBlock.FactionShare;

            var heard = new List<Broadcaster>();
            IMyTerminalBlock loudest = null;
            double loudestRadius = -1;
            for (int i = 0; i < _group.Count; i++)
            {
                GridInfo info;
                if (!_grids.TryGetValue(_group[i].EntityId, out info)) continue;
                for (int b = 0; b < info.Broadcasters.Count; b++)
                {
                    var block = info.Broadcasters[b];
                    double r;
                    if (!IsBroadcastingTo(block, dishPos, out r)) continue;

                    heard.Add(new Broadcaster
                    {
                        BlockId = block.EntityId,
                        Label = HudLabel(block),
                        Radius = r,
                        IsBeacon = block is IMyBeacon
                    });

                    // The contact takes the name of its strongest signal - the one that
                    // reaches furthest, ties to the lowest id so the name never flickers
                    // between two equal antennas from one scan to the next.
                    if (r > loudestRadius || (r == loudestRadius && loudest != null && block.EntityId < loudest.EntityId))
                    {
                        loudest = block;
                        loudestRadius = r;
                    }
                }
            }

            int detectedBy;
            string name;
            int shownRelation;
            if (ours)
            {
                detectedBy = DetectedByOurs;
                name = main.CustomName ?? "";
                shownRelation = relation;
            }
            else if (loudest != null)
            {
                detectedBy = DetectedByBroadcast;
                // The grid's name, under the broadcaster's faction tag. This was the
                // vanilla HUD label (HudLabel), which for a ship broadcasting a bare
                // antenna is just the block name - "GCDW.Antenna" in the target track,
                // which tells a player nothing. Jason's call, 2026-10-06: grid names.
                // The per-broadcaster list below still carries the HUD label.
                name = TaggedGridName(loudest, main);
                // As the HUD colours it: the broadcasting block's owner, not the grid's
                // majority owner. That is who the signal says it is from.
                shownRelation = (int)loudest.GetUserRelationToOwner(dishOwner);
            }
            else if (TacticalMath.WithinRange(dishPos, Nearest(box, dishPos), DarkReturnRange))
            {
                // Measured to the nearest point of the hull's box, not its centre: a
                // 400 m station whose near face is 1.9 km away is inside 2 km.
                detectedBy = DetectedByDarkReturn;
                name = "";
                shownRelation = RelationUnknown;
            }
            else return;

            Contact c;
            string was = null;
            if (!t.Contacts.TryGetValue(main.EntityId, out c))
            {
                c = new Contact { Id = main.EntityId, FirstSeen = now };
                t.Contacts[main.EntityId] = c;
            }
            else was = Summary(c);

            c.Position = box.Center;
            c.Velocity = main.Physics != null ? (Vector3D)main.Physics.LinearVelocity : Vector3D.Zero;
            c.IsStatic = main.IsStatic;
            c.IsLarge = main.GridSizeEnum == MyCubeSize.Large;
            c.Dimensions = main.LocalAABB.Max - main.LocalAABB.Min;
            c.Blocks = blocks;
            c.DetectedBy = detectedBy;
            c.Name = name;
            c.Relation = shownRelation;
            c.Broadcasters.Clear();
            // A dark return carries what a return physically gives, and nothing else
            // (D2). Its broadcasters are not heard, so they are not listed.
            if (detectedBy != DetectedByDarkReturn) c.Broadcasters.AddRange(heard);
            c.LastSeen = now;
            int priorState = c.State;
            c.State = StateDetected;
            _seen.Add(c.Id);

            // Transitions only - a steady contact writes nothing, so the log stays
            // readable through a ten-minute run.
            string isNow = Summary(c);
            if (was == null) Log(t, now, "NEW", c, dishPos);
            else if (priorState == StateLost) Log(t, now, "REGAINED", c, dishPos);
            else if (was != isNow) Log(t, now, "CHANGED", c, dishPos, "was " + was);
        }

        // ---- Lifecycle log ------------------------------------------------------------
        //
        // The pane only helps someone standing at the dish, and the interesting moments -
        // a ship going dark 4 km away, a contact expiring 30 s later - happen while the
        // tester is somewhere else. So every transition is written to the game log with
        // the session clock, and the run is read afterwards:
        //
        //   GT TACTICAL [CLIENT] t=123 table=<key> NEW id=<id> "<name>" by=broadcast rel=NEUTRAL Small ship d=3878
        //
        // NEW, REGAINED, CHANGED (identity or detection rule moved), LOST, and EXPIRED with
        // its reason: retention (30 s lost), closed (the grid is gone), bound (64 kept).
        // The side tag matters on a dedicated server, where client and server both scan.
        //
        // OFF for release (Jason, 2026-10-09): on a busy server every ship coming and going
        // writes a line on every machine with a dish watched. It earned its keep through
        // Phases 1-2 and acceptance; set this true for a debug build. Sensor ONLINE /
        // OFFLINE lines are not affected - they are rare and say why a radar is blank.
        private static readonly bool LogContactTransitions = false;

        private static string Summary(Contact c)
        {
            return DetectedName(c.DetectedBy) + "|" + RelationName(c.Relation) + "|" + (c.Name ?? "");
        }

        private static string DetectedName(int by)
        {
            if (by == DetectedByOurs) return "ours";
            if (by == DetectedByBroadcast) return "broadcast";
            if (by == DetectedByDarkReturn) return "dark";
            return "?";
        }

        private static void Log(Table t, double now, string what, Contact c, Vector3D from, string extra = null)
        {
            if (!LogContactTransitions) return;
            try
            {
                bool server = MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer;
                string name = string.IsNullOrEmpty(c.Name) ? "UNIDENTIFIED" : c.Name;
                MyLog.Default.WriteLineAndConsole(string.Format(
                    "GT TACTICAL [{0}] t={1:F0} table={2} {3} id={4} \"{5}\" by={6} rel={7} {8} blocks={9} d={10:F0} lastSeen={11:F0}{12}",
                    server ? (MyAPIGateway.Utilities.IsDedicated ? "SERVER" : "HOST") : "CLIENT",
                    now, t.Key, what, c.Id, name, DetectedName(c.DetectedBy), RelationName(c.Relation),
                    ClassOf(c), c.Blocks, Vector3D.Distance(from, c.Position), c.LastSeen,
                    extra == null ? "" : " " + extra));
            }
            catch { }
        }

        // Plan 5.2. A contact not seen this scan goes LOST and keeps its last-known
        // position for the retention interval; then it is removed. A contact whose grid
        // no longer exists - ground down, merged, deleted - is removed at once: there is
        // no last-known position to honour for something that is not there.
        private static void Age(Table t, double now, Vector3D dishPos)
        {
            _drop.Clear();
            foreach (var kv in t.Contacts)
            {
                var c = kv.Value;
                if (_seen.Contains(c.Id)) continue;

                var e = MyAPIGateway.Entities.GetEntityById(c.Id);
                if (e == null || e.Closed || e.MarkedForClose)
                {
                    Log(t, now, "EXPIRED", c, dishPos, "reason=closed");
                    _drop.Add(c.Id);
                    continue;
                }

                if (c.State != StateLost) Log(t, now, "LOST", c, dishPos);
                c.State = StateLost;
                if (now - c.LastSeen > RetentionSeconds)
                {
                    Log(t, now, "EXPIRED", c, dishPos, "reason=retention");
                    _drop.Add(c.Id);
                }
            }
            for (int i = 0; i < _drop.Count; i++) t.Contacts.Remove(_drop[i]);
        }

        // Nearest kept. Lost contacts compete on their last-known position.
        private static void Bound(Table t, Vector3D dishPos)
        {
            t.Truncated = false;
            if (t.Contacts.Count <= MaxContacts) return;

            t.Truncated = true;
            _sorted.Clear();
            foreach (var kv in t.Contacts) _sorted.Add(kv.Value);
            _sorted.Sort((a, b) => Vector3D.DistanceSquared(dishPos, a.Position)
                .CompareTo(Vector3D.DistanceSquared(dishPos, b.Position)));
            for (int i = MaxContacts; i < _sorted.Count; i++)
            {
                Log(t, GroundTruthSession.Seconds, "EXPIRED", _sorted[i], dishPos, "reason=bound");
                t.Contacts.Remove(_sorted[i].Id);
            }
            _sorted.Clear();
        }

        private static GridInfo InfoFor(IMyCubeGrid grid, double now)
        {
            GridInfo info;
            if (!_grids.TryGetValue(grid.EntityId, out info))
            {
                info = new GridInfo();
                _grids[grid.EntityId] = info;
            }
            info.LastTouched = now;
            // The representative block going away (ground down, say) forces a rebuild now
            // rather than leaving the relation wrong for up to ten seconds.
            bool ownerGone = info.OwnedBlock != null && info.OwnedBlock.Closed;
            if (info.Built >= 0 && now - info.Built < GridCacheSeconds && !ownerGone) return info;

            info.Built = now;
            info.Blocks = 0;
            info.OwnedBlock = null;
            info.Broadcasters.Clear();

            // The relation is read through a block owned by the grid's MAJORITY owner
            // (BigOwners, read on both sides by probe 0.3). The first owned block found
            // would do on a single-owner grid and be wrong on a mixed one: one block a
            // pirate left on your hull would make your own ship read as a pirate.
            long majority = 0;
            try
            {
                var big = grid.BigOwners;
                if (big != null && big.Count > 0) majority = big[0];
            }
            catch { }

            IMyCubeBlock anyOwned = null;
            _slims.Clear();
            grid.GetBlocks(_slims, null);
            info.Blocks = _slims.Count;
            for (int i = 0; i < _slims.Count; i++)
            {
                var fat = _slims[i].FatBlock;
                if (fat == null) continue;
                if (fat.OwnerId != 0)
                {
                    if (anyOwned == null) anyOwned = fat;
                    if (info.OwnedBlock == null && fat.OwnerId == majority) info.OwnedBlock = fat;
                }
                if (fat is IMyRadioAntenna || fat is IMyBeacon)
                {
                    var tb = fat as IMyTerminalBlock;
                    if (tb != null) info.Broadcasters.Add(tb);
                }
            }
            _slims.Clear();
            if (info.OwnedBlock == null) info.OwnedBlock = anyOwned;
            return info;
        }

        // The relation from the dish's owner to the grid, by way of one block owned by the
        // grid's majority owner - probe 0.3's block route, identical on client and server.
        // An unowned block would say NoOwnership about a grid that is in fact somebody's,
        // so a block WITH an owner is used whenever the grid has one.
        private static int RelationOf(GridInfo info, long dishOwner)
        {
            if (dishOwner == 0 || info.OwnedBlock == null)
                return (int)MyRelationsBetweenPlayerAndBlock.NoOwnership;
            try { return (int)info.OwnedBlock.GetUserRelationToOwner(dishOwner); }
            catch { return (int)MyRelationsBetweenPlayerAndBlock.NoOwnership; }
        }

        // Live state, read every scan - only the LIST of broadcasters is cached.
        private static bool IsBroadcastingTo(IMyTerminalBlock block, Vector3D dishPos, out double radius)
        {
            radius = 0;
            if (block == null || block.Closed || !block.IsWorking) return false;

            var antenna = block as IMyRadioAntenna;
            if (antenna != null)
            {
                // EnableBroadcasting is the player's switch; IsWorking covers power,
                // damage and on/off. Together they are what the HUD acts on.
                if (!antenna.EnableBroadcasting) return false;
                radius = antenna.Radius;
            }
            else
            {
                var beacon = block as IMyBeacon;
                if (beacon == null) return false;
                // A working beacon is a broadcaster, exactly like an antenna with
                // broadcast on (settled 2026-09-29).
                radius = beacon.Radius;
            }

            // D1 rule 1: the OBSERVING dish must be inside the broadcast radius. Vanilla
            // accepts any antenna in your network; this is the stated simplification of
            // plan section 10.
            return TacticalMath.WithinRange(block.GetPosition(), dishPos, radius);
        }

        // Exactly what the vanilla HUD prints for this broadcaster, read from the game's
        // own GetHudParams (MyRadioAntenna, MyBeacon, MyTerminalBlock):
        //
        //   antenna, HUD text set or Show ship name on:
        //       [TAG.][ShipName - ]HudText
        //   beacon, HUD text set:
        //       [TAG.]HudText
        //   otherwise:
        //       [TAG.]CustomName
        //
        // TAG is the owner's faction tag, followed by a dot; an owner with no faction gets
        // neither. Probe 0.4.text saw "GCDW RP-B SIGNAL" on the HUD for HUD text
        // "RP-B SIGNAL" - the dot is small in the HUD font.
        private static string HudLabel(IMyTerminalBlock block)
        {
            var sb = new StringBuilder();
            string tag = FactionTag(block.OwnerId);
            if (!string.IsNullOrEmpty(tag)) sb.Append(tag).Append('.');

            var antenna = block as IMyRadioAntenna;
            var beacon = block as IMyBeacon;
            string hud = antenna != null ? antenna.HudText : (beacon != null ? beacon.HudText : null);
            bool showShip = antenna != null && antenna.ShowShipName;

            if (showShip || !string.IsNullOrEmpty(hud))
            {
                if (showShip) sb.Append(block.CubeGrid.DisplayName).Append(" - ");
                sb.Append(hud ?? "");
            }
            else sb.Append(block.CustomName);

            return sb.ToString();
        }

        // [TAG.]GridName - the tag is the broadcaster's owner's faction, as the HUD
        // would show it, so a contact still says whose signal it is.
        private static string TaggedGridName(IMyTerminalBlock broadcaster, IMyCubeGrid grid)
        {
            string tag = FactionTag(broadcaster.OwnerId);
            string gridName = StripNpcPrefix(grid.CustomName ?? "");
            return string.IsNullOrEmpty(tag) ? gridName : tag + "." + gridName;
        }

        // MES names its spawns "(NPC-IMDC) Protos-Class Corvette Mk.2". Under the faction
        // tag that read "IMDC.(NPC-IMDC) Proto.." on the Target Track - the faction twice
        // and the ship class cut off. The tag already says whose it is, and the relation
        // already says it is not a player, so the prefix goes (Jason, 2026-10-07).
        private static string StripNpcPrefix(string name)
        {
            if (!name.StartsWith("(NPC-", StringComparison.OrdinalIgnoreCase)) return name;
            int close = name.IndexOf(')');
            if (close < 0 || close + 1 >= name.Length) return name;   // nothing left: keep it
            string rest = name.Substring(close + 1).TrimStart();
            return rest.Length == 0 ? name : rest;
        }

        private static string FactionTag(long owner)
        {
            if (owner == 0) return "";
            try
            {
                var f = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owner);
                return f == null ? "" : f.Tag;
            }
            catch { return ""; }
        }

        private static Vector3D Nearest(BoundingBoxD box, Vector3D p)
        {
            return Vector3D.Clamp(p, box.Min, box.Max);
        }

        // ---- Reading a table back, relative to one dish -------------------------------

        /// <summary>The table's contacts, nearest to this dish first, into a caller's list.</summary>
        public static void Sorted(Table t, IMyTerminalBlock dish, List<Contact> into)
        {
            into.Clear();
            if (t == null) return;
            foreach (var kv in t.Contacts) into.Add(kv.Value);
            var p = dish.GetPosition();
            into.Sort((a, b) => Vector3D.DistanceSquared(p, a.Position).CompareTo(Vector3D.DistanceSquared(p, b.Position)));
        }

        // Bearing frames, the same three as Readings: planetary north in a gravity well,
        // grid forward otherwise, none when undefined. North is world +Y on the horizon
        // (ENGINE_TRAPS 7). The ANGLE is clockwise seen from above - a target off the
        // right side reads 90 - tested against VRageMath in TacticalMath's harness.
        public static double Bearing(IMyTerminalBlock dish, Vector3D target, out int frame)
        {
            frame = Readings.FrameNone;
            try
            {
                Vector3D pos = dish.GetPosition();
                Vector3D up;
                float interference;
                var g = MyAPIGateway.Physics.CalculateNaturalGravityAt(pos, out interference);
                if (g.LengthSquared() > 0.000001)
                {
                    up = -Vector3D.Normalize(g);
                    var axis = new Vector3D(0, 1, 0);
                    var north = axis - up * Vector3D.Dot(axis, up);
                    if (north.LengthSquared() > 0.0075)
                    {
                        double b = TacticalMath.Bearing(pos, target, up, north);
                        if (b >= 0) { frame = Readings.FramePlanetNorth; return b; }
                    }
                }

                var m = dish.CubeGrid.WorldMatrix;
                double rel = TacticalMath.Bearing(pos, target, m.Up, m.Forward);
                if (rel >= 0) frame = Readings.FrameGridForward;
                return rel;
            }
            catch { frame = Readings.FrameNone; return -1; }
        }

        /// <summary>Bearing relative to the ship's nose, about the ship's own up. -1 if undefined.</summary>
        public static double RelativeBearing(IMyTerminalBlock dish, Vector3D target)
        {
            try
            {
                var m = dish.CubeGrid.WorldMatrix;
                return TacticalMath.Bearing(dish.GetPosition(), target, m.Up, m.Forward);
            }
            catch { return -1; }
        }

        /// <summary>Degrees above (+) or below (-) the ship's own horizontal plane.</summary>
        public static double Elevation(IMyTerminalBlock dish, Vector3D target)
        {
            try { return TacticalMath.Elevation(dish.GetPosition(), target, dish.CubeGrid.WorldMatrix.Up); }
            catch { return 0; }
        }

        public static double ClosingRate(IMyTerminalBlock dish, Contact c)
        {
            Vector3D own = Vector3D.Zero;
            try
            {
                var phys = dish.CubeGrid.Physics;
                if (phys != null) own = phys.LinearVelocity;
            }
            catch { }
            return TacticalMath.ClosingRate(dish.GetPosition(), own, c.Position, c.Velocity);
        }

        public static string ClassOf(Contact c)
        {
            if (c.IsStatic) return "Station";
            return c.IsLarge ? "Large ship" : "Small ship";
        }

        public static string RelationName(int relation)
        {
            if (relation == RelationUnknown) return "UNKNOWN";
            switch ((MyRelationsBetweenPlayerAndBlock)relation)
            {
                case MyRelationsBetweenPlayerAndBlock.Owner: return "OWNER";
                case MyRelationsBetweenPlayerAndBlock.FactionShare: return "FACTION";
                case MyRelationsBetweenPlayerAndBlock.Friends: return "FRIENDLY";
                case MyRelationsBetweenPlayerAndBlock.Neutral: return "NEUTRAL";
                case MyRelationsBetweenPlayerAndBlock.Enemies: return "ENEMY";
                case MyRelationsBetweenPlayerAndBlock.NoOwnership: return "NOBODY";
                default: return "UNKNOWN";
            }
        }
    }
}
