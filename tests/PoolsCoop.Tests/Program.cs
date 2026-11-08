using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PoolsCoop.Core;
using PoolsCoop.Net;

namespace PoolsCoop.Tests
{
    internal static class Program
    {
        private static int _checks;
        private static int _failures;

        private static void Check(bool ok, string what)
        {
            _checks++;
            if (!ok)
            {
                _failures++;
                Console.WriteLine("    FAIL: " + what);
            }
        }

        private static void Test(string name, Action body)
        {
            Console.WriteLine("- " + name);
            try { body(); }
            catch (Exception e)
            {
                _failures++;
                Console.WriteLine("    EXCEPTION: " + e);
            }
        }

        private static int Main()
        {
            Test("wire: pose round trip is bit exact, bad input is rejected", WireRoundTrip);
            Test("wire: every truncated prefix is rejected without throwing out of TryOpen", WireTruncation);
            Test("pose buffer: accurate, smooth and fresh under jitter, loss, duplicates and a sender stall", PoseBufferQuality);
            Test("handshake: client joins, ids and names are known on both sides", Handshake);
            Test("poses: both directions plus relay through the host, ids never mixed up", PosesAndRelay);
            Test("chaos: 30% loss, 20% duplicates, up to 250 ms jitter and reordering", Chaos);
            Test("scene: host scene reaches clients, also with 50% loss", SceneSync);
            Test("timeouts: crashed client is dropped, restarted host is rejoined", TimeoutsAndReconnect);
            Test("impersonation: a client cannot send poses as another player or as the host", Impersonation);
            Test("bodies: claim, conflict, stale states and grants, release, owner leaves, late join", BodyOwnership);
            Test("bodies: 30% loss + duplicates + jitter, random claims and releases, every view converges", BodyChaos);
            Test("events: scripted events reach everyone exactly once through loss, duplicates and relay", GameEvents);
            Test("pose extension: finger bends, skin, flags round trip, old packets still read, relay and buffer keep them", PoseExtension);
            Test("effects: footsteps and splashes reach everyone once, sender id enforced, rate limited", Effects);
            Test("capacity: the fourth guest is turned away", Capacity);
            Test("fuzz: random and mutated packets never crash a session and never leak slots", Fuzz);
            Test("udp: real sockets on loopback, handshake and poses", RealUdp);
            Test("steam: connects and syncs under all three Accept behaviours, clean and with chaos", SteamAllModes);
            Test("steam: strangers cannot reach the session", SteamStrangers);
            Test("steam: waits for Steam to become ready on both sides", SteamNotReady);
            Test("steam: junk from an allowed peer is counted, not fatal, and the session recovers", SteamJunk);
            Test("steam: a restarted client gets back in", SteamClientRestart);
            Test("invites: hosting friends are listed, one-button join, spoofs and strangers are ignored", InviteLobby);
            Test("invites: end to end, host starts, friend presses join, session connects over fake Steam", InviteEndToEnd);

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? $"ALL OK ({_checks} checks)"
                : $"{_failures} FAILURE(S) out of {_checks} checks");
            return _failures == 0 ? 0 : 1;
        }

        // ---- helpers -------------------------------------------------------------------------------

        private sealed class Node
        {
            public CoopSession S;
            public FakeTransport T;
            public int Ok;
            public int Bad;
            public readonly Dictionary<byte, uint> LastTime = new Dictionary<byte, uint>();
            public int NonMonotonic;
        }

        private static Node MakeNode(FakeHub hub, SessionRole role, string name, FakeTransport t = null)
        {
            var n = new Node();
            n.T = t ?? (role == SessionRole.Host ? hub.CreateHost() : hub.CreateClient());
            n.S = new CoopSession(role, n.T, hub.Clock, name);
            Hook(n);
            return n;
        }

        private static Node MakeNodeWith(SessionRole role, string name, ITransport transport, IClock clock)
        {
            var n = new Node();
            n.S = new CoopSession(role, transport, clock, name);
            Hook(n);
            return n;
        }

        private static void Hook(Node n)
        {
            n.S.PoseReceived += (id, timeMs, pose) =>
            {
                var expect = BotPose.At(timeMs, id);
                if (pose.MaxPosDistance(expect) < 1e-5f) n.Ok++; else n.Bad++;
                if (n.LastTime.TryGetValue(id, out uint last) && timeMs <= last) n.NonMonotonic++;
                n.LastTime[id] = timeMs;
            };
        }

        private static Action Pump(Node n) => () => n.S.Update();

        private static Action Sender(FakeHub hub, Node n) => Sender(hub.Clock, n);

        /// <summary>Every 33 ms: send a bot pose from this node (once it has an id).</summary>
        private static Action Sender(FakeClock clock, Node n)
        {
            long next = 0;
            return () =>
            {
                if (clock.NowMs < next) return;
                next = clock.NowMs + 33;
                if (!n.S.Connected) return;
                uint t = (uint)clock.NowMs;
                n.S.SendLocalPose(BotPose.At(t, n.S.LocalId), t);
            };
        }

        private static void RunSteam(FakeSteamNet net, int ms, params Action[] steps)
        {
            long end = net.Clock.NowMs + ms;
            while (net.Clock.NowMs < end)
            {
                net.Clock.NowMs += 10;
                foreach (var a in steps) a();
            }
        }

        private sealed class SteamPairSetup
        {
            public FakeSteamNet Net;
            public FakeSteamUser HostUser, ClientUser;
            public SteamTransport HostT, ClientT;
            public Node Host, Client;
        }

        private const ulong HostSteamId = 76561198000000100UL;
        private const ulong ClientSteamId = 76561198000000200UL;

        private static SteamPairSetup SteamPair(int seed, AcceptMode mode, double loss, double dup, int delayMs)
        {
            var s = new SteamPairSetup();
            s.Net = new FakeSteamNet(seed) { Mode = mode, Loss = loss, Dup = dup, MaxDelayMs = delayMs };
            s.HostUser = s.Net.AddUser(HostSteamId);
            s.ClientUser = s.Net.AddUser(ClientSteamId);
            s.HostT = SteamTransport.ForHost(s.HostUser, s.Net.Clock, new[] { ClientSteamId });
            s.ClientT = SteamTransport.ForClient(s.ClientUser, s.Net.Clock, HostSteamId);
            s.Host = MakeNodeWith(SessionRole.Host, "Alice", s.HostT, s.Net.Clock);
            s.Client = MakeNodeWith(SessionRole.Client, "Bob", s.ClientT, s.Net.Clock);
            return s;
        }

        // ---- tests ---------------------------------------------------------------------------------

        private static void WireRoundTrip()
        {
            var p = BotPose.At(12345, 2);
            Check(p.IsSane, "bot pose is sane");
            byte[] bytes = Wire.EncodePose(3, 777, 12345, p);
            Check(Wire.TryOpen(bytes, bytes.Length, out MsgType type, out PacketReader r) && type == MsgType.Pose, "pose packet opens");
            Check(r.U8() == 3, "id");
            Check(r.U16() == 777, "seq");
            Check(r.U32() == 12345u, "time");
            var q = r.Player();
            Check(q.MaxPosDistance(p) == 0f, "positions are bit exact");
            Check(q.Head.Rot.X == p.Head.Rot.X && q.Head.Rot.Y == p.Head.Rot.Y && q.Head.Rot.W == p.Head.Rot.W, "rotation is bit exact");

            var bad = p;
            bad.Head.Pos.X = float.NaN;
            Check(!bad.IsSane, "NaN pose is not sane");
            bad = p;
            bad.Left.Pos.Y = 1e9f;
            Check(!bad.IsSane, "huge pose is not sane");
            bad = p;
            bad.Right.Rot = new Quat(0, 0, 0, 0);
            Check(!bad.IsSane, "zero quaternion is not sane");

            byte[] state = Wire.EncodeState("MainMenu_VR", 7, new List<PlayerInfo> { new PlayerInfo(0, "Анна"), new PlayerInfo(2, "Bob") });
            Check(Wire.TryOpen(state, state.Length, out type, out r) && type == MsgType.State, "state opens");
            Check(r.Str() == "MainMenu_VR" && r.U16() == 7 && r.U8() == 2, "state header");
            Check(r.U8() == 0 && r.Str() == "Анна", "non-ascii name survives");
        }

        private struct BufStats
        {
            public double MaxErrM, MaxErrLateM, P95StaleMs, MaxStepM, AvgDelayMs;
            public int Frames;
        }

        /// <summary>
        /// Sender streams BotPose at <paramref name="rateHz"/>; network adds a fixed delay plus random jitter, loses and duplicates packets
        /// (reordering follows from the jitter). The receiver draws at 90 Hz from a PoseBuffer. Truth is BotPose at the sender time the
        /// buffer says it is drawing, so the error is interpolation/extrapolation error only; staleness is how far the drawn moment
        /// is behind the sender's real present.
        /// </summary>
        private static BufStats SimulateBuffer(int seed, double loss, double dup, int fixedDelayMs, int jitterMs, int rateHz, int stallFromMs = -1, int stallMs = 0, int lateAfterSenderMs = 0)
        {
            var rng = new Random(seed);
            const long ClockOffset = 777000; // local clock = sender clock + offset; the buffer must not need to know it
            var arrivals = new List<(long at, uint sender, PlayerPose pose)>();
            int stepMs = 1000 / rateHz;
            for (int t = 10000; t < 30000; t += stepMs)
            {
                if (stallFromMs >= 0 && t >= 10000 + stallFromMs && t < 10000 + stallFromMs + stallMs) continue; // sender frozen: nothing sent
                if (rng.NextDouble() < loss) continue;
                int copies = rng.NextDouble() < dup ? 2 : 1;
                for (int c = 0; c < copies; c++)
                    arrivals.Add((t + ClockOffset + fixedDelayMs + rng.Next(jitterMs + 1), (uint)t, BotPose.At((uint)t, 1)));
            }
            arrivals.Sort((x, y) => x.at.CompareTo(y.at));

            var buf = new PoseBuffer();
            int next = 0;
            double maxErr = 0, maxErrLate = 0, maxStep = 0, delaySum = 0;
            var stale = new List<double>();
            bool havePrev = false;
            Vec3 prev = default(Vec3);
            int frames = 0;
            for (long now = 10000 + ClockOffset; now < 30000 + ClockOffset; now += 11)
            {
                while (next < arrivals.Count && arrivals[next].at <= now)
                {
                    buf.Add(arrivals[next].sender, arrivals[next].at, arrivals[next].pose);
                    next++;
                }
                if (!buf.Sample(now, out PlayerPose p)) continue;
                if (now < 15000 + ClockOffset) continue; // warm-up
                double tr = buf.LastRenderSenderMs;
                var truth = BotPose.At((uint)Math.Round(tr), 1);
                double e = p.MaxPosDistance(truth);
                maxErr = Math.Max(maxErr, e);
                if (tr >= lateAfterSenderMs) maxErrLate = Math.Max(maxErrLate, e);
                stale.Add((now - ClockOffset) - tr);
                delaySum += buf.DelayMs;
                if (havePrev) maxStep = Math.Max(maxStep, prev.Distance(p.Head.Pos));
                prev = p.Head.Pos;
                havePrev = true;
                frames++;
            }
            stale.Sort();
            return new BufStats
            {
                MaxErrM = maxErr,
                MaxErrLateM = maxErrLate,
                P95StaleMs = stale.Count > 0 ? stale[(int)(0.95 * (stale.Count - 1))] : double.NaN,
                MaxStepM = maxStep,
                AvgDelayMs = frames > 0 ? delaySum / frames : double.NaN,
                Frames = frames,
            };
        }

        private static void PoseBufferQuality()
        {
            // Bot head moves at 0.75 m/s, so a smooth 90 Hz picture moves about 8 mm per frame.
            var good = SimulateBuffer(1, 0.0, 0.0, 20, 10, 60);
            Console.WriteLine($"    good net  (20 ms +0-10 jitter, 60 Hz): err {good.MaxErrM * 1000:F1} mm, stale p95 {good.P95StaleMs:F0} ms, delay {good.AvgDelayMs:F0} ms, max step {good.MaxStepM * 1000:F1} mm");
            Check(good.MaxErrM < 0.005, "good net: drawn pose within 5 mm of the sender's true path");
            Check(good.P95StaleMs < 90, $"good net: staleness under 90 ms (p95 {good.P95StaleMs:F0})");
            Check(good.MaxStepM < 0.012, "good net: no jumps between frames");

            var wifi = SimulateBuffer(2, 0.05, 0.05, 35, 40, 60);
            Console.WriteLine($"    wifi      (35 ms +0-40 jitter, 5% loss, 60 Hz): err {wifi.MaxErrM * 1000:F1} mm, stale p95 {wifi.P95StaleMs:F0} ms, delay {wifi.AvgDelayMs:F0} ms, max step {wifi.MaxStepM * 1000:F1} mm");
            Check(wifi.MaxErrM < 0.03, "wifi: within 3 cm");
            Check(wifi.P95StaleMs < 170, $"wifi: staleness under 170 ms (p95 {wifi.P95StaleMs:F0})");
            Check(wifi.MaxStepM < 0.03, "wifi: no jumps");

            var bad = SimulateBuffer(3, 0.30, 0.20, 60, 120, 60);
            Console.WriteLine($"    bad net   (60 ms +0-120 jitter, 30% loss, 20% dup, 60 Hz): err {bad.MaxErrM * 1000:F1} mm, stale p95 {bad.P95StaleMs:F0} ms, delay {bad.AvgDelayMs:F0} ms, max step {bad.MaxStepM * 1000:F1} mm");
            Check(bad.MaxErrM < 0.15, "bad net: within 15 cm");
            Check(bad.P95StaleMs < 330, $"bad net: staleness under 330 ms (p95 {bad.P95StaleMs:F0})");
            Check(bad.MaxStepM < 0.06, "bad net: no big jumps");

            // The sender freezes from 18000 to 18600 (its clock), then its pose jumps ahead by about 0.45 m.
            var stall = SimulateBuffer(4, 0.02, 0.0, 30, 20, 60, stallFromMs: 8000, stallMs: 600, lateAfterSenderMs: 19600);
            Console.WriteLine($"    stall     (sender frozen 600 ms): worst err {stall.MaxErrM * 1000:F0} mm during the catch-up, {stall.MaxErrLateM * 1000:F1} mm 1 s later, stale p95 {stall.P95StaleMs:F0} ms, max step {stall.MaxStepM * 1000:F1} mm");
            Check(stall.MaxStepM < 0.03, $"stall: the catch-up has no visible pop (max step {stall.MaxStepM * 1000:F1} mm)");
            Check(stall.MaxErrLateM < 0.005, "stall: a second after the stall the picture is back on the true path");

            // Quaternion helpers used by the buffer.
            var qa = new Quat(0, 0, 0, 1);
            var qb = new Quat(0, (float)Math.Sin(0.5), 0, (float)Math.Cos(0.5)); // 1 rad about Y
            var mid = Quat.Slerp(qa, qb, 0.5);
            Check(Math.Abs(mid.Y - (float)Math.Sin(0.25)) < 1e-5 && mid.IsSane, "slerp halfway is half the angle");
            var neg = new Quat(-qb.X, -qb.Y, -qb.Z, -qb.W);
            var viaNeg = Quat.Slerp(qa, neg, 0.5);
            Check(Math.Abs(viaNeg.Y - mid.Y) < 1e-5, "slerp takes the short way round");
        }

        private static void WireTruncation()
        {
            var packets = new List<byte[]>
            {
                Wire.EncodeHello("Bob"),
                Wire.EncodeWelcome(1, 4, "MainMenu_VR"),
                Wire.EncodePose(1, 5, 99, BotPose.At(99, 1)),
                Wire.EncodeState("scene", 1, new List<PlayerInfo> { new PlayerInfo(0, "A"), new PlayerInfo(1, "B") }),
                Wire.EncodePing(5), Wire.EncodePong(5), Wire.EncodeBye(),
            };
            int accepted = 0;
            foreach (var pkt in packets)
            {
                for (int len = 0; len < pkt.Length; len++)
                {
                    if (!Wire.TryOpen(pkt, len, out MsgType t, out PacketReader r)) continue;
                    accepted++;
                    try
                    {
                        switch (t)
                        {
                            case MsgType.Hello: r.Str(); break;
                            case MsgType.Welcome: r.U8(); r.U8(); r.Str(); break;
                            case MsgType.Pose: r.U8(); r.U16(); r.U32(); r.Player(); break;
                            case MsgType.State: r.Str(); r.U16(); int c = r.U8(); for (int i = 0; i < c; i++) { r.U8(); r.Str(); } break;
                            case MsgType.Ping: case MsgType.Pong: r.U32(); break;
                        }
                    }
                    catch (FormatException) { }
                }
            }
            Check(accepted > 0, "header-only prefixes open, bodies are then rejected by FormatException only");
            Check(!Wire.TryOpen(null, 10, out _, out _), "null data");
            Check(!Wire.TryOpen(new byte[5000], 5000, out _, out _), "oversize packet");
        }

        private static void Handshake()
        {
            var hub = new FakeHub(1);
            var host = MakeNode(hub, SessionRole.Host, "Alice");
            var c1 = MakeNode(hub, SessionRole.Client, "Bob");
            hub.Run(1000, Pump(host), Pump(c1));
            Check(c1.S.Connected, "client connected");
            Check(c1.S.LocalId == 1, "client id is 1");
            Check(host.S.Remotes.Count == 1 && host.S.Remotes[1].Name == "Bob", "host knows Bob");
            Check(c1.S.Remotes.Count == 1 && c1.S.Remotes[0].Name == "Alice", "client knows Alice");
            Check(c1.S.BadPackets == 0 && host.S.BadPackets == 0, "no bad packets");
        }

        private static void PosesAndRelay()
        {
            var hub = new FakeHub(2);
            var host = MakeNode(hub, SessionRole.Host, "Host");
            var a = MakeNode(hub, SessionRole.Client, "A");
            var b = MakeNode(hub, SessionRole.Client, "B");
            hub.Run(3000, Pump(host), Pump(a), Pump(b), Sender(hub, host), Sender(hub, a), Sender(hub, b));
            Check(a.S.Connected && b.S.Connected, "both clients connected");
            Check(a.S.LocalId != b.S.LocalId && a.S.LocalId > 0 && b.S.LocalId > 0, "distinct ids");
            Check(host.Ok > 100 && a.Ok > 100 && b.Ok > 100, $"everyone received poses (host {host.Ok}, a {a.Ok}, b {b.Ok})");
            Check(host.Bad == 0 && a.Bad == 0 && b.Bad == 0, "every received pose equals the sender's bot pose for that id (no mix-ups)");
            Check(a.S.Remotes.ContainsKey(0) && a.S.Remotes.ContainsKey(b.S.LocalId), "A sees host and B");
            Check(b.S.Remotes.ContainsKey(0) && b.S.Remotes.ContainsKey(a.S.LocalId), "B sees host and A");
            Check(a.S.Remotes[b.S.LocalId].HasPose, "A has a pose for B (relayed by host)");
            Check(host.NonMonotonic == 0 && a.NonMonotonic == 0 && b.NonMonotonic == 0, "no stale pose accepted");
            Check(a.S.RttMs >= 0, "rtt measured");
        }

        private static void Chaos()
        {
            var hub = new FakeHub(3) { Loss = 0.30, Dup = 0.20, MinDelayMs = 0, MaxDelayMs = 250 };
            var host = MakeNode(hub, SessionRole.Host, "Host");
            var a = MakeNode(hub, SessionRole.Client, "A");
            var b = MakeNode(hub, SessionRole.Client, "B");
            hub.Run(30000, Pump(host), Pump(a), Pump(b), Sender(hub, host), Sender(hub, a), Sender(hub, b));
            Check(a.S.Connected && b.S.Connected, "clients connected despite loss");
            Check(host.Ok > 200 && a.Ok > 200 && b.Ok > 200, $"poses kept flowing (host {host.Ok}, a {a.Ok}, b {b.Ok})");
            Check(host.Bad == 0 && a.Bad == 0 && b.Bad == 0, "no corrupted or mixed-up pose under chaos");
            Check(host.NonMonotonic == 0 && a.NonMonotonic == 0 && b.NonMonotonic == 0, "duplicates and reordered packets never applied an older pose");
            Check(host.S.BadPackets == 0 && a.S.BadPackets == 0 && b.S.BadPackets == 0, "chaos alone produces no malformed packets");
        }

        private static void SceneSync()
        {
            foreach (double loss in new[] { 0.0, 0.5 })
            {
                var hub = new FakeHub(4) { Loss = loss };
                var host = MakeNode(hub, SessionRole.Host, "Host");
                var a = MakeNode(hub, SessionRole.Client, "A");
                var b = MakeNode(hub, SessionRole.Client, "B");
                var seen = new List<string>();
                a.S.HostSceneChanged += s => seen.Add(s);
                host.S.SetLocalScene("MainMenu_VR");
                hub.Run(loss == 0 ? 1500 : 12000, Pump(host), Pump(a), Pump(b));
                Check(a.S.HostScene == "MainMenu_VR" && b.S.HostScene == "MainMenu_VR", $"clients learn the host scene (loss {loss})");
                host.S.SetLocalScene("Level2");
                hub.Run(loss == 0 ? 200 : 12000, Pump(host), Pump(a), Pump(b));
                Check(a.S.HostScene == "Level2" && b.S.HostScene == "Level2", $"scene change propagates (loss {loss})");
                Check(seen.Count == 2 && seen[0] == "MainMenu_VR" && seen[1] == "Level2", $"scene change event fires once per change (loss {loss}): {string.Join(",", seen)}");
                a.S.SetLocalScene("Hacked");
                hub.Run(1500, Pump(host), Pump(a), Pump(b));
                Check(b.S.HostScene == "Level2", "a client cannot change the host scene");
            }
        }

        private static void TimeoutsAndReconnect()
        {
            var hub = new FakeHub(5);
            var host = MakeNode(hub, SessionRole.Host, "Host");
            var a = MakeNode(hub, SessionRole.Client, "A");
            var b = MakeNode(hub, SessionRole.Client, "B");
            var left = new List<string>();
            a.S.PlayerLeft += rp => left.Add(rp.Name);
            hub.Run(2000, Pump(host), Pump(a), Pump(b), Sender(hub, host), Sender(hub, a), Sender(hub, b));
            Check(a.S.Connected && b.S.Connected, "all connected");
            byte bId = b.S.LocalId;

            // B crashes: stops sending and updating.
            hub.Run(7000, Pump(host), Pump(a), Sender(hub, host), Sender(hub, a));
            Check(!host.S.Remotes.ContainsKey(bId), "host dropped the silent client");
            Check(!a.S.Remotes.ContainsKey(bId), "other client was told");
            Check(left.Contains("B"), "PlayerLeft fired for B");
            Check(host.T.Known.Count <= 1, $"host forgot the dead connection (known {host.T.Known.Count})");

            // A new client takes the freed slot.
            var c = MakeNode(hub, SessionRole.Client, "C");
            hub.Run(1500, Pump(host), Pump(a), Pump(c));
            Check(c.S.Connected && c.S.LocalId == bId, "freed id is reused");

            // Host crashes and a new session starts on the same transport; clients must rejoin on their own.
            var host2 = new CoopSession(SessionRole.Host, host.T, hub.Clock, "Host2");
            hub.Run(7000, () => host2.Update(), Pump(a), Pump(c));
            Check(a.S.Connected && c.S.Connected, "clients rejoined the restarted host");
            Check(host2.Remotes.Count == 2, "restarted host has both clients");
        }

        private static void Impersonation()
        {
            var hub = new FakeHub(8);
            var host = MakeNode(hub, SessionRole.Host, "Host");
            var a = MakeNode(hub, SessionRole.Client, "A");
            var b = MakeNode(hub, SessionRole.Client, "B");
            const uint Sentinel = 4242424242u;
            int sentinelSeen = 0;
            host.S.PoseReceived += (id, t, p) => { if (t == Sentinel) sentinelSeen++; };
            b.S.PoseReceived += (id, t, p) => { if (t == Sentinel) sentinelSeen++; };
            hub.Run(1500, Pump(host), Pump(a), Pump(b));
            Check(a.S.Connected && b.S.Connected, "all connected");

            // A claims to be the host (0), and to be B.
            var fake = BotPose.At(1, 5);
            hub.Inject(host.T, a.T.Id, Wire.EncodePose(0, 1000, Sentinel, fake));
            hub.Inject(host.T, a.T.Id, Wire.EncodePose(b.S.LocalId, 1001, Sentinel, fake));
            hub.Run(500, Pump(host), Pump(a), Pump(b));
            Check(sentinelSeen == 0, "no forged pose was accepted or relayed");
            Check(host.S.RejectedPoseClaims == 2, $"host counted both forgeries (got {host.S.RejectedPoseClaims})");
            Check(!host.S.Remotes[b.S.LocalId].HasPose || host.S.Remotes[b.S.LocalId].PoseTimeMs != Sentinel, "B's slot on the host is untouched");

            // A honest pose from A is still accepted.
            hub.Inject(host.T, a.T.Id, Wire.EncodePose(a.S.LocalId, 2000, 777, BotPose.At(777, a.S.LocalId)));
            hub.Run(100, Pump(host), Pump(a), Pump(b));
            Check(host.S.Remotes[a.S.LocalId].HasPose && host.S.Remotes[a.S.LocalId].PoseTimeMs == 777, "an honest pose from A is accepted");
        }

        private static BodyState St(float x)
        {
            var s = new BodyState();
            s.Pos = new Vec3(x, 0, 0);
            s.Rot = new Quat(0, 0, 0, 1);
            s.Vel = new Vec3(1, 0, 0);
            s.AngVel = new Vec3(0, 0, 0);
            return s;
        }

        private static (byte owner, ushort epoch) View(Node n, uint id)
        {
            return n.S.Bodies.TryGetValue(id, out BodyView v) ? (v.Owner, v.Epoch) : (BodyView.NoOwner, (ushort)0);
        }

        private static bool Agree(uint id, params Node[] nodes)
        {
            var first = View(nodes[0], id);
            foreach (var n in nodes) if (View(n, id) != first) return false;
            return true;
        }

        private static void BodyOwnership()
        {
            var hub = new FakeHub(31);
            var host = MakeNode(hub, SessionRole.Host, "H");
            var a = MakeNode(hub, SessionRole.Client, "A");
            var b = MakeNode(hub, SessionRole.Client, "B");
            var receivedByB = new List<uint>();
            b.S.BodyStateReceived += v => receivedByB.Add(v.TimeMs);
            var wrongReceiver = 0;
            a.S.BodyStateReceived += v => { if (v.Owner == a.S.LocalId) wrongReceiver++; };
            hub.Run(1500, Pump(host), Pump(a), Pump(b));
            Check(a.S.Connected && b.S.Connected, "connected");

            // 1. basic claim and state streaming
            a.S.ClaimBody(7);
            hub.Run(200, Pump(host), Pump(a), Pump(b));
            Check(View(a, 7) == (a.S.LocalId, (ushort)1) && Agree(7, host, a, b), "claim: host, A and B agree that A owns body 7 at epoch 1");
            a.S.SendBodyState(7, St(1), 100);
            a.S.SendBodyState(7, St(2), 133);
            hub.Run(100, Pump(host), Pump(a), Pump(b));
            Check(host.S.Bodies[7].HasState && host.S.Bodies[7].State.Pos.X == 2f, "host got the latest state");
            Check(b.S.Bodies[7].HasState && b.S.Bodies[7].State.Pos.X == 2f && receivedByB.Count == 2, "B got both states, relayed by the host");
            b.S.SendBodyState(7, St(99), 200); // B is not the owner
            hub.Run(100, Pump(host), Pump(a), Pump(b));
            Check(host.S.Bodies[7].State.Pos.X == 2f, "a non-owner cannot publish a state");
            // A forged packet from B for body 7, with the right epoch and even naming A as owner: the host looks at the connection.
            int staleForged = host.S.StaleBodyStates;
            hub.Inject(host.T, b.T.Id, Wire.EncodeBodyState(7, a.S.LocalId, 1, 50, 900, St(1234)));
            hub.Run(100, Pump(host), Pump(a), Pump(b));
            Check(host.S.Bodies[7].State.Pos.X == 2f && host.S.StaleBodyStates == staleForged + 1, "a forged state from a non-owner connection is rejected by the host");

            // 2. conflict: A and B claim body 8 in the same step
            a.S.ClaimBody(8);
            b.S.ClaimBody(8);
            a.S.SendBodyState(8, St(10), 300); // not confirmed yet: nothing is sent
            b.S.SendBodyState(8, St(20), 300);
            hub.Run(300, Pump(host), Pump(a), Pump(b));
            Check(Agree(8, host, a, b), "conflict: all three views agree on one owner and epoch");
            var winner = View(host, 8).owner;
            Check(winner == a.S.LocalId || winner == b.S.LocalId, "conflict: the owner is one of the two claimants");
            Check(View(host, 8).epoch == 1, "conflict: exactly one grant happened (epoch 1)");
            var win = winner == a.S.LocalId ? a : b;
            var lose = winner == a.S.LocalId ? b : a;
            win.S.SendBodyState(8, St(10), 400);
            lose.S.SendBodyState(8, St(20), 400); // the loser is no longer an owner: nothing is sent
            hub.Run(200, Pump(host), Pump(a), Pump(b));
            Check(host.S.Bodies[8].State.Pos.X == 10f, "conflict: the host keeps only the winner's state");
            Check(!lose.S.IsBodyOwner(8) && win.S.IsBodyOwner(8), "conflict: only the winner may stream");

            // 3. stale replay: A owns 9, releases, B claims; then old traffic from A's time is replayed
            a.S.ClaimBody(9);
            hub.Run(100, Pump(host), Pump(a), Pump(b));
            a.S.ReleaseBody(9);
            hub.Run(100, Pump(host), Pump(a), Pump(b));
            b.S.ClaimBody(9);
            hub.Run(100, Pump(host), Pump(a), Pump(b));
            Check(View(host, 9) == (b.S.LocalId, (ushort)3) && Agree(9, host, a, b), "claim, release, claim: epoch 3, owner B everywhere");
            int staleBefore = host.S.StaleBodyStates;
            hub.Inject(host.T, a.T.Id, Wire.EncodeBodyState(9, a.S.LocalId, 1, 5, 500, St(777)));  // A's old epoch-1 state
            hub.Inject(b.T, 0, Wire.EncodeBodyGrant(9, a.S.LocalId, 1));                           // replayed old grant
            hub.Run(100, Pump(host), Pump(a), Pump(b));
            Check(host.S.StaleBodyStates == staleBefore + 1 && !(host.S.Bodies[9].HasState && host.S.Bodies[9].State.Pos.X == 777f), "a past owner's state is rejected");
            Check(View(b, 9) == (b.S.LocalId, (ushort)3), "a replayed older grant changes nothing");

            // 4. release
            a.S.ClaimBody(10);
            hub.Run(100, Pump(host), Pump(a), Pump(b));
            a.S.ReleaseBody(10);
            hub.Run(100, Pump(host), Pump(a), Pump(b));
            Check(View(host, 10) == (BodyView.NoOwner, (ushort)2) && Agree(10, host, a, b), "release: free again at epoch 2 on every machine");

            // 5. owner leaves: A owns 11 then goes silent
            a.S.ClaimBody(11);
            hub.Run(100, Pump(host), Pump(a), Pump(b));
            Check(View(b, 11).owner == a.S.LocalId, "B knows A owns 11");
            hub.Run(7000, Pump(host), Pump(b)); // A crashed
            Check(View(host, 11).owner == BodyView.NoOwner && View(b, 11).owner == BodyView.NoOwner, "owner crashed: body 11 is free on the host and on B");
            Check(View(host, 11).epoch > 1 && Agree(11, host, b), "and the epoch moved on, so A's late packets would be stale");

            // 6. late join: B owns 12 and 13, then a new client C joins
            b.S.ClaimBody(12);
            b.S.ClaimBody(13);
            hub.Run(200, Pump(host), Pump(b));
            var c = MakeNode(hub, SessionRole.Client, "C");
            hub.Run(1500, Pump(host), Pump(b), Pump(c));
            Check(c.S.Connected, "C joined");
            Check(View(c, 12) == (b.S.LocalId, (ushort)1) && View(c, 13) == (b.S.LocalId, (ushort)1), "late join: C learned the owners of bodies claimed before it arrived");
            Check(wrongReceiver == 0, "a machine never receives a state for a body it owns");
        }

        private static void BodyChaos()
        {
            var hub = new FakeHub(32) { Loss = 0.30, Dup = 0.20, MinDelayMs = 0, MaxDelayMs = 150 };
            var host = MakeNode(hub, SessionRole.Host, "H");
            var a = MakeNode(hub, SessionRole.Client, "A");
            var b = MakeNode(hub, SessionRole.Client, "B");
            var all = new[] { host, a, b };
            hub.Run(6000, Pump(host), Pump(a), Pump(b));
            Check(a.S.Connected && b.S.Connected, "connected through the chaos");

            int illegal = 0;
            foreach (var n in all)
            {
                var me = n;
                me.S.BodyStateReceived += v => { if (v.Owner == me.S.LocalId) illegal++; };
            }

            var rng = new Random(5);
            for (int step = 0; step < 2000; step++) // 20 s at 10 ms
            {
                hub.Run(10, Pump(host), Pump(a), Pump(b));
                if (step % 7 == 0)
                {
                    var n = all[rng.Next(3)];
                    uint id = (uint)rng.Next(1, 21);
                    switch (rng.Next(4))
                    {
                        case 0: n.S.ClaimBody(id); break;
                        case 1: n.S.ReleaseBody(id); break;
                        default: n.S.SendBodyState(id, St(step), (uint)hub.Clock.NowMs); break;
                    }
                }
            }
            hub.Loss = 0; hub.Dup = 0; hub.MaxDelayMs = 0;
            hub.Run(8000, Pump(host), Pump(a), Pump(b)); // quiet network: the periodic grants must repair every view
            int disagree = 0;
            for (uint id = 1; id <= 20; id++) if (!Agree(id, host, a, b)) disagree++;
            Check(disagree == 0, $"after the chaos all 20 bodies have the same owner and epoch on all machines (disagree {disagree})");
            Check(illegal == 0, "no machine ever applied a state for a body it owns itself");
            Check(host.S.BadPackets == 0 && a.S.BadPackets == 0 && b.S.BadPackets == 0, "no malformed packets");
        }

        private static void GameEvents()
        {
            foreach (double loss in new[] { 0.0, 0.2, 0.3 })
            {
                var hub = new FakeHub(61 + (int)(loss * 10)) { Loss = loss, Dup = loss > 0 ? 0.2 : 0, MinDelayMs = 0, MaxDelayMs = loss > 0 ? 120 : 0 };
                var host = MakeNode(hub, SessionRole.Host, "H");
                var a = MakeNode(hub, SessionRole.Client, "A");
                var b = MakeNode(hub, SessionRole.Client, "B");
                hub.Run(8000, Pump(host), Pump(a), Pump(b));
                Check(a.S.Connected && b.S.Connected, $"[loss {loss}] connected");

                var gotHost = new List<uint>(); var gotA = new List<uint>(); var gotB = new List<uint>();
                var fromOnHost = new HashSet<byte>(); var fromOnB = new HashSet<byte>();
                host.S.GameEventReceived += (id, k, f) => { gotHost.Add(id); fromOnHost.Add(f); };
                a.S.GameEventReceived += (id, k, f) => gotA.Add(id);
                b.S.GameEventReceived += (id, k, f) => { gotB.Add(id); fromOnB.Add(f); };

                // A fires 40 events (ids 1000..1039); the host fires 10 (2000..2009).
                for (uint i = 0; i < 40; i++) { a.S.SendGameEvent(1000 + i, 1); hub.Run(60, Pump(host), Pump(a), Pump(b)); }
                for (uint i = 0; i < 10; i++) { host.S.SendGameEvent(2000 + i, 2); hub.Run(60, Pump(host), Pump(a), Pump(b)); }
                hub.Run(3000, Pump(host), Pump(a), Pump(b));

                int aFromHost = gotHost.FindAll(x => x >= 1000 && x < 1040).Count;
                int bFromA = gotB.FindAll(x => x >= 1000 && x < 1040).Count;
                int aFromHostEvents = gotA.FindAll(x => x >= 2000).Count;
                int bFromHostEvents = gotB.FindAll(x => x >= 2000).Count;
                double need = loss >= 0.3 ? 0.9 : 1.0;
                Check(aFromHost >= need * 40, $"[loss {loss}] host got A's events ({aFromHost}/40)");
                Check(bFromA >= need * 40, $"[loss {loss}] B got A's events through the host relay ({bFromA}/40)");
                Check(aFromHostEvents >= need * 10 && bFromHostEvents >= need * 10, $"[loss {loss}] both clients got the host's events ({aFromHostEvents}/10, {bFromHostEvents}/10)");
                Check(gotHost.Count == new HashSet<uint>(gotHost).Count && gotB.Count == new HashSet<uint>(gotB).Count && gotA.Count == new HashSet<uint>(gotA).Count, $"[loss {loss}] no event was delivered twice");
                Check(gotA.FindAll(x => x < 2000).Count == 0, $"[loss {loss}] A never receives its own events back");
                Check(fromOnB.Contains(a.S.LocalId) && fromOnB.Contains(0), $"[loss {loss}] B can tell who an event fired for (A and the host)");
            }

            // A runaway script is rate limited.
            var h2 = new FakeHub(70);
            var ho = MakeNode(h2, SessionRole.Host, "H");
            var cl = MakeNode(h2, SessionRole.Client, "C");
            h2.Run(1500, Pump(ho), Pump(cl));
            int received = 0;
            ho.S.GameEventReceived += (id, k, f) => received++;
            for (uint i = 0; i < 500; i++) cl.S.SendGameEvent(i, 0);
            h2.Run(3000, Pump(ho), Pump(cl));
            Check(received <= 70 && received >= 50, $"rate limit: 500 events in one instant -> {received} delivered");
        }

        private static void PoseExtension()
        {
            // round trip of the extension, and an old-format packet (no extension) still reads
            var p = BotPose.At(1234, 1);
            p.Flags = (byte)(PoseFlags.Vr | PoseFlags.FemaleHands | PoseFlags.HandsAreRoots);
            p.Skin = 77;
            p.LeftFingers = new FingerBends(1, 2, 3, 4, 5);
            p.RightFingers = new FingerBends(250, 200, 150, 100, 50);
            byte[] pkt = Wire.EncodePose(1, 9, 1234, p);
            Check(Wire.TryOpen(pkt, pkt.Length, out MsgType t, out PacketReader r) && t == MsgType.Pose, "extended pose opens");
            r.U8(); r.U16(); r.U32();
            var q = r.Player();
            Check(q.Flags == (p.Flags | PoseFlags.Extended) && q.Skin == 77, "flags and skin survive (Extended bit set by the writer)");
            bool fingersOk = true;
            for (int i = 0; i < 5; i++) fingersOk &= q.LeftFingers[i] == p.LeftFingers[i] && q.RightFingers[i] == p.RightFingers[i];
            Check(fingersOk, "all ten finger bends survive");
            Check(q.MaxPosDistance(p) == 0f, "positions unchanged by the extension");
            byte[] old = new byte[pkt.Length - 12];
            Buffer.BlockCopy(pkt, 0, old, 0, old.Length);
            Check(Wire.TryOpen(old, old.Length, out _, out PacketReader r2), "old pose (no extension) opens");
            r2.U8(); r2.U16(); r2.U32();
            var o = r2.Player();
            Check(o.Flags == 0 && o.MaxPosDistance(p) == 0f, "old pose reads with no flags");

            // through the host relay to another client, interpolated by the buffer
            var hub = new FakeHub(81);
            var host = MakeNode(hub, SessionRole.Host, "H");
            var a = MakeNode(hub, SessionRole.Client, "A");
            var b = MakeNode(hub, SessionRole.Client, "B");
            hub.Run(1500, Pump(host), Pump(a), Pump(b));
            long next = 0;
            Action send = () =>
            {
                if (hub.Clock.NowMs < next || !a.S.Connected) return;
                next = hub.Clock.NowMs + 16;
                uint tm = (uint)hub.Clock.NowMs;
                var pose = BotPose.At(tm, a.S.LocalId);
                pose.Flags = PoseFlags.Vr;
                pose.Skin = 200;
                pose.LeftFingers = FingerBends.All(180);
                pose.RightFingers = FingerBends.All(20);
                a.S.SendLocalPose(pose, tm);
            };
            hub.Run(2000, Pump(host), Pump(a), Pump(b), send);
            RemotePlayer rp = null;
            Check(b.S.Remotes.TryGetValue(a.S.LocalId, out rp) && rp.HasPose, "B has A's pose");
            PlayerPose drawn = default(PlayerPose);
            Check(rp != null && rp.Buffer.Sample(b.S.NowMs, out drawn) && drawn.LeftFingers.Index == 180 && drawn.RightFingers.Pinky == 20 && drawn.Skin == 200 && (drawn.Flags & PoseFlags.Vr) != 0,
                "fingers, skin and flags arrive at B through the host and through the buffer");
            var mid = FingerBends.Lerp(FingerBends.All(0), FingerBends.All(200), 0.5);
            Check(mid.Middle == 100, "finger bends interpolate");
        }

        private static void Effects()
        {
            foreach (double loss in new[] { 0.0, 0.2 })
            {
                var hub = new FakeHub(90 + (int)(loss * 10)) { Loss = loss, Dup = 0, MinDelayMs = 5, MaxDelayMs = 40 };
                var host = MakeNode(hub, SessionRole.Host, "H");
                var a = MakeNode(hub, SessionRole.Client, "A");
                var b = MakeNode(hub, SessionRole.Client, "B");
                hub.Run(4000, Pump(host), Pump(a), Pump(b));
                var onHost = new List<FxEvent>(); var onA = new List<FxEvent>(); var onB = new List<FxEvent>();
                host.S.FxReceivedEvent += e => onHost.Add(e);
                a.S.FxReceivedEvent += e => onA.Add(e);
                b.S.FxReceivedEvent += e => onB.Add(e);
                for (int i = 0; i < 30; i++)
                {
                    a.S.SendFx(new FxEvent { Kind = FxKind.Footstep, From = 3 /* forged, must be replaced */, Pos = new Vec3(i, 1, 2), A = 0.8f, B = 0.2f, C = 1, D = 255 });
                    hub.Run(50, Pump(host), Pump(a), Pump(b));
                }
                host.S.SendFx(new FxEvent { Kind = FxKind.BodySplash, Pos = new Vec3(5, 0, 5), A = 3f, B = 1f });
                hub.Run(500, Pump(host), Pump(a), Pump(b));
                double need = loss > 0 ? 0.6 : 1.0;
                Check(onHost.Count >= need * 30 && onHost.Count <= 30, $"[loss {loss}] host got A's steps once each ({onHost.Count}/30, A connected={a.S.Connected}, sent={a.S.FxSent})");
                Check(onB.FindAll(e => e.Kind == FxKind.Footstep).Count >= need * need * 30, $"[loss {loss}] B got A's steps through the relay ({onB.Count})");
                Check(onA.FindAll(e => e.Kind == FxKind.Footstep).Count == 0, $"[loss {loss}] A never gets its own steps back");
                Check(onB.TrueForAll(e => e.Kind != FxKind.Footstep || e.From == a.S.LocalId), $"[loss {loss}] the sender id is A's real id, not the forged one");
                var st = onB.Find(e => e.Kind == FxKind.Footstep);
                Check(onB.Count == 0 || (st.A == 0.8f && st.B == 0.2f && st.C == 1 && st.D == 255), $"[loss {loss}] footstep fields survive");
                if (loss == 0) Check(onA.Exists(e => e.Kind == FxKind.BodySplash && e.From == 0) && onB.Exists(e => e.Kind == FxKind.BodySplash), "the host's splash reaches both clients");
            }

            var h2 = new FakeHub(99);
            var ho = MakeNode(h2, SessionRole.Host, "H");
            var cl = MakeNode(h2, SessionRole.Client, "C");
            h2.Run(1500, Pump(ho), Pump(cl));
            int got = 0;
            ho.S.FxReceivedEvent += e => got++;
            for (int i = 0; i < 300; i++) cl.S.SendFx(new FxEvent { Kind = FxKind.Footstep, Pos = new Vec3(0, 0, 0) });
            cl.S.SendFx(new FxEvent { Kind = FxKind.Footstep, Pos = new Vec3(float.NaN, 0, 0) });
            h2.Run(1000, Pump(ho), Pump(cl));
            Check(got <= 40 && got >= 30, $"fx rate limit: 300 in one instant -> {got}");
        }

        private static void Capacity()
        {
            var hub = new FakeHub(6);
            var host = MakeNode(hub, SessionRole.Host, "Host");
            var clients = new List<Node>();
            for (int i = 0; i < 4; i++) clients.Add(MakeNode(hub, SessionRole.Client, "G" + i));
            var pumps = new List<Action> { Pump(host) };
            foreach (var c in clients) pumps.Add(Pump(c));
            hub.Run(4000, pumps.ToArray());
            int connected = 0;
            var ids = new HashSet<byte>();
            foreach (var c in clients) if (c.S.Connected) { connected++; ids.Add(c.S.LocalId); }
            Check(connected == 3, $"exactly 3 guests fit (got {connected})");
            Check(ids.Count == 3 && ids.Contains(1) && ids.Contains(2) && ids.Contains(3), "ids 1..3");
            Check(host.S.Remotes.Count == 3, "host tracks 3");
        }

        private static void Fuzz()
        {
            var hub = new FakeHub(7);
            var host = MakeNode(hub, SessionRole.Host, "Host");
            var a = MakeNode(hub, SessionRole.Client, "A");
            hub.Run(1500, Pump(host), Pump(a));
            Check(a.S.Connected, "baseline connected");
            var rng = new Random(99);

            var seeds = new List<byte[]>
            {
                Wire.EncodeHello("Mallory"),
                Wire.EncodeWelcome(2, 4, "x"),
                Wire.EncodePose(1, 9, 9, BotPose.At(9, 1)),
                Wire.EncodeState("s", 3, new List<PlayerInfo> { new PlayerInfo(0, "H"), new PlayerInfo(1, "A") }),
                Wire.EncodePing(1), Wire.EncodePong(1), Wire.EncodeBye(),
            };

            for (int i = 0; i < 30000; i++)
            {
                byte[] pkt;
                if (rng.Next(3) == 0)
                {
                    pkt = new byte[rng.Next(0, 300)];
                    rng.NextBytes(pkt);
                }
                else
                {
                    pkt = (byte[])seeds[rng.Next(seeds.Count)].Clone();
                    int flips = rng.Next(1, 4);
                    for (int f = 0; f < flips && pkt.Length > 0; f++) pkt[rng.Next(pkt.Length)] = (byte)rng.Next(256);
                    if (rng.Next(4) == 0 && pkt.Length > 1) Array.Resize(ref pkt, rng.Next(0, pkt.Length));
                }
                // From unknown connections into the host, and from the host connection into the client.
                hub.Inject(host.T, 1000 + rng.Next(40), pkt);
                hub.Inject(a.T, 0, pkt);
                if (i % 50 == 0) { host.S.Update(); a.S.Update(); }
            }
            host.S.Update();
            a.S.Update();
            Check(host.S.BadPackets > 0 && a.S.BadPackets > 0, "malformed packets were counted");
            Check(host.S.Remotes.Count <= 3, "host never exceeds capacity");
            Check(host.S.Remotes.Keys.Count == 0 || System.Linq.Enumerable.All(host.S.Remotes.Keys, id => id >= 1 && id <= 3), "host ids stay in range");
            Check(System.Linq.Enumerable.All(a.S.Remotes.Keys, id => id < Wire.MaxPlayers), "client ids stay in range");

            // Fake peers made by mutated valid Hellos must expire, and their slots must be freed.
            hub.Run(7000, Pump(host), Pump(a));
            Check(host.T.Known.Count <= 1 + 3, $"host transport does not keep junk connections (known {host.T.Known.Count})");

            // The system still works afterwards.
            var late = MakeNode(hub, SessionRole.Client, "Late");
            hub.Run(3000, Pump(host), Pump(a), Pump(late), Sender(hub, host), Sender(hub, late));
            Check(late.S.Connected, "a legitimate client can still join after the fuzz");
            Check(late.Ok > 20 && late.Bad == 0, "and receives correct poses");
        }

        private static void SteamAllModes()
        {
            foreach (AcceptMode mode in Enum.GetValues(typeof(AcceptMode)))
            {
                foreach (bool chaos in new[] { false, true })
                {
                    string tag = $"[{mode}, {(chaos ? "chaos" : "clean")}]";
                    var p = SteamPair(10 + (int)mode, mode, chaos ? 0.3 : 0, chaos ? 0.2 : 0, chaos ? 200 : 0);
                    RunSteam(p.Net, chaos ? 20000 : 6000, Pump(p.Host), Pump(p.Client), Sender(p.Net.Clock, p.Host), Sender(p.Net.Clock, p.Client));
                    Check(p.Client.S.Connected && p.Client.S.LocalId == 1, tag + " client connected as player 1");
                    Check(p.Host.S.Remotes.Count == 1 && p.Host.S.Remotes[1].Name == "Bob", tag + " host knows Bob");
                    Check(p.Host.Ok > 50 && p.Client.Ok > 50, $"{tag} poses flowed both ways (host {p.Host.Ok}, client {p.Client.Ok})");
                    Check(p.Host.Bad == 0 && p.Client.Bad == 0, tag + " no corrupted pose");
                    Check(p.Host.NonMonotonic == 0 && p.Client.NonMonotonic == 0, tag + " no stale pose applied");
                    Check(p.Host.S.BadPackets == 0 && p.Client.S.BadPackets == 0, tag + " no malformed packets (the host's knock is a valid packet)");
                    Check(p.HostT.Unauthorized == 0 && p.ClientT.Unauthorized == 0, tag + " nothing unauthorized");
                }
            }
        }

        private static void SteamStrangers()
        {
            var p = SteamPair(21, AcceptMode.Lenient, 0, 0, 0);
            var stranger = p.Net.AddUser(76561198000000300UL);
            // Even if Steam delivers a stranger's packet (we have a session with them), the transport must drop it.
            p.HostUser.Send(stranger.LocalId, new byte[] { 1 });
            p.ClientUser.Send(stranger.LocalId, new byte[] { 1 });
            stranger.Send(p.HostUser.LocalId, Wire.EncodeHello("Mallory"));
            stranger.Send(p.ClientUser.LocalId, Wire.EncodeWelcome(2, 4, "Evil"));
            stranger.Send(p.ClientUser.LocalId, Wire.EncodeBye());
            RunSteam(p.Net, 4000, Pump(p.Host), Pump(p.Client));
            Check(p.HostT.Unauthorized >= 1, "host dropped the stranger's Hello at the transport");
            Check(p.ClientT.Unauthorized >= 2, "client dropped the stranger's Welcome and Bye at the transport");
            Check(p.Host.S.Remotes.Count == 1, "host has only the real client");
            Check(p.Client.S.Connected && p.Client.S.HostScene == "", "client is unaffected by the stranger");
        }

        private static void SteamNotReady()
        {
            var p = SteamPair(22, AcceptMode.PendingOnly, 0, 0, 0);
            p.HostUser.Ready = false;
            p.ClientUser.Ready = false;
            RunSteam(p.Net, 3000, Pump(p.Host), Pump(p.Client));
            Check(!p.Client.S.Connected, "nothing happens while Steam is not ready");
            Check(p.HostT.Describe() == "steam=not-ready", "diagnostics say not ready");
            p.ClientUser.Ready = true;
            RunSteam(p.Net, 3000, Pump(p.Host), Pump(p.Client));
            Check(!p.Client.S.Connected, "still not connected while the host side is not ready");
            p.HostUser.Ready = true;
            RunSteam(p.Net, 8000, Pump(p.Host), Pump(p.Client));
            Check(p.Client.S.Connected, "connects once both sides are ready");
            Check(p.HostT.Describe().StartsWith("steam=ready"), "diagnostics say ready");
        }

        private static void SteamJunk()
        {
            var p = SteamPair(23, AcceptMode.SendFirstOnly, 0, 0, 0);
            RunSteam(p.Net, 4000, Pump(p.Host), Pump(p.Client));
            Check(p.Client.S.Connected, "baseline connected");
            var rng = new Random(77);
            for (int i = 0; i < 3000; i++)
            {
                var junk = new byte[rng.Next(0, 300)];
                rng.NextBytes(junk);
                p.Net.Route(ClientSteamId, HostSteamId, junk);   // "from the client", straight into the host
                p.Net.Route(HostSteamId, ClientSteamId, junk);   // "from the host", straight into the client
                if (i % 40 == 0) { p.Host.S.Update(); p.Client.S.Update(); }
            }
            p.Host.S.Update();
            p.Client.S.Update();
            Check(p.Host.S.BadPackets > 0 && p.Client.S.BadPackets > 0, "junk was counted");
            RunSteam(p.Net, 9000, Pump(p.Host), Pump(p.Client), Sender(p.Net.Clock, p.Host), Sender(p.Net.Clock, p.Client));
            Check(p.Client.S.Connected, "session recovered after the junk");
            Check(p.Host.Ok > 20 && p.Client.Ok > 20 && p.Host.Bad == 0 && p.Client.Bad == 0, "and carries correct poses again");
        }

        private static void SteamClientRestart()
        {
            var p = SteamPair(24, AcceptMode.PendingOnly, 0, 0, 0);
            RunSteam(p.Net, 4000, Pump(p.Host), Pump(p.Client));
            Check(p.Client.S.Connected, "baseline connected");
            byte oldId = p.Client.S.LocalId;

            // The client process is restarted: new transport and session, same Steam user, no goodbye.
            var t2 = SteamTransport.ForClient(p.ClientUser, p.Net.Clock, HostSteamId);
            var c2 = MakeNodeWith(SessionRole.Client, "Bob", t2, p.Net.Clock);
            RunSteam(p.Net, 4000, Pump(p.Host), Pump(c2));
            Check(c2.S.Connected && c2.S.LocalId == oldId, "restarted client is welcomed back under its old id");
            Check(p.Host.S.Remotes.Count == 1, "host still has exactly one remote");
        }

        private const ulong H = 76561198000000100UL, F1 = 76561198000000200UL, F2 = 76561198000000300UL, Stranger = 76561198000000400UL, Offline = 76561198000000500UL;

        private static void InviteLobby()
        {
            var clock = new FakeClock();
            var w = new FakeSocialWorld();
            var sh = w.UserOf(H, "Host Hanna");
            var s1 = w.UserOf(F1, "Friend Finn");
            var s2 = w.UserOf(F2, "Friend Fay");
            var ss = w.UserOf(Stranger, "Stranger");
            w.Names[Offline] = "Offline Olga"; // a friend who is not running the game
            w.Befriend(H, F1); w.Befriend(H, F2); w.Befriend(H, Offline); w.Befriend(F1, F2);

            var lh = new CoopLobby(sh, clock);
            var l1 = new CoopLobby(s1, clock);
            var l2 = new CoopLobby(s2, clock);
            var ls = new CoopLobby(ss, clock);
            var appeared = new List<ulong>();
            var gone = new List<ulong>();
            lh.JoinerAppeared += id => appeared.Add(id);
            lh.JoinerGone += id => gone.Add(id);
            Action tick = () => { clock.NowMs += 2100; lh.Update(); l1.Update(); l2.Update(); ls.Update(); };

            tick();
            Check(l1.HostedGames.Count == 0 && l2.HostedGames.Count == 0, "nobody hosts yet: empty list");

            lh.StartHosting();
            Check(w.Rich[H][CoopLobby.KeyHost] == H.ToString() && w.Rich[H][CoopLobby.KeyStatus] == "Co-op: hosting", "host published its presence");
            tick();
            Check(l1.HostedGames.Count == 1 && l1.HostedGames[0].HostId == H && l1.HostedGames[0].Name == "Host Hanna", "a friend sees the host with its name");
            Check(l2.HostedGames.Count == 1, "another friend sees the host too");
            Check(ls.HostedGames.Count == 0, "a stranger (not a friend) sees nothing");
            Check(lh.HostedGames.Count == 0, "the host does not list itself");

            // A spoof: Fay advertises Hanna's ID as her own host claim.
            s2.SetRich(CoopLobby.KeyHost, H.ToString());
            tick();
            Check(l1.HostedGames.Count == 1 && l1.HostedGames[0].HostId == H, "a claim that names someone else's ID is ignored");
            s2.SetRich(CoopLobby.KeyHost, "");

            // The friend presses Join.
            l1.StartJoining(H);
            tick();
            Check(appeared.Count == 1 && appeared[0] == F1, "the host noticed exactly the friend who asked");
            tick(); tick();
            Check(appeared.Count == 1, "and does not notice the same request again");

            // Strangers and wrong targets do not get in.
            ss.SetRich(CoopLobby.KeyJoin, H.ToString());
            s2.SetRich(CoopLobby.KeyJoin, F1.ToString()); // Fay asks to join Finn, not Hanna
            tick();
            Check(appeared.Count == 1, "a stranger asking, and a friend asking for someone else, are both ignored by the host");

            // Leaving.
            l1.Stop();
            tick();
            Check(gone.Count == 1 && gone[0] == F1, "when the friend stops asking the host hears about it");
            lh.Stop();
            tick();
            Check(!w.Rich[H].ContainsKey(CoopLobby.KeyHost) && l1.HostedGames.Count == 0, "stopping hosting clears the presence and the list");

            // Not ready (Steam still starting): nothing happens, nothing throws.
            sh.Ready = false;
            lh.StartHosting();
            Check(lh.State == CoopLobby.LobbyState.Idle, "hosting is refused while Steam is not ready");
        }

        private static void InviteEndToEnd()
        {
            var net = new FakeSteamNet(41) { Mode = AcceptMode.SendFirstOnly, Loss = 0.1, Dup = 0.05, MaxDelayMs = 60 };
            var hu = net.AddUser(H);
            var cu = net.AddUser(F1);
            var w = new FakeSocialWorld();
            var sh = w.UserOf(H, "Host Hanna");
            var s1 = w.UserOf(F1, "Friend Finn");
            w.Befriend(H, F1);

            var lh = new CoopLobby(sh, net.Clock);
            var l1 = new CoopLobby(s1, net.Clock);
            var hostT = SteamTransport.ForHost(hu, net.Clock, new ulong[0]); // nobody is allowed yet
            lh.JoinerAppeared += id => hostT.AddPeer(id);
            lh.JoinerGone += id => hostT.RemovePeer(id);
            var host = MakeNodeWith(SessionRole.Host, "Hanna", hostT, net.Clock);

            lh.StartHosting();
            Node client = null;
            Action step = () =>
            {
                lh.Update();
                l1.Update();
                host.S.Update();
                if (client != null) client.S.Update();
            };

            RunSteam(net, 4000, step);
            Check(l1.HostedGames.Count == 1, "the friend's list shows the host");
            Check(hostT.PeerCount == 0, "before the friend asks, nobody is allowed in");

            // Press "Join".
            ulong hostId = l1.HostedGames[0].HostId;
            l1.StartJoining(hostId);
            client = MakeNodeWith(SessionRole.Client, "Finn", SteamTransport.ForClient(cu, net.Clock, hostId), net.Clock);
            RunSteam(net, 20000, step, Sender(net.Clock, host), Sender(net.Clock, client));
            Check(client.S.Connected && client.S.LocalId == 1, "the friend is in the session");
            Check(host.S.Remotes.Count == 1 && host.S.Remotes[1].Name == "Finn", "the host sees Finn");
            Check(host.Ok > 50 && client.Ok > 50 && host.Bad == 0 && client.Bad == 0, $"poses flow both ways and are correct (host {host.Ok}, client {client.Ok})");

            // Leave: the friend stops asking, the host stops accepting and knocking.
            l1.Stop();
            RunSteam(net, 5000, step);
            Check(hostT.PeerCount == 0, "after the friend leaves the host no longer keeps their ID on its list");
        }

        private static void RealUdp()
        {
            var clock = new StopwatchClock();
            var hostT = UdpTransport.Listen(IPAddress.Loopback, 0);
            var clientT = UdpTransport.Connect(new IPEndPoint(IPAddress.Loopback, hostT.LocalPort));
            var host = new CoopSession(SessionRole.Host, hostT, clock, "Host");
            var client = new CoopSession(SessionRole.Client, clientT, clock, "Guest");
            int hostOk = 0, hostBad = 0, clientOk = 0, clientBad = 0;
            host.PoseReceived += (id, t, p) => { if (p.MaxPosDistance(BotPose.At(t, id)) < 1e-5f) hostOk++; else hostBad++; };
            client.PoseReceived += (id, t, p) => { if (p.MaxPosDistance(BotPose.At(t, id)) < 1e-5f) clientOk++; else clientBad++; };
            host.SetLocalScene("MainMenu_VR");

            // A stranger socket spraying junk at the host must not break anything.
            var stranger = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var hostEp = new IPEndPoint(IPAddress.Loopback, hostT.LocalPort);
            var rng = new Random(5);

            long end = clock.NowMs + 2500;
            long nextSend = 0;
            while (clock.NowMs < end)
            {
                host.Update();
                client.Update();
                if (clock.NowMs >= nextSend)
                {
                    nextSend = clock.NowMs + 33;
                    uint t = (uint)clock.NowMs;
                    host.SendLocalPose(BotPose.At(t, 0), t);
                    if (client.Connected) client.SendLocalPose(BotPose.At(t, client.LocalId), t);
                    var junk = new byte[rng.Next(1, 100)];
                    rng.NextBytes(junk);
                    stranger.SendTo(junk, hostEp);
                }
                Thread.Sleep(2);
            }
            Check(client.Connected && client.LocalId == 1, "client connected over real UDP");
            Check(client.HostScene == "MainMenu_VR", "scene arrived over real UDP");
            Check(hostOk > 20 && clientOk > 20, $"poses flowed both ways (host {hostOk}, client {clientOk})");
            Check(hostBad == 0 && clientBad == 0, "no corrupted pose over real UDP");
            Check(host.BadPackets > 0, "junk from the stranger was counted, not fatal");
            Check(hostT.KnownConnections <= 2, $"stranger did not leave connection slots behind (known {hostT.KnownConnections})");
            stranger.Dispose();
            client.Dispose();
            host.Dispose();
        }
    }
}
