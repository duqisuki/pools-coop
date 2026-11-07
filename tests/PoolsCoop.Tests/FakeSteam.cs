using System;
using System.Collections.Generic;
using PoolsCoop.Net;

namespace PoolsCoop.Tests
{
    /// <summary>How the fake Steam treats Accept. Real Steam's exact behaviour for a polled Accept is not known, so the transport must work under all three.</summary>
    public enum AcceptMode
    {
        /// <summary>Accept(peer) opens a session at any time.</summary>
        Lenient,
        /// <summary>Accept(peer) works only if that peer's packet arrived in the last 5 s (a request is pending).</summary>
        PendingOnly,
        /// <summary>Accept never works; a session exists only if we sent to the peer first.</summary>
        SendFirstOnly,
    }

    public sealed class FakeSteamNet
    {
        public readonly FakeClock Clock = new FakeClock();
        public readonly Random Rng;
        public double Loss;
        public double Dup;
        public int MaxDelayMs;
        public AcceptMode Mode = AcceptMode.Lenient;
        public readonly Dictionary<ulong, FakeSteamUser> Users = new Dictionary<ulong, FakeSteamUser>();

        public FakeSteamNet(int seed) { Rng = new Random(seed); }

        public FakeSteamUser AddUser(ulong id, bool ready = true)
        {
            var u = new FakeSteamUser(this, id) { Ready = ready };
            Users[id] = u;
            return u;
        }

        internal void Route(ulong from, ulong to, byte[] data)
        {
            if (!Users.TryGetValue(to, out FakeSteamUser dest)) return;
            if (Rng.NextDouble() < Loss) return;
            int copies = Rng.NextDouble() < Dup ? 2 : 1;
            for (int i = 0; i < copies; i++)
            {
                int delay = MaxDelayMs > 0 ? Rng.Next(MaxDelayMs + 1) : 0;
                dest.Arrive(Clock.NowMs + delay, from, (byte[])data.Clone());
            }
        }
    }

    /// <summary>Shared friends graph and rich-presence store for the invite tests.</summary>
    public sealed class FakeSocialWorld
    {
        public readonly Dictionary<ulong, Dictionary<string, string>> Rich = new Dictionary<ulong, Dictionary<string, string>>();
        public readonly Dictionary<ulong, HashSet<ulong>> FriendsOf = new Dictionary<ulong, HashSet<ulong>>();
        public readonly HashSet<ulong> InGame = new HashSet<ulong>();
        public readonly Dictionary<ulong, string> Names = new Dictionary<ulong, string>();

        public void Befriend(ulong a, ulong b)
        {
            if (!FriendsOf.ContainsKey(a)) FriendsOf[a] = new HashSet<ulong>();
            if (!FriendsOf.ContainsKey(b)) FriendsOf[b] = new HashSet<ulong>();
            FriendsOf[a].Add(b);
            FriendsOf[b].Add(a);
        }

        public FakeSocial UserOf(ulong id, string name)
        {
            Names[id] = name;
            InGame.Add(id);
            return new FakeSocial(this, id);
        }
    }

    public sealed class FakeSocial : ISteamSocial
    {
        private readonly FakeSocialWorld _w;
        public FakeSocial(FakeSocialWorld w, ulong id) { _w = w; LocalId = id; }
        public bool Ready { get; set; } = true;
        public ulong LocalId { get; }
        public string LocalName => _w.Names[LocalId];
        public IList<ulong> Friends() => _w.FriendsOf.TryGetValue(LocalId, out var s) ? new List<ulong>(s) : new List<ulong>();
        public string FriendName(ulong id) => _w.Names.TryGetValue(id, out var n) ? n : "?";
        public bool FriendInThisGame(ulong id) => _w.InGame.Contains(id);
        public string GetRich(ulong id, string key) => _w.Rich.TryGetValue(id, out var d) && d.TryGetValue(key, out var v) ? v : "";
        public void RequestRich(ulong id) { }
        public void SetRich(string key, string value)
        {
            if (!_w.Rich.TryGetValue(LocalId, out var d)) { d = new Dictionary<string, string>(); _w.Rich[LocalId] = d; }
            if (string.IsNullOrEmpty(value)) d.Remove(key); else d[key] = value;
        }
    }

    public sealed class FakeSteamUser : ISteamP2P
    {
        private readonly FakeSteamNet _net;
        private readonly HashSet<ulong> _sessions = new HashSet<ulong>();
        private readonly Dictionary<ulong, long> _pending = new Dictionary<ulong, long>();
        private readonly List<(long at, ulong from, byte[] data)> _inbox = new List<(long, ulong, byte[])>();

        public bool Ready { get; set; }
        public ulong LocalId { get; }
        public int DroppedNoSession;

        public FakeSteamUser(FakeSteamNet net, ulong id) { _net = net; LocalId = id; }

        public bool Send(ulong peer, byte[] data)
        {
            if (!Ready) return false;
            _sessions.Add(peer); // sending first opens our side of the session
            _net.Route(LocalId, peer, data);
            return true;
        }

        internal void Arrive(long at, ulong from, byte[] data)
        {
            if (!Ready) { DroppedNoSession++; return; }
            if (_sessions.Contains(from))
            {
                _inbox.Add((at, from, data));
                return;
            }
            // No session: Steam raises a request (which only the game's own code would see) and drops the packet.
            _pending[from] = _net.Clock.NowMs;
            DroppedNoSession++;
        }

        public bool TryReceive(out ulong from, out byte[] data)
        {
            int best = -1;
            for (int i = 0; i < _inbox.Count; i++)
                if (_inbox[i].at <= _net.Clock.NowMs && (best < 0 || _inbox[i].at < _inbox[best].at)) best = i;
            if (best < 0) { from = 0; data = null; return false; }
            from = _inbox[best].from;
            data = _inbox[best].data;
            _inbox.RemoveAt(best);
            return true;
        }

        public void Accept(ulong peer)
        {
            if (!Ready) return;
            switch (_net.Mode)
            {
                case AcceptMode.Lenient:
                    _sessions.Add(peer);
                    break;
                case AcceptMode.PendingOnly:
                    if (_pending.TryGetValue(peer, out long t) && _net.Clock.NowMs - t <= 5000) _sessions.Add(peer);
                    break;
                default:
                    break;
            }
        }

        public void Close(ulong peer) => _sessions.Remove(peer);

        public string Describe(ulong peer) => $"session={_sessions.Contains(peer)}";
    }
}
