using System;

namespace PoolsCoop.Net
{
    /// <summary>
    /// A way to move datagrams between players. The session does not care whether it is UDP on loopback
    /// (bot tests on one PC) or Steam relay (friends). Connection ids are small ints chosen by the transport:
    /// on a client transport, connection 0 is always the host; on a host transport, each remote endpoint gets its own id.
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>Non-blocking. Returns false when nothing is waiting.</summary>
        bool Receive(out int conn, out byte[] data);

        /// <summary>Best-effort send. Must never throw for a network error.</summary>
        void Send(int conn, byte[] data);

        /// <summary>The session no longer needs this connection (left, timed out, or sent junk). Frees its slot.</summary>
        void Forget(int conn);
    }
}
