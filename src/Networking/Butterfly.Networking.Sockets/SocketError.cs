namespace Butterfly.Networking.Sockets
{
    public enum SocketError
    {
        Unknown = -1,
        Success = 0,
        Interrupted,
        AccessDenied,
        InvalidArgument,
        TooManyOpenSockets,
        WouldBlock,
        InProgress,
        AlreadyInProgress,
        NotSocket,
        DestinationAddressRequired,
        MessageSize,
        ProtocolType,
        ProtocolOption,
        ProtocolNotSupported,
        SocketNotSupported,
        OperationNotSupported,
        ProtocolFamilyNotSupported,
        AddressFamilyNotSupported,
        AddressInUse,
        AddressNotAvailable,
        NetworkDown,
        NetworkUnreachable,
        NetworkReset,
        ConnectionAborted,
        ConnectionReset,
        NoBufferSpace,
        IsConnected,
        NotConnected,
        Shutdown,
        TimedOut,
        ConnectionRefused,
        HostDown,
        HostUnreachable,
        SystemNotReady,
        VersionNotSupported,
        NotInitialized,

        /// <summary>The call was interrupted by <see cref="Socket.Abort"/>.</summary>
        OperationAborted
    }
}
