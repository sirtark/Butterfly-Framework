using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Butterfly.Networking.Sockets;

namespace Butterfly.Communication.Testing
{
    /// <summary>
    /// A loopback TCP server that runs a script for each accepted connection on a background thread.
    /// Failures inside the script are rethrown by <see cref="Wait"/> (and by Dispose).
    /// </summary>
    public sealed class TestServer : IDisposable
    {
        private readonly TcpSocket _listener;
        private readonly Thread _thread;
        private readonly List<Exception> _errors = [];
        private readonly int _connections;

        public TestServer(Action<ServerSession> script, int connections = 1, AddressFamily family = AddressFamily.IPv4)
            : this((session, _) => script(session), connections, family)
        {
        }

        public TestServer(Action<ServerSession, int> script, int connections = 1, AddressFamily family = AddressFamily.IPv4)
        {
            _connections = connections;
            _listener = new TcpSocket(family);
            _listener.Bind(SocketAddress.Loopback(family, 0));
            _listener.Listen();
            Port = _listener.LocalAddress.Port;

            _thread = new Thread(() =>
            {
                for (int i = 0; i < _connections; i++)
                {
                    try
                    {
                        using var session = new ServerSession(_listener.Accept());
                        script(session, i);
                    }
                    catch (Exception ex)
                    {
                        lock (_errors)
                            _errors.Add(ex);
                        return;
                    }
                }
            }) { IsBackground = true, Name = "TestServer" };
            _thread.Start();
        }

        public ushort Port { get; }

        public string Host => "127.0.0.1";

        /// <summary>Waits for every scripted connection to finish and rethrows the first script failure.</summary>
        public void Wait(TimeSpan? timeout = null)
        {
            if (!_thread.Join(timeout ?? TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The test server script did not finish.");

            lock (_errors)
            {
                if (_errors.Count > 0)
                    throw new InvalidOperationException("The test server script failed: " + _errors[0].Message, _errors[0]);
            }
        }

        public void Dispose()
        {
            _listener.Dispose();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    public sealed class ServerSession : IDisposable
    {
        public ServerSession(TcpSocket socket)
        {
            Socket = socket;
            socket.ReceiveTimeout = TimeSpan.FromSeconds(10);
            Stream = new SocketStream(socket);
            Reader = new ProtocolReader(Stream);
        }

        public TcpSocket Socket { get; }
        public Stream Stream { get; private set; }
        public ProtocolReader Reader { get; private set; }

        public string ReadLine() => Reader.ReadLine() ?? throw new EndOfStreamException("The client closed the connection.");

        /// <summary>Reads a line and fails the script if it is not <paramref name="expected"/>.</summary>
        public string Expect(string expected)
        {
            string line = ReadLine();
            if (line != expected)
                throw new InvalidOperationException($"Expected '{expected}' but the client sent '{line}'.");
            return line;
        }

        public string ExpectStartsWith(string prefix)
        {
            string line = ReadLine();
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Expected a line starting with '{prefix}' but the client sent '{line}'.");
            return line;
        }

        /// <summary>Reads lines until an empty one (HTTP headers) and returns them.</summary>
        public List<string> ReadUntilEmptyLine()
        {
            var lines = new List<string>();
            for (string line = ReadLine(); line.Length > 0; line = ReadLine())
                lines.Add(line);
            return lines;
        }

        public void WriteLine(string line) => Write(line + "\r\n");

        public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

        public void Write(ReadOnlySpan<byte> bytes)
        {
            Stream.Write(bytes);
            Stream.Flush();
        }

        public byte[] ReadBytes(int count) => Reader.ReadBytes(count);

        public void StartTls(X509Certificate2 certificate)
        {
            var ssl = new SslStream(Stream, leaveInnerStreamOpen: false);
            ssl.AuthenticateAsServer(certificate);
            Stream = ssl;
            Reader = new ProtocolReader(ssl);
        }

        public void Dispose() => Stream.Dispose();
    }

    /// <summary>A loopback UDP server that answers each datagram with the handler's result (null = stay silent).</summary>
    public sealed class UdpTestServer : IDisposable
    {
        private readonly UdpSocket _socket;
        private readonly Thread _thread;

        public UdpTestServer(Func<byte[], byte[]?> handler, ushort port = 0)
        {
            _socket = new UdpSocket();
            _socket.Bind(SocketAddress.Loopback(AddressFamily.IPv4, port));
            Address = _socket.LocalAddress;

            _thread = new Thread(() =>
            {
                byte[] buffer = new byte[65535];
                try
                {
                    while (true)
                    {
                        int received = _socket.ReceiveFrom(buffer, out SocketAddress client);
                        Received++;
                        byte[]? reply = handler(buffer[..received]);
                        if (reply is not null)
                            _socket.SendTo(reply, client);
                    }
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                }
            }) { IsBackground = true, Name = "UdpTestServer" };
            _thread.Start();
        }

        public SocketAddress Address { get; }

        public int Received { get; private set; }

        public void Dispose() => _socket.Dispose();
    }
}
