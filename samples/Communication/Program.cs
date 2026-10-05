using Butterfly.Communication.Dns;
using Butterfly.Communication.Http;

// Butterfly's own DNS client, using the servers configured in the operating system.
string host = args.Length > 0 ? args[0] : "example.com";
foreach (var address in DnsClient.Default.ResolveAddresses(host))
    Console.WriteLine($"{host} -> {address.AddressToString()}");

// HttpClient here is Butterfly's: the package removes the conflicting implicit "using System.Net.Http".
using var client = new HttpClient();
using HttpResponse response = client.Get($"https://{host}/");

Console.WriteLine($"{response} ({response.ContentType})");
Console.WriteLine($"{response.ReadAsString().Length} characters received");
