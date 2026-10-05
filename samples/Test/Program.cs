using Butterfly.Networking.Sockets;

using var tcpListener = new TcpSocket(AddressFamily.IPv4);
tcpListener.Bind(SocketAddress.Loopback(AddressFamily.IPv4, 5000));
tcpListener.Listen();

Console.WriteLine("Listening");

using var client = new TcpSocket(AddressFamily.IPv4);
client.Connect(tcpListener.LocalAddress);

Console.WriteLine("Connected");

using var acceptedClient = tcpListener.Accept();

Console.WriteLine("Sending");
Console.WriteLine(acceptedClient.Send([255, 255, 127, 127]));

byte[] buffer = new byte[2];

Console.WriteLine("Receiving");
Console.WriteLine(client.Receive(buffer));

Console.WriteLine($"Received: {string.Join(", ", buffer.Select(b => b.ToString("X")))}");

Console.WriteLine("Receiving");
Console.WriteLine(client.Receive(buffer));

Console.WriteLine($"Received: {string.Join(", ", buffer.Select(b => b.ToString("X")))}");