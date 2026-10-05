namespace Butterfly.Communication
{
    /// <summary>Base class of the errors raised by Butterfly protocol clients.</summary>
    public class CommunicationException : Exception
    {
        public CommunicationException(string message, Exception? innerException = null) : base(message, innerException) { }
    }

    /// <summary>The host could not be resolved or none of its addresses accepted the connection.</summary>
    public class ConnectionFailedException : CommunicationException
    {
        public ConnectionFailedException(string host, int port, string message, Exception? innerException = null)
            : base($"Could not connect to {host}:{port}: {message}", innerException)
        {
            Host = host;
            Port = port;
        }

        public string Host { get; }
        public int Port { get; }
    }

    /// <summary>The server answered something the protocol does not allow at that point.</summary>
    public class ProtocolViolationException : CommunicationException
    {
        public ProtocolViolationException(string message, Exception? innerException = null) : base(message, innerException) { }
    }
}
