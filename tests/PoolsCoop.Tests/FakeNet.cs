using System;
using System.Collections.Generic;
using PoolsCoop.Net;

namespace PoolsCoop.Tests
{
    public sealed class FakeClock : IClock
    {
        public long NowMs { get; set; }
    }

    /// <summary>In-memory network with seeded loss, duplication and random delay (which also reorders).</summary>
    public sealed class FakeHub
    {
        public readonly FakeClock Clock = new FakeClock();
        public readonly Random Rng;
        public double Loss;
        public double Dup;
        public int MinDelayMs;
        public int MaxDelayMs;

        private int _nextTransportId = 1;
        private FakeTransport _host;
        private readonly Dictionary<int, FakeTransport> _clients = new Dictionary<int, FakeTransport>();

        public FakeHub(int seed) { Rng = new Random(seed); }

        public FakeTransport CreateHost()
        {
            _host = new FakeTransport(this, true, _nextTransportId++);
            return _host;
        }

        public FakeTransport CreateClient()
        {
            var c = new FakeTransport(this, false, _nextTransportId++);
            _clients[c.Id] = c;
            return c;
        }

        internal void Route(FakeTransport from, int conn, byte[] data)
        {
            FakeTransport to;
            int arrivesAsConn;
            if (from.IsHost)
            {
                if (!_clients.TryGetValue(conn, out to)) return;
                arrivesAsConn = 0;
            }
            else
            {
                to = _host;
                arrivesAsConn = from.Id;
                if (to == null) return;
            }
            if (Rng.NextDouble() < Loss) return;
            int copies = Rng.NextDouble() < Dup ? 2 : 1;
            for (int i = 0; i < copies; i++)
            {
                int delay = MinDelayMs + (MaxDelayMs > MinDelayMs ? Rng.Next(MaxDelayMs - MinDelayMs + 1) : 0);
                to.Deliver(Clock.NowMs + delay, arrivesAsConn, (byte[])data.Clone());
            }
        }

        /// <summary>Put raw bytes into a transport as if they came from connection <paramref name="conn"/>.</summary>
        public void Inject(FakeTransport to, int conn, byte[] data) => to.Deliver(Clock.NowMs, conn, data);

        public void Run(int ms, params Action[] perStep) => Run(ms, 10, perStep);

        public void Run(int ms, int stepMs, params Action[] perStep)
        {
            long end = Clock.NowMs + ms;
            while (Clock.NowMs < end)
            {
                Clock.NowMs += stepMs;
                foreach (var a in perStep) a();
            }
        }
    }

    public sealed class FakeTransport : ITransport
    {
        private readonly FakeHub _hub;
        private readonly List<(long at, int conn, byte[] data)> _inbox = new List<(long, int, byte[])>();

        public readonly bool IsHost;
        public readonly int Id;
        public readonly HashSet<int> Known = new HashSet<int>();
        public int Forgotten;

        public FakeTransport(FakeHub hub, bool isHost, int id) { _hub = hub; IsHost = isHost; Id = id; }

        internal void Deliver(long at, int conn, byte[] data)
        {
            if (IsHost) Known.Add(conn);
            _inbox.Add((at, conn, data));
        }

        public bool Receive(out int conn, out byte[] data)
        {
            int best = -1;
            for (int i = 0; i < _inbox.Count; i++)
                if (_inbox[i].at <= _hub.Clock.NowMs && (best < 0 || _inbox[i].at < _inbox[best].at)) best = i;
            if (best < 0) { conn = 0; data = null; return false; }
            conn = _inbox[best].conn;
            data = _inbox[best].data;
            _inbox.RemoveAt(best);
            return true;
        }

        public void Send(int conn, byte[] data) => _hub.Route(this, conn, data);

        public void Forget(int conn)
        {
            if (Known.Remove(conn)) Forgotten++;
        }

        public void Dispose() { }
    }
}
