using System;
using System.Collections.Generic;

namespace PoolsCoop.Net
{
    /// <summary>
    /// The few Steam calls we need, on the old ISteamNetworking P2P API (the game's own steam_api64.dll has no
    /// NetworkingMessages). Implemented natively in the plugin and by a fake in the tests.
    /// </summary>
    public interface ISteamP2P
    {
        /// <summary>False until Steam is running, logged in and the game has initialised it. Everything else is a no-op until then.</summary>
        bool Ready { get; }

        ulong LocalId { get; }

        /// <summary>Unreliable, no-delay send (our protocol tolerates loss). Creates the session if we are the first to talk.</summary>
        bool Send(ulong peer, byte[] data);

        bool TryReceive(out ulong from, out byte[] data);

        /// <summary>Accept an incoming session from this user. Harmless if there is nothing to accept.</summary>
        void Accept(ulong peer);

        void Close(ulong peer);

        string Describe(ulong peer);
    }

    public interface IDiagnosticSource
    {
        string Describe();
    }

    /// <summary>
    /// ITransport over Steam P2P between known SteamIDs. The host only talks to the SteamIDs it was told to allow;
    /// a client only talks to the host's SteamID. Nothing else gets through, so strangers cannot reach the session.
    /// We cannot see Steam's "session request" callback (the game's own Steam code consumes the callbacks), so
    /// sessions are opened without it: both sides send first, and both keep calling Accept for the peers they expect.
    /// </summary>
    public sealed class SteamTransport : ITransport, IDiagnosticSource
    {
        public const long AcceptEveryMs = 500;
        public const long KnockEveryMs = 2000;
        public const long HeardRecentlyMs = 3000;

        private readonly ISteamP2P _api;
        private readonly IClock _clock;
        private readonly bool _isHost;
        private readonly Dictionary<int, ulong> _peerByConn = new Dictionary<int, ulong>();
        private readonly Dictionary<ulong, int> _connByPeer = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, long> _lastHeard = new Dictionary<ulong, long>();
        private long _lastAcceptMs = -AcceptEveryMs;
        private long _lastKnockMs = -KnockEveryMs;

        public int Unauthorized { get; private set; }
        public int SendFailures { get; private set; }
        public int Received { get; private set; }

        private SteamTransport(ISteamP2P api, IClock clock, bool isHost)
        {
            _api = api;
            _clock = clock;
            _isHost = isHost;
        }

        public static SteamTransport ForClient(ISteamP2P api, IClock clock, ulong hostId)
        {
            var t = new SteamTransport(api, clock, false);
            t._peerByConn[0] = hostId;
            t._connByPeer[hostId] = 0;
            return t;
        }

        public static SteamTransport ForHost(ISteamP2P api, IClock clock, IEnumerable<ulong> allowed)
        {
            var t = new SteamTransport(api, clock, true);
            int conn = 1;
            foreach (ulong id in allowed)
            {
                if (id == 0 || t._connByPeer.ContainsKey(id)) continue;
                t._peerByConn[conn] = id;
                t._connByPeer[id] = conn;
                conn++;
            }
            return t;
        }

        public int PeerCount => _peerByConn.Count;

        private const int MaxHostPeers = 8;

        /// <summary>Host only: let this Steam user in (they asked to join). Returns false when the list is full.</summary>
        public bool AddPeer(ulong id)
        {
            if (!_isHost || id == 0) return false;
            if (_connByPeer.ContainsKey(id)) return true;
            if (_peerByConn.Count >= MaxHostPeers) return false;
            int conn = 1;
            while (_peerByConn.ContainsKey(conn)) conn++;
            _peerByConn[conn] = id;
            _connByPeer[id] = conn;
            return true;
        }

        /// <summary>Host only: this user no longer wants in. Stops accepting and knocking.</summary>
        public void RemovePeer(ulong id)
        {
            if (!_isHost || !_connByPeer.TryGetValue(id, out int conn)) return;
            _connByPeer.Remove(id);
            _peerByConn.Remove(conn);
            _lastHeard.Remove(id);
            try { if (_api.Ready) _api.Close(id); } catch (Exception) { }
        }

        public bool Receive(out int conn, out byte[] data)
        {
            conn = 0;
            data = null;
            if (!_api.Ready) return false;

            long now = _clock.NowMs;
            Tick(now);

            for (int guard = 0; guard < 1024; guard++)
            {
                if (!_api.TryReceive(out ulong from, out byte[] bytes)) return false;
                if (!_connByPeer.TryGetValue(from, out int c))
                {
                    Unauthorized++;
                    continue;
                }
                _lastHeard[from] = now;
                Received++;
                conn = c;
                data = bytes;
                return true;
            }
            return false;
        }

        public void Send(int conn, byte[] data)
        {
            if (!_api.Ready) return;
            if (!_peerByConn.TryGetValue(conn, out ulong peer)) return;
            if (!_api.Send(peer, data)) SendFailures++;
        }

        public void Forget(int conn)
        {
            // The peer list is fixed by configuration, so there is nothing to free.
        }

        public void Dispose()
        {
            try
            {
                if (_api.Ready) foreach (ulong p in _connByPeer.Keys) _api.Close(p);
            }
            catch (Exception) { }
        }

        private void Tick(long now)
        {
            if (now - _lastAcceptMs >= AcceptEveryMs)
            {
                _lastAcceptMs = now;
                foreach (ulong p in _connByPeer.Keys) _api.Accept(p);
            }

            // The host opens a session towards every allowed friend that has not been heard from, so the friend's
            // Hello is not the first packet the host's Steam client has to match to a session.
            if (_isHost && now - _lastKnockMs >= KnockEveryMs)
            {
                _lastKnockMs = now;
                byte[] knock = Wire.EncodePong(0);
                foreach (ulong p in _connByPeer.Keys)
                {
                    if (_lastHeard.TryGetValue(p, out long heard) && now - heard < HeardRecentlyMs) continue;
                    if (!_api.Send(p, knock)) SendFailures++;
                }
            }
        }

        public string Describe()
        {
            if (!_api.Ready) return "steam=not-ready";
            var sb = new System.Text.StringBuilder();
            sb.Append("steam=ready rx=").Append(Received).Append(" unauthorized=").Append(Unauthorized).Append(" sendFail=").Append(SendFailures);
            foreach (ulong p in _connByPeer.Keys) sb.Append(" [").Append(_api.Describe(p)).Append(']');
            return sb.ToString();
        }
    }
}
