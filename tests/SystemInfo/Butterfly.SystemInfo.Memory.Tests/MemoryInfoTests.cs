using Butterfly.SystemInfo.Memory.Detection;

namespace Butterfly.SystemInfo.Memory.Tests
{
    public class MemoryInfoTests
    {
        [Fact]
        public void ReadsTheMemoryOfTheMachine()
        {
            var memory = MemoryInfoSnapshotProvider.Get();

            Assert.True(memory.TotalPhysicalBytes > 0);
            Assert.NotNull(memory.AvailablePhysicalBytes);
            Assert.InRange(memory.AvailablePhysicalBytes.Value, 1, memory.TotalPhysicalBytes);
            Assert.Equal(Environment.SystemPageSize, memory.PageSize);
            if (memory.TotalSwapBytes is { } swap && memory.AvailableSwapBytes is { } availableSwap)
                Assert.InRange(availableSwap, 0, swap);
        }

        [Fact]
        public void ReadsMeminfo()
        {
            const string meminfo = """
                MemTotal:       16314024 kB
                MemFree:         1022344 kB
                MemAvailable:    9876544 kB
                Buffers:          123456 kB
                Cached:          7000000 kB
                SwapTotal:       2097148 kB
                SwapFree:        2097148 kB
                """;

            var memory = LinuxMemory.FromMeminfo(meminfo);

            Assert.Equal(16314024L * 1024, memory.TotalPhysicalBytes);
            Assert.Equal(9876544L * 1024, memory.AvailablePhysicalBytes);
            Assert.Equal(2097148L * 1024, memory.TotalSwapBytes);
            Assert.Equal(2097148L * 1024, memory.AvailableSwapBytes);
        }

        [Fact]
        public void EstimatesAvailableMemoryOnOldKernels()
        {
            var memory = LinuxMemory.FromMeminfo("MemTotal: 1000 kB\nMemFree: 100 kB\nBuffers: 20 kB\nCached: 300 kB\n");

            Assert.Equal(420L * 1024, memory.AvailablePhysicalBytes);
            Assert.Null(memory.TotalSwapBytes);
        }

        [Fact]
        public void TakesTheSmallestCgroupLimitUpTheHierarchy()
        {
            var files = new Dictionary<string, string>
            {
                ["/sys/fs/cgroup/kubepods/pod1/container1/memory.max"] = "max",
                ["/sys/fs/cgroup/kubepods/pod1/memory.max"] = "536870912",
                ["/sys/fs/cgroup/kubepods/memory.max"] = "1073741824"
            };

            var limit = LinuxMemory.CgroupLimit("0::/kubepods/pod1/container1\n", path => files.GetValueOrDefault(path));

            Assert.Equal(536870912, limit);
        }

        [Fact]
        public void ReadsCgroupV1Limits()
        {
            var files = new Dictionary<string, string> { ["/sys/fs/cgroup/memory/memory.limit_in_bytes"] = "268435456" };

            Assert.Equal(268435456, LinuxMemory.CgroupLimit("12:memory:/docker/3f2a\n", path => files.GetValueOrDefault(path)));
        }

        [Theory]
        [InlineData("max", null)]
        [InlineData("9223372036854771712", null)]
        [InlineData("0", null)]
        [InlineData("garbage", null)]
        [InlineData(null, null)]
        [InlineData("268435456", 268435456L)]
        public void ParsesCgroupLimits(string? text, long? expected) => Assert.Equal(expected, LinuxMemory.ParseLimit(text));
    }
}
