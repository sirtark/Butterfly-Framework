namespace Butterfly.SystemInfo.Memory
{
    public sealed record MemoryInfoSnapshot
    {
        internal MemoryInfoSnapshot()
        { }

        /// <summary>Physical memory installed and usable by the operating system. 0 when unknown.</summary>
        public long TotalPhysicalBytes { get; internal init; }
        /// <summary>Physical memory that can be given to new allocations without swapping (free plus reclaimable caches).</summary>
        public long? AvailablePhysicalBytes { get; internal init; }

        /// <summary>
        /// Swap space (page file on Windows). Windows reports a commit limit instead of a page file size, so there it is
        /// derived as commit limit minus physical memory.
        /// </summary>
        public long? TotalSwapBytes { get; internal init; }
        public long? AvailableSwapBytes { get; internal init; }

        public int PageSize { get; internal init; }

        /// <summary>Memory limit imposed on this process's control group (container limits on Linux). Null when unlimited.</summary>
        public long? LimitBytes { get; internal init; }

        public static MemoryInfoSnapshot Unknown => new();
    }
}
