using Butterfly.Chrysalis;
using Butterfly.SystemInfo.CPU;
using Butterfly.SystemInfo.Memory;
using Butterfly.SystemInfo.OS;

namespace Butterfly.SystemInfo.Chrysalis
{
    /// <summary>The machine a Chrysalis server runs on: GET system/cpu, system/memory and system/os over REST.</summary>
    [ChrysalisService(Namespace = "butterfly.systeminfo.v1", Route = "system")]
    public interface ISystemInfoService
    {
        [HttpGet("cpu")]
        CpuInfo GetCpu();

        [HttpGet("memory")]
        MemoryInfo GetMemory();

        [HttpGet("os")]
        OperatingSystemInfo GetOperatingSystem();
    }

    // Snapshots are created by their providers only; these records are what travels.
    public sealed record CpuVirtualizationInfo(bool? HardwareVirtualizationSupported, bool? FirmwareVirtualizationEnabled, bool? SecondLevelAddressTranslation,
        bool? HypervisorPresent, HypervisorVendor HypervisorVendor, string? HypervisorSignature, bool? IsVirtualMachine);

    public sealed record CpuInfo(CPUArchitecture Architecture, CPUInstructionSet InstructionSet, IReadOnlyList<string> Features, string? Vendor, string? Model,
        int LogicalProcessorCount, int PhysicalCoreCount, int PackageCount, int ProcessAvailableProcessorCount, CpuVirtualizationInfo Virtualization)
    {
        public static CpuInfo From(CPUInfoSnapshot cpu) => new(cpu.Architecture, cpu.InstructionSet,
            [.. Enum.GetValues<CPUFeatures>().Where(feature => feature != CPUFeatures.None && cpu.Features.HasFlag(feature)).Select(feature => feature.ToString())],
            cpu.Vendor, cpu.Model, cpu.LogicalProcessorCount, cpu.PhysicalCoreCount, cpu.PackageCount, cpu.ProcessAvailableProcessorCount,
            new CpuVirtualizationInfo(cpu.Virtualization.HardwareVirtualizationSupported, cpu.Virtualization.FirmwareVirtualizationEnabled,
                cpu.Virtualization.SecondLevelAddressTranslation, cpu.Virtualization.HypervisorPresent, cpu.Virtualization.HypervisorVendor,
                cpu.Virtualization.HypervisorSignature, cpu.Virtualization.IsVirtualMachine));
    }

    public sealed record MemoryInfo(long TotalPhysicalBytes, long? AvailablePhysicalBytes, long? TotalSwapBytes, long? AvailableSwapBytes, int PageSize, long? LimitBytes)
    {
        public static MemoryInfo From(MemoryInfoSnapshot memory) => new(memory.TotalPhysicalBytes, memory.AvailablePhysicalBytes, memory.TotalSwapBytes,
            memory.AvailableSwapBytes, memory.PageSize, memory.LimitBytes);
    }

    public sealed record OperatingSystemInfo(OSFamily Family, OSKernelFamily KernelFamily, string? Name, string? Version, string? DisplayVersion,
        string? KernelVersion, string? DistributionId, bool Is64Bit, bool IsContainer, bool IsWsl)
    {
        public static OperatingSystemInfo From(OSInfoSnapshot os) => new(os.Family, os.KernelFamily, os.Name, os.Version?.ToString(), os.DisplayVersion,
            os.KernelVersion, os.DistributionId, os.Is64Bit, os.IsContainer, os.IsWsl);
    }

    public sealed class SystemInfoService : ISystemInfoService
    {
        public CpuInfo GetCpu() => CpuInfo.From(CPUInfoSnapshotProvider.Get());
        public MemoryInfo GetMemory() => MemoryInfo.From(MemoryInfoSnapshotProvider.Get());
        public OperatingSystemInfo GetOperatingSystem() => OperatingSystemInfo.From(OSInfoSnapshotProvider.Get());
    }
}
