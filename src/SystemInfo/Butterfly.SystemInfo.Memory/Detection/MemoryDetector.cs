using Butterfly.SystemInfo.Native;
using System.Buffers.Binary;
using System.Runtime.Versioning;

namespace Butterfly.SystemInfo.Memory.Detection
{
    internal static class MemoryDetector
    {
        public static MemoryInfoSnapshot Detect()
        {
            if (OperatingSystem.IsWindows())
                return Windows();
            if (OperatingSystem.IsLinux())
                return LinuxMemory.Detect();
            if (OperatingSystem.IsMacOS())
                return MacOS();
            if (OperatingSystem.IsFreeBSD())
                return FreeBSD();

            return new MemoryInfoSnapshot { PageSize = Environment.SystemPageSize };
        }

        [SupportedOSPlatform("windows")]
        private static unsafe MemoryInfoSnapshot Windows()
        {
            var status = new Kernel32.MemoryStatusEx { Length = (uint)sizeof(Kernel32.MemoryStatusEx) };
            if (!Kernel32.GlobalMemoryStatusEx(&status))
                return new MemoryInfoSnapshot { PageSize = Environment.SystemPageSize };

            return new MemoryInfoSnapshot
            {
                TotalPhysicalBytes = (long)status.TotalPhys,
                AvailablePhysicalBytes = (long)status.AvailPhys,
                TotalSwapBytes = (long)(status.TotalPageFile > status.TotalPhys ? status.TotalPageFile - status.TotalPhys : 0),
                AvailableSwapBytes = (long)(status.AvailPageFile > status.AvailPhys ? status.AvailPageFile - status.AvailPhys : 0),
                PageSize = Environment.SystemPageSize
            };
        }

        private static MemoryInfoSnapshot MacOS()
        {
            var total = Sysctl.GetInt64("hw.memsize") ?? 0;

            // Percentage of memory available before the system starts reclaiming (what Activity Monitor calls pressure).
            long? available = Sysctl.GetInt64("kern.memorystatus_level") is { } level and >= 0 and <= 100 ? total * level / 100 : null;

            // struct xsw_usage { uint64 xsu_total; uint64 xsu_avail; uint64 xsu_used; ... }
            long? swapTotal = null, swapAvailable = null;
            if (Sysctl.GetBytes("vm.swapusage") is { Length: >= 16 } swap)
            {
                swapTotal = (long)BinaryPrimitives.ReadUInt64LittleEndian(swap);
                swapAvailable = (long)BinaryPrimitives.ReadUInt64LittleEndian(swap.AsSpan(8));
            }

            return new MemoryInfoSnapshot
            {
                TotalPhysicalBytes = total,
                AvailablePhysicalBytes = available,
                TotalSwapBytes = swapTotal,
                AvailableSwapBytes = swapAvailable,
                PageSize = Environment.SystemPageSize
            };
        }

        private static MemoryInfoSnapshot FreeBSD()
        {
            var pageSize = Sysctl.GetInt64("hw.pagesize") ?? Environment.SystemPageSize;
            long? free = Sysctl.GetInt64("vm.stats.vm.v_free_count");
            long? inactive = Sysctl.GetInt64("vm.stats.vm.v_inactive_count");

            return new MemoryInfoSnapshot
            {
                TotalPhysicalBytes = Sysctl.GetInt64("hw.physmem") ?? 0,
                AvailablePhysicalBytes = free is null ? null : (free + (inactive ?? 0)) * pageSize,
                TotalSwapBytes = Sysctl.GetInt64("vm.swap_total"),
                PageSize = (int)pageSize
            };
        }
    }
}
