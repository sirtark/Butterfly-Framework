using Butterfly.SystemInfo.Native;
using System.Globalization;

namespace Butterfly.SystemInfo.Memory.Detection
{
    internal static class LinuxMemory
    {
        private const string CgroupRoot = "/sys/fs/cgroup";
        // cgroup v1 reports "no limit" as a huge page-aligned number instead of "max".
        private const long CgroupV1Unlimited = 0x7FFFFFFFFFFFF000;

        public static MemoryInfoSnapshot Detect()
        {
            var snapshot = FromMeminfo(SystemFiles.ReadText("/proc/meminfo"));
            var limit = CgroupLimit(SystemFiles.ReadText("/proc/self/cgroup"), SystemFiles.ReadLine);

            return snapshot with
            {
                LimitBytes = limit is { } value && (snapshot.TotalPhysicalBytes == 0 || value < snapshot.TotalPhysicalBytes) ? value : null
            };
        }

        internal static MemoryInfoSnapshot FromMeminfo(string? meminfo)
        {
            var values = KeyValueText.Parse(meminfo, ':');
            long? Read(string key) => values.TryGetValue(key, out var text) ? Kilobytes(text) : null;

            // MemAvailable exists since Linux 3.14; older kernels only allow an estimate.
            var available = Read("MemAvailable");
            if (available is null && Read("MemFree") is { } free)
                available = free + (Read("Buffers") ?? 0) + (Read("Cached") ?? 0);

            return new MemoryInfoSnapshot
            {
                TotalPhysicalBytes = Read("MemTotal") ?? 0,
                AvailablePhysicalBytes = available,
                TotalSwapBytes = Read("SwapTotal"),
                AvailableSwapBytes = Read("SwapFree"),
                PageSize = Environment.SystemPageSize
            };
        }

        // The effective limit is the smallest one between the process's cgroup and its ancestors.
        internal static long? CgroupLimit(string? selfCgroup, Func<string, string?> readLine)
        {
            long? limit = null;
            void Apply(string? text)
            {
                if (ParseLimit(text) is { } value && (limit is null || value < limit))
                    limit = value;
            }

            // cgroup v2: "0::/user.slice/user-1000.slice/session-2.scope"
            var v2Path = selfCgroup?.Split('\n').FirstOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal))?[3..].Trim();
            if (v2Path is not null)
            {
                for (var path = v2Path.TrimEnd('/'); ; path = path[..path.LastIndexOf('/')])
                {
                    Apply(readLine(CgroupRoot + path + "/memory.max"));
                    if (path.Length == 0)
                        break;
                }
            }

            // cgroup v1 (inside a container the namespace root is the container's own group).
            Apply(readLine(CgroupRoot + "/memory/memory.limit_in_bytes"));
            return limit;
        }

        internal static long? ParseLimit(string? text) =>
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 && value < CgroupV1Unlimited ? value : null;

        // "16314024 kB"
        private static long? Kilobytes(string text)
        {
            var number = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                return null;
            return text.EndsWith("kB", StringComparison.Ordinal) ? value * 1024 : value;
        }
    }
}
