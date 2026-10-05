namespace Butterfly.Networking.Sockets
{
    public enum SocketState : byte
    {
        Created,
        Open,
        Bound,
        Listening,
        Connected,
        Closed
    }
}
