using Butterfly.Chrysalis;
using Butterfly.Chrysalis.Binary;
using Butterfly.Chrysalis.Grpc;
using Butterfly.Chrysalis.Http;
using Butterfly.Chrysalis.JsonRpc;
using Butterfly.Chrysalis.Rest;
using Butterfly.Chrysalis.Soap;
using Butterfly.Chrysalis.XmlRpc;
using Butterfly.Networking.Sockets;
using System.Text;

// One service, written once.
var chrysalis = new ChrysalisServer().Expose<IGreeter>(new Greeter());
chrysalis.Use(async (context, next) =>
{
    var result = await next(context);
    Console.WriteLine($"  [{context.Protocol}] {context.Operation.FullName}");
    return result;
});

// Exposed over HTTP (REST, SOAP, JSON-RPC, XML-RPC and gRPC on one port)...
var options = new HttpServerOptions();
options.Endpoints.Add(HttpEndpoint.Loopback(0));
await using var http = new HttpServer(options)
    .MapRest(chrysalis, "/api")
    .MapSoap(chrysalis, "/soap")
    .MapJsonRpc(chrysalis, "/rpc")
    .MapXmlRpc(chrysalis, "/xmlrpc")
    .MapGrpc(chrysalis);
http.Start();

// ...and over the binary protocol.
var binaryOptions = new ChrysalisBinaryServerOptions();
binaryOptions.Endpoints.Add((SocketAddress.Loopback(AddressFamily.IPv4, 0), null));
await using var binary = new ChrysalisBinaryServer(chrysalis, binaryOptions);
binary.Start();

var port = http.Endpoints[0].Address.Port;
Console.WriteLine($"HTTP on {http.Endpoints[0]}, binary on {binary.Endpoints[0]}");

using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
Console.WriteLine("REST:     " + await client.GetStringAsync("api/Greeter/hello/Ada"));

var jsonRpc = await client.PostAsync("rpc", new StringContent("""{"jsonrpc":"2.0","method":"Greeter.Hello","params":["Grace"],"id":1}""", Encoding.UTF8, "application/json"));
Console.WriteLine("JSON-RPC: " + await jsonRpc.Content.ReadAsStringAsync());

await using (var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", binary.Endpoints[0].Port))
{
    IGreeter greeter = connection.CreateClient<IGreeter>();
    Console.WriteLine("Binary:   " + (await greeter.Hello("Linus")).Message);
}

Console.WriteLine();
Console.WriteLine(ProtoFile.Generate(ChrysalisRegistry.GetService<IGreeter>()));
Console.WriteLine($"WSDL: {(await client.GetStringAsync("soap/Greeter?wsdl")).Length} characters at /soap/Greeter?wsdl");

[ChrysalisService(Namespace = "greeter.v1")]
public interface IGreeter
{
    [HttpGet("hello/{name}")]
    Task<Greeting> Hello(string name);
}

public sealed record Greeting(string Message, DateTimeOffset At);

public sealed class Greeter : IGreeter
{
    public Task<Greeting> Hello(string name) =>
        Task.FromResult(new Greeting($"Hello, {name}!", new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)));
}
