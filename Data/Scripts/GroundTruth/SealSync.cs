using System;
using System.Collections.Generic;
using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace GroundTruth
{
    // Seal state, from the server that owns it to the clients that draw it.
    //
    // WHY THIS EXISTS
    //
    // Every other reading in this mod is computed where it is displayed. Radiation,
    // weather, sun geometry and life detection all read data a client has, so the whole
    // mod worked in multiplayer without anyone thinking about it.
    //
    // Pressurisation is different, and it took two days and three wrong theories to
    // find out. Measured on a dedicated server 2026-08-13:
    //
    //   - grid.GasSystem is NULL on a client. Not empty - absent.
    //   - IsRoomAtPositionAirtight, Keen's own wrapper, fails there too.
    //   - the SERVER never computes any of our readings, because state is created on
    //     demand and every consumer - panel, terminal property, LCD app - is a client.
    //
    // So the one measurement that exists only on the server was the one nobody was
    // asking the server for. A sealed base reported OPEN, and the radiation model was
    // told its planetary shielding was absent.
    //
    // HOW IT WORKS
    //
    // The server evaluates registered instruments once a second and sends ONLY WHAT
    // CHANGED. Seal state is a boolean that, for a base nobody is shooting at, never
    // changes - so the idle cost is zero packets, not "a small packet". A breach
    // reaches the client in about a tick, which matters, because a depressurisation
    // alarm is the one place latency is felt.
    //
    // A heartbeat every 30 seconds re-sends everything, which covers a joining player,
    // a dropped packet and a client that streamed a grid in late, without anyone having
    // to detect those cases.
    public static class SealSync
    {
        // Arbitrary, ours, and unlikely to collide.
        private const ushort MessageId = 47881;

        private const int HeartbeatSeconds = 30;

        [ProtoContract]
        public class Entry
        {
            [ProtoMember(1)] public long EntityId;
            [ProtoMember(2)] public bool Airtight;
            [ProtoMember(3)] public float Oxygen;
            [ProtoMember(4)] public int RoomBlocks;
            [ProtoMember(5)] public int Status;
        }

        [ProtoContract]
        public class Packet
        {
            [ProtoMember(1)] public List<Entry> Entries;
        }

        // Server: every instrument that exists, registered by its game logic component.
        private static readonly Dictionary<long, IMyCubeBlock> _instruments = new Dictionary<long, IMyCubeBlock>();

        // Server: what we last told the clients, so we can send only differences.
        private static readonly Dictionary<long, Entry> _lastSent = new Dictionary<long, Entry>();

        // Client: what the server last told us.
        private static readonly Dictionary<long, Entry> _received = new Dictionary<long, Entry>();

        private static readonly List<Entry> _changed = new List<Entry>();
        private static readonly List<long> _dead = new List<long>();

        private static bool _handlerRegistered;
        private static int _sinceHeartbeat;

        public static bool IsServer
        {
            get { return MyAPIGateway.Multiplayer == null || MyAPIGateway.Multiplayer.IsServer; }
        }

        public static void Init()
        {
            if (_handlerRegistered) return;
            _handlerRegistered = true;

            // Clients listen. The server registers too and simply never receives, which
            // costs nothing and keeps the single-player path identical.
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(MessageId, OnMessage);
        }

        public static void Close()
        {
            if (_handlerRegistered)
                MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(MessageId, OnMessage);

            _handlerRegistered = false;
            _instruments.Clear();
            _lastSent.Clear();
            _received.Clear();
        }

        // ---- registration, from InstrumentPower on both sides ----

        public static void Register(IMyCubeBlock block)
        {
            if (block == null || !IsServer) return;
            _instruments[block.EntityId] = block;
        }

        public static void Unregister(IMyCubeBlock block)
        {
            if (block == null) return;
            _instruments.Remove(block.EntityId);
            _lastSent.Remove(block.EntityId);
        }

        // ---- client read ----

        public static bool TryGet(long entityId, out bool airtight, out float oxygen,
                                  out int roomBlocks, out int status)
        {
            airtight = false; oxygen = -1f; roomBlocks = 0; status = Readings.SealNoRoom;

            Entry e;
            if (!_received.TryGetValue(entityId, out e)) return false;

            airtight = e.Airtight;
            oxygen = e.Oxygen;
            roomBlocks = e.RoomBlocks;
            status = e.Status;
            return true;
        }

        // ---- server tick, once a second from the session ----

        public static void ServerTick()
        {
            if (!IsServer || MyAPIGateway.Multiplayer == null) return;
            if (MyAPIGateway.Utilities.IsDedicated == false && MyAPIGateway.Multiplayer.MultiplayerActive == false)
                return;   // single player: clients read locally, nothing to send

            _changed.Clear();
            _dead.Clear();
            _sinceHeartbeat++;
            bool heartbeat = _sinceHeartbeat >= HeartbeatSeconds;
            if (heartbeat) _sinceHeartbeat = 0;

            foreach (var kv in _instruments)
            {
                var block = kv.Value;
                if (block == null || block.Closed) { _dead.Add(kv.Key); continue; }

                bool airtight; float oxygen; int blocks; int status;
                Readings.ReadSealLocal(block, out airtight, out oxygen, out blocks, out status);

                Entry last;
                bool known = _lastSent.TryGetValue(kv.Key, out last);

                // Oxygen drifts continuously and is not worth a packet on its own; a
                // change of STATE is. The heartbeat carries fresh numbers anyway.
                bool differs = !known
                            || last.Airtight != airtight
                            || last.Status != status
                            || last.RoomBlocks != blocks;

                if (!differs && !heartbeat) continue;

                var e = new Entry
                {
                    EntityId = kv.Key,
                    Airtight = airtight,
                    Oxygen = oxygen,
                    RoomBlocks = blocks,
                    Status = status
                };
                _lastSent[kv.Key] = e;
                _changed.Add(e);
            }

            for (int i = 0; i < _dead.Count; i++)
            {
                _instruments.Remove(_dead[i]);
                _lastSent.Remove(_dead[i]);
            }

            if (_changed.Count == 0) return;

            try
            {
                var packet = new Packet { Entries = new List<Entry>(_changed) };
                var bytes = MyAPIGateway.Utilities.SerializeToBinary(packet);
                MyAPIGateway.Multiplayer.SendMessageToOthers(MessageId, bytes);
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("GroundTruth SealSync send: " + e);
            }
        }

        private static void OnMessage(ushort id, byte[] data, ulong sender, bool fromServer)
        {
            // Only the server has anything to say about pressurisation. Ignoring
            // everything else means a client cannot spoof another client's readings.
            if (!fromServer) return;

            try
            {
                var packet = MyAPIGateway.Utilities.SerializeFromBinary<Packet>(data);
                if (packet == null || packet.Entries == null) return;

                for (int i = 0; i < packet.Entries.Count; i++)
                {
                    var e = packet.Entries[i];
                    if (e != null) _received[e.EntityId] = e;
                }
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole("GroundTruth SealSync receive: " + e);
            }
        }
    }
}
