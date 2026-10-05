using Butterfly.SystemInfo.CPU;
using Butterfly.SystemInfo.Memory;
using Butterfly.SystemInfo.OS;

namespace Butterfly.Virtualization
{
    public enum VirtualizationIssueKind : byte
    {
        UnsupportedOperatingSystem,
        /// <summary>The Windows edition does not include Hyper-V (Home).</summary>
        HypervisorNotIncludedInEdition,
        HardwareVirtualizationUnsupported,
        /// <summary>The host is itself a virtual machine whose hypervisor does not expose nested virtualization.</summary>
        NestedVirtualizationUnavailable,
        DisabledInFirmware,
        SecondLevelAddressTranslationMissing,
        /// <summary>The processor capabilities could not be read (e.g. not x86, or hidden by a hypervisor).</summary>
        CapabilitiesUnverified,
        LowMemory
    }

    public sealed record VirtualizationIssue(VirtualizationIssueKind Kind, bool IsBlocking, string Message);

    public sealed record VirtualizationReadiness
    {
        internal VirtualizationReadiness()
        { }

        /// <summary>The hypervisor that fits this operating system.</summary>
        public HypervisorPlatform Platform { get; internal init; }
        /// <summary>This host is itself a virtual machine: its machines would be nested.</summary>
        public bool IsNested { get; internal init; }
        public IReadOnlyList<VirtualizationIssue> Issues { get; internal init; } = [];

        /// <summary>No blocking issue was found. Warnings may remain in <see cref="Issues"/>.</summary>
        public bool IsReady => !Issues.Any(issue => issue.IsBlocking);
    }

    /// <summary>Checks whether this machine can run virtual machines, before any hypervisor is installed or queried.</summary>
    public static class VirtualizationHost
    {
        private const long LowMemoryBytes = 4L * 1024 * 1024 * 1024;

        public static VirtualizationReadiness CheckReadiness()
        {
            var cpu = CPUInfoSnapshotProvider.Get();
            var os = OSInfoSnapshotProvider.Get();
            var memory = MemoryInfoSnapshotProvider.Get();
            var virtualization = cpu.Virtualization;

            return Evaluate(new HostFacts(
                os.Family,
                os.Name,
                virtualization.HardwareVirtualizationSupported,
                virtualization.FirmwareVirtualizationEnabled,
                virtualization.SecondLevelAddressTranslation,
                virtualization.IsVirtualMachine == true,
                memory.TotalPhysicalBytes));
        }

        internal readonly record struct HostFacts(
            OSFamily Family,
            string? OSName,
            bool? HardwareVirtualization,
            bool? FirmwareVirtualization,
            bool? SecondLevelAddressTranslation,
            bool IsVirtualMachine,
            long TotalMemoryBytes);

        internal static VirtualizationReadiness Evaluate(HostFacts host)
        {
            var issues = new List<VirtualizationIssue>();
            void Add(VirtualizationIssueKind kind, bool blocking, string message) => issues.Add(new VirtualizationIssue(kind, blocking, message));

            var platform = host.Family switch
            {
                OSFamily.Windows => HypervisorPlatform.HyperV,
                OSFamily.Linux => HypervisorPlatform.KVM,
                OSFamily.MacOS => HypervisorPlatform.AppleVirtualization,
                OSFamily.FreeBSD => HypervisorPlatform.Bhyve,
                _ => HypervisorPlatform.Unknown
            };

            if (platform == HypervisorPlatform.Unknown)
                Add(VirtualizationIssueKind.UnsupportedOperatingSystem, true, "There is no supported hypervisor for this operating system.");

            if (platform == HypervisorPlatform.HyperV && host.OSName?.Contains("Home", StringComparison.OrdinalIgnoreCase) == true)
                Add(VirtualizationIssueKind.HypervisorNotIncludedInEdition, true, $"{host.OSName} does not include Hyper-V; it needs Pro, Enterprise, Education or Server.");

            if (host.HardwareVirtualization == false)
            {
                if (host.IsVirtualMachine)
                    Add(VirtualizationIssueKind.NestedVirtualizationUnavailable, true, "This machine is a virtual machine and its hypervisor does not expose nested virtualization.");
                else
                    Add(VirtualizationIssueKind.HardwareVirtualizationUnsupported, true, "The processor does not implement hardware virtualization (Intel VT-x / AMD-V).");
            }
            else if (host.HardwareVirtualization is null)
            {
                Add(VirtualizationIssueKind.CapabilitiesUnverified, false, "Hardware virtualization support could not be verified.");
            }

            if (host.FirmwareVirtualization == false && host.HardwareVirtualization != false)
                Add(VirtualizationIssueKind.DisabledInFirmware, true, "Hardware virtualization is disabled in the firmware (BIOS/UEFI) settings.");

            // Hyper-V refuses to run without SLAT; KVM works, only slower.
            if (host.SecondLevelAddressTranslation == false && host.HardwareVirtualization != false)
                Add(VirtualizationIssueKind.SecondLevelAddressTranslationMissing, platform == HypervisorPlatform.HyperV,
                    "The processor lacks Second Level Address Translation (Intel EPT / AMD NPT).");

            if (host.TotalMemoryBytes is > 0 and < LowMemoryBytes)
                Add(VirtualizationIssueKind.LowMemory, false, "The host has less than 4 GiB of memory; there is little left for virtual machines.");

            return new VirtualizationReadiness
            {
                Platform = platform,
                IsNested = host.IsVirtualMachine,
                Issues = issues
            };
        }
    }
}
