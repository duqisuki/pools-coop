using System;
using System.Collections.Generic;

namespace PoolsCoop.Net
{
    public enum SessionRole { Host, Client }

    public interface IClock
    {
        long NowMs { get; }
    }

    public sealed class StopwatchClock : IClock
    {
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public long NowMs => _sw.ElapsedMilliseconds;
    }

    public sealed class RemotePlayer
    {
        public byte Id;
        public string Name;
        public bool HasPose;
        public PlayerPose Pose;
        public ushort Seq;
        public uint PoseTimeMs;
        public long LastPoseRecvMs;
        public int PosesReceived;

        /// <summary>Smooth, delayed view of this player for drawing; fed from every accepted pose.</summary>
        public readonly PoseBuffer Buffer = new PoseBuffer();
    }

    /// <summary>What this machine knows about one shared rigid body (a door, a duck).</summary>
    public sealed class BodyView
    {
        /// <summary>Nobody owns it: every machine simulates it locally. This is the starting state of every body.</summary>
        public const byte NoOwner = 255;

        public uint Id;
        public byte Owner = NoOwner;
        public ushort Epoch;
        public bool HasState;
        public BodyState State;
        public ushort Seq;
        public ushort SeqEpoch;
        public uint TimeMs;
        public long RecvMs;
        public int StatesReceived;

        // Client side only. A claim or release is applied locally at once (so the toucher's physics reacts without waiting for
        // the network) but the epoch is only ever raised by the host. Until the host's grant arrives, the request is repeated.
        public bool PendingClaim;
        public bool PendingRelease;
        public ushort PendingSeen;
        public long PendingSentMs;
        public int PendingTries;
    }

    /// <summary>
    /// Star topology: one host, up to three clients. The host relays poses between clients and owns the
    /// "which scene are we in" state. Everything here is pure .NET and driven by Update() from the game's main thread.
    /// Pose packets are unreliable and newest-wins; State packets are repeated every second, so no reliability layer is needed.
    /// </summary>
    public sealed class CoopSession : IDisposable
    {
        public const long TimeoutMs = 5000;
        public const long HelloRetryMs = 500;
        public const long StateEveryMs = 1000;
        public const long PingEveryMs = 1000;

        private sealed class Peer
        {
            public int Conn;
            public byte Id;
            public string Name;
            public long LastRecvMs;
        }

        private readonly ITransport _t;
        private readonly IClock _clock;
        private readonly Dictionary<int, Peer> _peers = new Dictionary<int, Peer>();

        private string _localScene = "";
        private ushort _epoch;
        private ushort _poseSeq;
        private long _lastStateMs;
        private long _lastHelloMs = -HelloRetryMs;
        private long _lastPingMs;
        private long _lastHostRecvMs;

        public CoopSession(SessionRole role, ITransport transport, IClock clock, string playerName)
        {
            Role = role;
            _t = transport;
            _clock = clock;
            LocalName = Sanitize(playerName, Wire.MaxName);
            if (LocalName.Length == 0) LocalName = role == SessionRole.Host ? "Host" : "Guest";
            Connected = role == SessionRole.Host;
            _lastHostRecvMs = clock.NowMs;
            _lastStateMs = clock.NowMs;
        }

        /// <summary>The session's own millisecond clock; use it with PoseBuffer.Sample.</summary>
        public long NowMs => _clock.NowMs;

        public SessionRole Role { get; private set; }
        public string LocalName { get; private set; }
        public byte LocalId { get; private set; }
        public bool Connected { get; private set; }
        public string HostScene { get; private set; } = "";
        public double RttMs { get; private set; }
        public int BadPackets { get; private set; }
        public int PacketsIn { get; private set; }
        public int PacketsOut { get; private set; }

        /// <summary>Host only: well-formed non-Hello packets from a connection that is not in the session.</summary>
        public int StrayPackets { get; private set; }

        /// <summary>Host only: pose packets from a client that named some other player as the sender.</summary>
        public int RejectedPoseClaims { get; private set; }

        public readonly Dictionary<byte, RemotePlayer> Remotes = new Dictionary<byte, RemotePlayer>();

        public event Action<RemotePlayer> PlayerJoined;
        public event Action<RemotePlayer> PlayerLeft;
        public event Action<string> HostSceneChanged;

        /// <summary>Fires for every accepted (newest-so-far) pose: remote id, sender's timestamp, pose.</summary>
        public event Action<byte, uint, PlayerPose> PoseReceived;

        // ---- scripted events (scares, triggers) ---------------------------------------------------
        //
        // A trigger that fires for one player (a door slams, something howls) must fire for everybody. The plugin reports the local
        // firing here; every other machine replays it. Packets are unreliable, so each event is sent three times (now, +120 ms, +350 ms)
        // with a per-sender sequence number, and receivers drop repeats of a (sender, sequence) pair they have already seen.
        // The host relays events between clients.

        private const int EventRepeatCount = 5;
        private static readonly long[] EventRepeatAtMs = { 0, 100, 250, 500, 900 };
        private const int MaxEventsPerSecond = 60;

        private ushort _eventSeq;
        private long _eventWindowStart;
        private int _eventWindowCount;
        private readonly List<KeyValuePair<long, byte[]>> _eventRepeats = new List<KeyValuePair<long, byte[]>>();
        private readonly HashSet<uint> _seenEvents = new HashSet<uint>();
        private readonly Queue<uint> _seenOrder = new Queue<uint>();

        public int GameEventsSent { get; private set; }
        public int GameEventsReceived { get; private set; }

        /// <summary>Fires once per remote event: event id, kind, id of the player it fired for.</summary>
        public event Action<uint, byte, byte> GameEventReceived;

        /// <summary>The local player's machine fired a scripted event. Tell everyone else.</summary>
        public void SendGameEvent(uint eventId, byte kind)
        {
            if (!Connected) return;
            long now = _clock.NowMs;
            if (now - _eventWindowStart > 1000) { _eventWindowStart = now; _eventWindowCount = 0; }
            if (++_eventWindowCount > MaxEventsPerSecond) return; // a runaway script must not flood the link
            unchecked { _eventSeq++; }
            GameEventsSent++;
            QueueEvent(Wire.EncodeGameEvent(eventId, kind, LocalId, _eventSeq), now);
        }

        private void QueueEvent(byte[] pkt, long now)
        {
            for (int i = 0; i < EventRepeatCount; i++) _eventRepeats.Add(new KeyValuePair<long, byte[]>(now + EventRepeatAtMs[i], pkt));
        }

        private void PumpEventRepeats(long now)
        {
            if (_eventRepeats.Count == 0) return;
            for (int i = _eventRepeats.Count - 1; i >= 0; i--)
            {
                if (_eventRepeats[i].Key > now) continue;
                byte[] pkt = _eventRepeats[i].Value;
                _eventRepeats.RemoveAt(i);
                if (Role == SessionRole.Host) foreach (var p in _peers.Values) Send(p.Conn, pkt);
                else if (Connected) Send(0, pkt);
            }
        }

        /// <summary>Returns true the first time a (sender, sequence) pair is seen.</summary>
        private bool FirstTimeSeen(byte from, ushort seq)
        {
            uint key = ((uint)from << 16) | seq;
            if (!_seenEvents.Add(key)) return false;
            _seenOrder.Enqueue(key);
            while (_seenOrder.Count > 512) _seenEvents.Remove(_seenOrder.Dequeue());
            return true;
        }

        private void HandleGameEvent(byte senderId, PacketReader r)
        {
            uint id = r.U32();
            byte kind = r.U8();
            byte from = r.U8();
            ushort seq = r.U16();
            if (Role == SessionRole.Host) from = senderId;           // the connection says who really sent it
            else if (from >= Wire.MaxPlayers) return;
            if (from == LocalId && Role == SessionRole.Client) return; // our own event coming back
            if (!FirstTimeSeen(from, seq)) return;
            GameEventsReceived++;
            if (Role == SessionRole.Host)
            {
                // relay to every other client, with the same retry policy
                byte[] pkt = Wire.EncodeGameEvent(id, kind, from, seq);
                foreach (var p in _peers.Values) if (p.Id != senderId) QueueEventTo(p.Conn, pkt, _clock.NowMs);
            }
            GameEventReceived?.Invoke(id, kind, from);
        }

        // ---- one-shot effects (footsteps, splashes) ------------------------------------------------
        //
        // Cosmetic and frequent, so they are sent once with no repeats: a lost footstep is better than a late one.

        private const int MaxFxPerSecond = 40;
        private long _fxWindowStart;
        private int _fxWindowCount;
        public int FxSent { get; private set; }
        public int FxReceived { get; private set; }

        /// <summary>Fires for every effect from another player.</summary>
        public event Action<FxEvent> FxReceivedEvent;

        public void SendFx(FxEvent e)
        {
            if (!Connected || !e.IsSane) return;
            long now = _clock.NowMs;
            if (now - _fxWindowStart > 1000) { _fxWindowStart = now; _fxWindowCount = 0; }
            if (++_fxWindowCount > MaxFxPerSecond) return;
            e.From = LocalId;
            byte[] pkt = Wire.EncodeFx(e);
            FxSent++;
            if (Role == SessionRole.Host) foreach (var p in _peers.Values) Send(p.Conn, pkt);
            else Send(0, pkt);
        }

        private void HandleFx(byte senderId, PacketReader r)
        {
            FxEvent e = Wire.ReadFx(r);
            if (!e.IsSane) { BadPackets++; return; }
            if (Role == SessionRole.Host)
            {
                e.From = senderId; // the connection says who really sent it
                byte[] pkt = Wire.EncodeFx(e);
                foreach (var p in _peers.Values) if (p.Id != senderId) Send(p.Conn, pkt);
            }
            else if (e.From == LocalId || e.From >= Wire.MaxPlayers) return;
            FxReceived++;
            FxReceivedEvent?.Invoke(e);
        }

        private readonly List<KeyValuePair<long, KeyValuePair<int, byte[]>>> _relayRepeats = new List<KeyValuePair<long, KeyValuePair<int, byte[]>>>();

        private void QueueEventTo(int conn, byte[] pkt, long now)
        {
            for (int i = 0; i < EventRepeatCount; i++)
                _relayRepeats.Add(new KeyValuePair<long, KeyValuePair<int, byte[]>>(now + EventRepeatAtMs[i], new KeyValuePair<int, byte[]>(conn, pkt)));
        }

        private void PumpRelayRepeats(long now)
        {
            if (_relayRepeats.Count == 0) return;
            for (int i = _relayRepeats.Count - 1; i >= 0; i--)
            {
                if (_relayRepeats[i].Key > now) continue;
                var item = _relayRepeats[i].Value;
                _relayRepeats.RemoveAt(i);
                Send(item.Key, item.Value);
            }
        }

        // ---- shared bodies (doors, ducks) ---------------------------------------------------------
        //
        // One owner per body, decided by the host, stamped with an epoch (see the playbook's actor-ownership page).
        // A body nobody touched has no owner and every machine simulates it. Touching it claims it; the owner simulates and streams
        // its state, everyone else follows. When it comes to rest the owner releases it. Only the owner at the current epoch may
        // publish; everything else is dropped, so a replayed grant or a past owner's late packets can never win.

        private const int MaxBodies = 4096;
        private int _grantCursor;

        public readonly Dictionary<uint, BodyView> Bodies = new Dictionary<uint, BodyView>();
        public int StaleBodyStates { get; private set; }
        public int BodyStatesAccepted { get; private set; }

        /// <summary>Fires after the owner or epoch of a body changed on this machine (including our own claim and release).</summary>
        public event Action<BodyView> BodyOwnerChanged;

        /// <summary>Fires for every accepted state of a body owned by someone else.</summary>
        public event Action<BodyView> BodyStateReceived;

        private BodyView GetBody(uint id)
        {
            if (Bodies.TryGetValue(id, out BodyView v)) return v;
            if (Bodies.Count >= MaxBodies) return null;
            v = new BodyView { Id = id };
            Bodies[id] = v;
            return v;
        }

        /// <summary>True when we own the body and the host has confirmed it (we may stream its state).</summary>
        public bool IsBodyOwner(uint id) => Bodies.TryGetValue(id, out BodyView v) && v.Owner == LocalId && !v.PendingClaim && Connected;

        /// <summary>The local player touched a body. It behaves as ours at once; the host confirms or overrules.</summary>
        public void ClaimBody(uint id)
        {
            BodyView v = GetBody(id);
            if (v == null || !Connected || v.Owner == LocalId) return;
            if (Role == SessionRole.Host) { GrantOnHost(v, 0); return; }
            v.Owner = LocalId;
            v.PendingClaim = true;
            v.PendingRelease = false;
            v.PendingSeen = v.Epoch;
            v.PendingSentMs = _clock.NowMs;
            v.PendingTries = 0;
            Send(0, Wire.EncodeBodyClaim(id, v.PendingSeen));
            BodyOwnerChanged?.Invoke(v);
        }

        /// <summary>The body we own came to rest. Nobody needs to stream it any more.</summary>
        public void ReleaseBody(uint id)
        {
            if (!Bodies.TryGetValue(id, out BodyView v) || !Connected || v.Owner != LocalId || v.PendingClaim) return;
            if (Role == SessionRole.Host) { GrantOnHost(v, BodyView.NoOwner); return; }
            v.Owner = BodyView.NoOwner;
            v.PendingRelease = true;
            v.PendingSeen = v.Epoch;
            v.PendingSentMs = _clock.NowMs;
            v.PendingTries = 0;
            Send(0, Wire.EncodeBodyRelease(id, v.PendingSeen));
            BodyOwnerChanged?.Invoke(v);
        }

        private const long PendingRetryMs = 300;
        private const int PendingMaxTries = 30;

        private void ResendPendingBodies(long now)
        {
            List<BodyView> giveUp = null;
            foreach (var v in Bodies.Values)
            {
                if (!v.PendingClaim && !v.PendingRelease) continue;
                if (now - v.PendingSentMs < PendingRetryMs) continue;
                if (++v.PendingTries > PendingMaxTries)
                {
                    if (giveUp == null) giveUp = new List<BodyView>();
                    giveUp.Add(v);
                    continue;
                }
                v.PendingSentMs = now;
                Send(0, v.PendingClaim ? Wire.EncodeBodyClaim(v.Id, v.PendingSeen) : Wire.EncodeBodyRelease(v.Id, v.PendingSeen));
            }
            if (giveUp != null)
            {
                foreach (var v in giveUp)
                {
                    bool wasClaim = v.PendingClaim;
                    v.PendingClaim = false;
                    v.PendingRelease = false;
                    if (wasClaim) v.Owner = BodyView.NoOwner; // the host never answered: back to simulating it locally
                    BodyOwnerChanged?.Invoke(v);
                }
            }
        }

        /// <summary>Publish the state of a body we own. Ignored if we are not its owner.</summary>
        public void SendBodyState(uint id, BodyState state, uint timeMs)
        {
            if (!state.IsSane || !IsBodyOwner(id)) return;
            BodyView v = Bodies[id];
            if (v.SeqEpoch != v.Epoch) { v.SeqEpoch = v.Epoch; v.Seq = 0; }
            unchecked { v.Seq++; }
            byte[] pkt = Wire.EncodeBodyState(id, LocalId, v.Epoch, v.Seq, timeMs, state);
            if (Role == SessionRole.Host) foreach (var p in _peers.Values) Send(p.Conn, pkt);
            else Send(0, pkt);
        }

        private void GrantOnHost(BodyView v, byte owner)
        {
            v.Owner = owner;
            unchecked { v.Epoch++; }
            byte[] pkt = Wire.EncodeBodyGrant(v.Id, owner, v.Epoch);
            foreach (var p in _peers.Values) Send(p.Conn, pkt);
            BodyOwnerChanged?.Invoke(v);
        }

        private void SendAllGrants(int conn)
        {
            int n = 0;
            foreach (var v in Bodies.Values)
            {
                if (v.Epoch == 0) continue;
                Send(conn, Wire.EncodeBodyGrant(v.Id, v.Owner, v.Epoch));
                if (++n >= 200) break;
            }
        }

        private void RebroadcastSomeGrants()
        {
            if (_peers.Count == 0 || Bodies.Count == 0) return;
            var list = new List<BodyView>();
            foreach (var v in Bodies.Values) if (v.Epoch != 0) list.Add(v);
            if (list.Count == 0) return;
            for (int i = 0; i < Math.Min(16, list.Count); i++)
            {
                BodyView v = list[(_grantCursor + i) % list.Count];
                byte[] pkt = Wire.EncodeBodyGrant(v.Id, v.Owner, v.Epoch);
                foreach (var p in _peers.Values) Send(p.Conn, pkt);
            }
            _grantCursor = (_grantCursor + 16) % list.Count;
        }

        private void HostHandleBody(Peer peer, MsgType type, PacketReader r)
        {
            switch (type)
            {
                case MsgType.BodyClaim:
                {
                    uint id = r.U32();
                    ushort seen = r.U16();
                    BodyView v = GetBody(id);
                    if (v == null) return;
                    if (v.Owner == peer.Id) { Send(peer.Conn, Wire.EncodeBodyGrant(id, v.Owner, v.Epoch)); return; } // duplicate claim
                    if (seen == v.Epoch) GrantOnHost(v, peer.Id);
                    else Send(peer.Conn, Wire.EncodeBodyGrant(id, v.Owner, v.Epoch)); // they were behind: tell them who owns it
                    break;
                }
                case MsgType.BodyRelease:
                {
                    uint id = r.U32();
                    ushort epoch = r.U16();
                    if (Bodies.TryGetValue(id, out BodyView v) && v.Owner == peer.Id && v.Epoch == epoch) GrantOnHost(v, BodyView.NoOwner);
                    break;
                }
                case MsgType.BodyState:
                {
                    uint id = r.U32();
                    r.U8(); // owner claimed in the packet; the connection decides who really sent it
                    ushort epoch = r.U16();
                    ushort seq = r.U16();
                    uint timeMs = r.U32();
                    BodyState st = r.Body();
                    if (!st.IsSane) { BadPackets++; return; }
                    if (!Bodies.TryGetValue(id, out BodyView v) || v.Owner != peer.Id || v.Epoch != epoch) { StaleBodyStates++; return; }
                    if (!AcceptBodyState(v, seq, timeMs, st)) return;
                    byte[] pkt = Wire.EncodeBodyState(id, peer.Id, epoch, seq, timeMs, st);
                    foreach (var other in _peers.Values) if (other.Conn != peer.Conn) Send(other.Conn, pkt);
                    break;
                }
            }
        }

        private void ClientHandleBody(MsgType type, PacketReader r, long now)
        {
            switch (type)
            {
                case MsgType.BodyGrant:
                {
                    uint id = r.U32();
                    byte owner = r.U8();
                    ushort epoch = r.U16();
                    if (owner != BodyView.NoOwner && owner >= Wire.MaxPlayers) throw new FormatException("bad owner");
                    BodyView v = GetBody(id);
                    if (v == null) return;
                    // Only the host raises epochs, so a strictly newer epoch is always the truth and an equal or older one is a repeat.
                    // A grant that is newer than the epoch our pending claim or release was based on answers that request.
                    if (IsNewer(epoch, v.Epoch))
                    {
                        v.Owner = owner;
                        v.Epoch = epoch;
                        v.PendingClaim = false;
                        v.PendingRelease = false;
                        BodyOwnerChanged?.Invoke(v);
                    }
                    break;
                }
                case MsgType.BodyState:
                {
                    uint id = r.U32();
                    byte owner = r.U8();
                    ushort epoch = r.U16();
                    ushort seq = r.U16();
                    uint timeMs = r.U32();
                    BodyState st = r.Body();
                    if (!st.IsSane) { BadPackets++; return; }
                    if (!Bodies.TryGetValue(id, out BodyView v) || owner == LocalId || v.Owner != owner || v.Epoch != epoch) { StaleBodyStates++; return; }
                    AcceptBodyState(v, seq, timeMs, st);
                    break;
                }
            }
        }

        private bool AcceptBodyState(BodyView v, ushort seq, uint timeMs, BodyState st)
        {
            if (v.SeqEpoch != v.Epoch) { v.SeqEpoch = v.Epoch; v.HasState = false; }
            if (v.HasState && !IsNewer(seq, v.Seq)) { StaleBodyStates++; return false; } // duplicate or reordered older packet
            v.HasState = true;
            v.Seq = seq;
            v.State = st;
            v.TimeMs = timeMs;
            v.RecvMs = _clock.NowMs;
            v.StatesReceived++;
            BodyStatesAccepted++;
            BodyStateReceived?.Invoke(v);
            return true;
        }

        // ---- public API -------------------------------------------------------------------------------

        /// <summary>Host only: tell everyone which scene the host is in.</summary>
        public void SetLocalScene(string scene)
        {
            if (Role != SessionRole.Host) return;
            scene = Sanitize(scene, Wire.MaxScene);
            if (scene == _localScene) return;
            _localScene = scene;
            unchecked { _epoch++; }
            BroadcastState();
        }

        public void SendLocalPose(PlayerPose pose, uint timeMs)
        {
            if (!pose.IsSane) return;
            unchecked { _poseSeq++; }
            if (Role == SessionRole.Host)
            {
                byte[] pkt = Wire.EncodePose(0, _poseSeq, timeMs, pose);
                foreach (var p in _peers.Values) Send(p.Conn, pkt);
            }
            else if (Connected)
            {
                Send(0, Wire.EncodePose(LocalId, _poseSeq, timeMs, pose));
            }
        }

        public void Update()
        {
            long now = _clock.NowMs;

            int guard = 0;
            while (guard++ < 1024 && _t.Receive(out int conn, out byte[] data))
            {
                PacketsIn++;
                try
                {
                    Handle(conn, data, now);
                }
                catch (Exception)
                {
                    BadPackets++;
                    if (Role == SessionRole.Host && !_peers.ContainsKey(conn)) _t.Forget(conn);
                }
            }

            PumpEventRepeats(now);
            PumpRelayRepeats(now);
            if (Role == SessionRole.Host) HostTick(now);
            else ClientTick(now);
        }

        public void Dispose()
        {
            try
            {
                byte[] bye = Wire.EncodeBye();
                if (Role == SessionRole.Host) foreach (var p in _peers.Values) Send(p.Conn, bye);
                else if (Connected) Send(0, bye);
            }
            catch (Exception) { }
            _t.Dispose();
        }

        // ---- shared -----------------------------------------------------------------------------------

        private void Send(int conn, byte[] data)
        {
            PacketsOut++;
            _t.Send(conn, data);
        }

        private void Handle(int conn, byte[] data, long now)
        {
            if (!Wire.TryOpen(data, data.Length, out MsgType type, out PacketReader r))
            {
                BadPackets++;
                if (Role == SessionRole.Host && !_peers.ContainsKey(conn)) _t.Forget(conn);
                return;
            }
            if (Role == SessionRole.Host) HostHandle(conn, type, r, now);
            else ClientHandle(conn, type, r, now);
        }

        private static bool IsNewer(ushort a, ushort b) => (short)(a - b) > 0;

        internal static string Sanitize(string s, int max)
        {
            if (s == null) return "";
            var sb = new System.Text.StringBuilder(Math.Min(s.Length, max));
            foreach (char c in s)
            {
                if (sb.Length >= max) break;
                if (c < 32 || c == 127) continue;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        private List<PlayerInfo> PlayerList()
        {
            var list = new List<PlayerInfo>(Wire.MaxPlayers) { new PlayerInfo(0, LocalName) };
            foreach (var p in _peers.Values) list.Add(new PlayerInfo(p.Id, p.Name));
            return list;
        }

        // ---- host -------------------------------------------------------------------------------------

        private void BroadcastState()
        {
            byte[] pkt = Wire.EncodeState(_localScene, _epoch, PlayerList());
            foreach (var p in _peers.Values) Send(p.Conn, pkt);
            _lastStateMs = _clock.NowMs;
        }

        private int FreeId()
        {
            for (int id = 1; id < Wire.MaxPlayers; id++)
            {
                bool used = false;
                foreach (var p in _peers.Values) if (p.Id == id) { used = true; break; }
                if (!used) return id;
            }
            return -1;
        }

        private byte[] WelcomeFor(Peer p) => Wire.EncodeWelcome(p.Id, (byte)Wire.MaxPlayers, _localScene);

        private void HostHandle(int conn, MsgType type, PacketReader r, long now)
        {
            _peers.TryGetValue(conn, out Peer peer);

            if (type == MsgType.Hello)
            {
                string name = Sanitize(r.Str(), Wire.MaxName);
                if (peer != null)
                {
                    peer.LastRecvMs = now;
                    Send(conn, WelcomeFor(peer)); // our Welcome may have been lost
                    return;
                }
                int id = FreeId();
                if (id < 0)
                {
                    Send(conn, Wire.EncodeBye());
                    _t.Forget(conn);
                    return;
                }
                if (name.Length == 0) name = "Player" + id;
                peer = new Peer { Conn = conn, Id = (byte)id, Name = name, LastRecvMs = now };
                _peers[conn] = peer;
                var rp = new RemotePlayer { Id = peer.Id, Name = peer.Name };
                Remotes[peer.Id] = rp;
                Send(conn, WelcomeFor(peer));
                SendAllGrants(conn); // the newcomer must learn who owns what, including bodies released at a higher epoch
                BroadcastState();
                PlayerJoined?.Invoke(rp);
                return;
            }

            if (peer == null)
            {
                // A well-formed packet from someone who is not (or no longer) in the session, e.g. a packet still in flight
                // after a timeout, or an echo of our own packet. Not malformed, so not "bad"; just do not keep a slot for it.
                StrayPackets++;
                _t.Forget(conn);
                return;
            }

            peer.LastRecvMs = now;
            switch (type)
            {
                case MsgType.Pose:
                {
                    byte claimed = r.U8();
                    if (claimed != peer.Id)
                    {
                        // A client may only send poses as itself. This also drops our own packets if a transport ever
                        // echoes them back (same Steam account on both sides in a lab run).
                        RejectedPoseClaims++;
                        return;
                    }
                    ushort seq = r.U16();
                    uint timeMs = r.U32();
                    PlayerPose pose = r.Player();
                    if (!pose.IsSane) { BadPackets++; return; }
                    if (!AcceptPose(peer.Id, seq, timeMs, pose, now)) return;
                    byte[] pkt = Wire.EncodePose(peer.Id, seq, timeMs, pose);
                    foreach (var other in _peers.Values)
                        if (other.Conn != conn) Send(other.Conn, pkt);
                    break;
                }
                case MsgType.Ping:
                    Send(conn, Wire.EncodePong(r.U32()));
                    break;
                case MsgType.Bye:
                    RemovePeer(peer);
                    break;
                case MsgType.BodyClaim:
                case MsgType.BodyRelease:
                case MsgType.BodyState:
                    HostHandleBody(peer, type, r);
                    break;
                case MsgType.GameEvent:
                    HandleGameEvent(peer.Id, r);
                    break;
                case MsgType.Fx:
                    HandleFx(peer.Id, r);
                    break;
                default:
                    break; // Welcome/State/Pong/BodyGrant are not meant for the host; ignore
            }
        }

        private void RemovePeer(Peer peer)
        {
            _peers.Remove(peer.Conn);
            _t.Forget(peer.Conn);
            if (Remotes.TryGetValue(peer.Id, out RemotePlayer rp))
            {
                Remotes.Remove(peer.Id);
                PlayerLeft?.Invoke(rp);
            }
            List<BodyView> orphans = null;
            foreach (var b in Bodies.Values)
                if (b.Owner == peer.Id) { if (orphans == null) orphans = new List<BodyView>(); orphans.Add(b); }
            if (orphans != null) foreach (var b in orphans) GrantOnHost(b, BodyView.NoOwner); // the owner left: the body is free again
            BroadcastState();
        }

        private void HostTick(long now)
        {
            List<Peer> dead = null;
            foreach (var p in _peers.Values)
            {
                if (now - p.LastRecvMs > TimeoutMs)
                {
                    if (dead == null) dead = new List<Peer>();
                    dead.Add(p);
                }
            }
            if (dead != null) foreach (var p in dead) RemovePeer(p);

            if (now - _lastStateMs >= StateEveryMs && _peers.Count > 0)
            {
                BroadcastState();
                RebroadcastSomeGrants(); // a lost grant must not leave anyone with a stale idea of an owner
            }
        }

        // ---- client -----------------------------------------------------------------------------------

        private void ClientHandle(int conn, MsgType type, PacketReader r, long now)
        {
            if (conn != 0) return;
            _lastHostRecvMs = now;

            switch (type)
            {
                case MsgType.Welcome:
                {
                    byte id = r.U8();
                    r.U8(); // max players
                    string scene = Sanitize(r.Str(), Wire.MaxScene);
                    if (id == 0 || id >= Wire.MaxPlayers) throw new FormatException("bad player id");
                    LocalId = id;
                    Connected = true;
                    SetHostScene(scene);
                    break;
                }
                case MsgType.State:
                {
                    string scene = Sanitize(r.Str(), Wire.MaxScene);
                    r.U16(); // epoch (kept for later conflict handling)
                    int count = r.U8();
                    if (count > Wire.MaxPlayers) throw new FormatException("too many players");
                    var infos = new List<PlayerInfo>(count);
                    for (int i = 0; i < count; i++) infos.Add(new PlayerInfo(r.U8(), Sanitize(r.Str(), Wire.MaxName)));
                    if (!Connected) break; // wait for Welcome so LocalId is known
                    ApplyPlayerList(infos);
                    SetHostScene(scene);
                    break;
                }
                case MsgType.Pose:
                {
                    byte id = r.U8();
                    ushort seq = r.U16();
                    uint timeMs = r.U32();
                    PlayerPose pose = r.Player();
                    if (!Connected || id == LocalId || id >= Wire.MaxPlayers) break;
                    if (!pose.IsSane) { BadPackets++; break; }
                    if (!Remotes.ContainsKey(id))
                    {
                        var rp = new RemotePlayer { Id = id, Name = "Player" + id };
                        Remotes[id] = rp;
                        PlayerJoined?.Invoke(rp);
                    }
                    AcceptPose(id, seq, timeMs, pose, now);
                    break;
                }
                case MsgType.Pong:
                {
                    uint echo = r.U32();
                    double sample = (uint)(now & 0xFFFFFFFF) - echo;
                    if (sample >= 0 && sample < 60000) RttMs = RttMs == 0 ? sample : RttMs * 0.8 + sample * 0.2;
                    break;
                }
                case MsgType.Bye:
                    Disconnect();
                    break;
                case MsgType.BodyGrant:
                case MsgType.BodyState:
                    if (Connected) ClientHandleBody(type, r, now);
                    break;
                case MsgType.GameEvent:
                    if (Connected) HandleGameEvent(0, r);
                    break;
                case MsgType.Fx:
                    if (Connected) HandleFx(0, r);
                    break;
                default:
                    break;
            }
        }

        private void SetHostScene(string scene)
        {
            if (scene == HostScene) return;
            HostScene = scene;
            HostSceneChanged?.Invoke(scene);
        }

        private void ApplyPlayerList(List<PlayerInfo> infos)
        {
            var present = new HashSet<byte>();
            foreach (var info in infos)
            {
                if (info.Id == LocalId || info.Id >= Wire.MaxPlayers) continue;
                present.Add(info.Id);
                string name = info.Name.Length > 0 ? info.Name : "Player" + info.Id;
                if (Remotes.TryGetValue(info.Id, out RemotePlayer existing))
                {
                    existing.Name = name;
                }
                else
                {
                    var rp = new RemotePlayer { Id = info.Id, Name = name };
                    Remotes[info.Id] = rp;
                    PlayerJoined?.Invoke(rp);
                }
            }
            List<byte> gone = null;
            foreach (var id in Remotes.Keys)
                if (!present.Contains(id)) { if (gone == null) gone = new List<byte>(); gone.Add(id); }
            if (gone != null)
            {
                foreach (var id in gone)
                {
                    RemotePlayer rp = Remotes[id];
                    Remotes.Remove(id);
                    PlayerLeft?.Invoke(rp);
                }
            }
        }

        private void Disconnect()
        {
            Connected = false;
            LocalId = 0;
            RttMs = 0;
            var all = new List<RemotePlayer>(Remotes.Values);
            Remotes.Clear();
            foreach (var rp in all) PlayerLeft?.Invoke(rp);
            // Lost the host: every body goes back to being simulated locally.
            foreach (var b in new List<BodyView>(Bodies.Values))
            {
                b.Owner = BodyView.NoOwner;
                BodyOwnerChanged?.Invoke(b);
            }
            Bodies.Clear();
            _lastHelloMs = -HelloRetryMs;
        }

        private void ClientTick(long now)
        {
            if (!Connected)
            {
                if (now - _lastHelloMs >= HelloRetryMs)
                {
                    _lastHelloMs = now;
                    Send(0, Wire.EncodeHello(LocalName));
                }
                return;
            }

            if (now - _lastHostRecvMs > TimeoutMs)
            {
                Disconnect();
                return;
            }

            if (now - _lastPingMs >= PingEveryMs)
            {
                _lastPingMs = now;
                Send(0, Wire.EncodePing((uint)(now & 0xFFFFFFFF)));
            }

            ResendPendingBodies(now);
        }

        // ---- poses ------------------------------------------------------------------------------------

        private bool AcceptPose(byte id, ushort seq, uint timeMs, PlayerPose pose, long now)
        {
            if (!Remotes.TryGetValue(id, out RemotePlayer rp)) return false;
            if (rp.HasPose && !IsNewer(seq, rp.Seq)) return false; // duplicate or reordered older packet
            rp.HasPose = true;
            rp.Pose = pose;
            rp.Seq = seq;
            rp.PoseTimeMs = timeMs;
            rp.LastPoseRecvMs = now;
            rp.PosesReceived++;
            rp.Buffer.Add(timeMs, now, pose);
            PoseReceived?.Invoke(id, timeMs, pose);
            return true;
        }
    }
}
