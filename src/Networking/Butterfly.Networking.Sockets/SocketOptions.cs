namespace Butterfly.Networking.Sockets
{
    /// <summary>
    /// Options applied once, when the socket is opened.
    /// </summary>
    public class SocketOptions
    {
        /// <summary>
        /// Allows binding to a local address that still has connections in TIME_WAIT
        /// (POSIX <c>SO_REUSEADDR</c> semantics). It never lets two active sockets share a port.
        /// </summary>
        /// <remarks>
        /// Sets <c>SO_REUSEADDR</c> on Linux, Android, Apple platforms and FreeBSD. Windows already behaves
        /// this way by default, so nothing is set there; Winsock's own <c>SO_REUSEADDR</c> is deliberately
        /// not used because it allows port hijacking.
        /// </remarks>
        public bool ReuseAddress { get; init; }
    }

    public sealed class TcpSocketOptions : SocketOptions
    {
        public bool NoDelay { get; init; }
        public bool KeepAlive { get; init; }
    }

    public sealed class UdpSocketOptions : SocketOptions
    {
        /// <summary>
        /// Allows sending to broadcast addresses such as 255.255.255.255 (<c>SO_BROADCAST</c>).
        /// Without it, sending to one fails with <see cref="SocketError.AccessDenied"/>.
        /// </summary>
        public bool Broadcast { get; init; }
    }
}
