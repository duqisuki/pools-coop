using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace PoolsCoop.Net
{
    /// <summary>Plain UDP, polled from the game's main thread (no background threads). Used for bot tests on one PC and for LAN.</summary>
    public sealed class UdpTransport : ITransport
    {
        private const int MaxHostConnections = 64;

        private readonly Socket _sock;
        private readonly bool _isHost;
        private readonly IPEndPoint _hostEp;
        private readonly Dictionary<IPEndPoint, int> _byEp = new Dictionary<IPEndPoint, int>();
        private readonly Dictionary<int, IPEndPoint> _byId = new Dictionary<int, IPEndPoint>();
        private readonly byte[] _rx = new byte[2048];
        private int _nextId = 1;

        private UdpTransport(Socket sock, bool isHost, IPEndPoint hostEp)
        {
            _sock = sock;
            _isHost = isHost;
            _hostEp = hostEp;
        }

        public int LocalPort => ((IPEndPoint)_sock.LocalEndPoint).Port;

        public int KnownConnections => _byId.Count;

        public static UdpTransport Listen(IPAddress bind, int port)
        {
            var s = NewSocket();
            s.Bind(new IPEndPoint(bind, port));
            return new UdpTransport(s, true, null);
        }

        public static UdpTransport Connect(IPEndPoint host)
        {
            var s = NewSocket();
            s.Bind(new IPEndPoint(IPAddress.Any, 0));
            return new UdpTransport(s, false, host);
        }

        private static Socket NewSocket()
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Blocking = false;
            try
            {
                // Windows reports ICMP "port unreachable" as a receive error; we do not want that for UDP.
                s.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception) { }
            return s;
        }

        public bool Receive(out int conn, out byte[] data)
        {
            conn = 0;
            data = null;
            int errors = 0;
            while (errors < 32)
            {
                try
                {
                    if (!_sock.Poll(0, SelectMode.SelectRead)) return false;
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = _sock.ReceiveFrom(_rx, ref from);
                    var ep = (IPEndPoint)from;
                    if (_isHost)
                    {
                        if (!_byEp.TryGetValue(ep, out conn))
                        {
                            if (_byId.Count >= MaxHostConnections) continue; // flood: drop datagrams from new senders
                            conn = _nextId++;
                            _byEp[ep] = conn;
                            _byId[conn] = ep;
                        }
                    }
                    else
                    {
                        if (!ep.Equals(_hostEp)) continue; // only the host may talk to a client
                        conn = 0;
                    }
                    data = new byte[n];
                    Buffer.BlockCopy(_rx, 0, data, 0, n);
                    return true;
                }
                catch (SocketException e)
                {
                    if (e.SocketErrorCode == SocketError.WouldBlock) return false;
                    errors++;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
            return false;
        }

        public void Send(int conn, byte[] data)
        {
            try
            {
                if (_isHost)
                {
                    if (_byId.TryGetValue(conn, out IPEndPoint ep)) _sock.SendTo(data, ep);
                }
                else
                {
                    _sock.SendTo(data, _hostEp);
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        public void Forget(int conn)
        {
            if (!_isHost) return;
            if (_byId.TryGetValue(conn, out IPEndPoint ep))
            {
                _byId.Remove(conn);
                _byEp.Remove(ep);
            }
        }

        public void Dispose()
        {
            try { _sock.Close(); } catch (Exception) { }
        }
    }
}
