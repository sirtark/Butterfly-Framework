using Butterfly.Chrysalis;
using Butterfly.Chrysalis.Binary;
using Butterfly.Chrysalis.Http;
using Butterfly.Chrysalis.Rest;
using Butterfly.Networking.Sockets;
using Butterfly.SystemInfo.CPU;
using System.Text.Json;

namespace Butterfly.SystemInfo.Chrysalis.Tests
{
    public class SystemInfoServiceTests : IAsyncLifetime
    {
        private readonly ChrysalisServer chrysalis = new ChrysalisServer().Expose<ISystemInfoService>(new SystemInfoService());
        private ChrysalisBinaryServer binary = null!;
        private HttpServer http = null!;

        public Task InitializeAsync()
        {
            var options = new ChrysalisBinaryServerOptions();
            options.Endpoints.Add((SocketAddress.Loopback(AddressFamily.IPv4, 0), null));
            binary = new ChrysalisBinaryServer(chrysalis, options);
            binary.Start();

            var httpOptions = new HttpServerOptions();
            httpOptions.Endpoints.Add(HttpEndpoint.Loopback(0));
            http = new HttpServer(httpOptions).MapRest(chrysalis);
            http.Start();
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            await binary.StopAsync();
            await http.StopAsync();
        }

        [Fact]
        public async Task ReportsTheMachineThroughTheBinaryProtocol()
        {
            await using var connection = await ChrysalisBinaryClient.ConnectAsync("127.0.0.1", binary.Endpoints[0].Port);
            var system = connection.CreateClient<ISystemInfoService>();
            var local = CPUInfoSnapshotProvider.Get();

            var cpu = system.GetCpu();
            var memory = system.GetMemory();
            var os = system.GetOperatingSystem();

            Assert.Equal(CpuInfo.From(local) with { Features = [] }, cpu with { Features = [] });
            Assert.Equal(CpuInfo.From(local).Features, cpu.Features);
            Assert.True(memory.TotalPhysicalBytes > 0);
            Assert.Equal(Environment.SystemPageSize, memory.PageSize);
            Assert.Equal(Environment.Is64BitOperatingSystem, os.Is64Bit);
            Assert.False(string.IsNullOrWhiteSpace(os.Name));
        }

        [Fact]
        public async Task AnswersRestGets()
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{http.Endpoints[0].Address.Port}/") };

            using var cpu = JsonDocument.Parse(await client.GetStringAsync("/api/system/cpu"));
            using var os = JsonDocument.Parse(await client.GetStringAsync("/api/system/os"));

            Assert.Equal(Environment.ProcessorCount, cpu.RootElement.GetProperty("processAvailableProcessorCount").GetInt32());
            Assert.True(cpu.RootElement.GetProperty("virtualization").TryGetProperty("hypervisorVendor", out _));
            Assert.Equal(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : os.RootElement.GetProperty("family").GetString(),
                os.RootElement.GetProperty("family").GetString());
        }
    }
}
