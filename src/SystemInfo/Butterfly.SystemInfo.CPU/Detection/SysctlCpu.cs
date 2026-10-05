using Butterfly.SystemInfo.Native;
using System.Runtime.InteropServices;

namespace Butterfly.SystemInfo.CPU.Detection
{
    // macOS and FreeBSD.
    internal static class SysctlCpu
    {
        public static void Fill(CPUFacts facts)
        {
            if (OperatingSystem.IsMacOS())
                FillMacOS(facts);
            else
                FillFreeBSD(facts);
        }

        private static void FillMacOS(CPUFacts facts)
        {
            var appleSilicon = RuntimeInformation.OSArchitecture == Architecture.Arm64;

            facts.Model ??= Sysctl.GetString("machdep.cpu.brand_string");
            facts.Vendor ??= Sysctl.GetString("machdep.cpu.vendor") ?? (appleSilicon ? "Apple" : null);
            facts.LogicalProcessors = (int)(Sysctl.GetInt64("hw.logicalcpu") ?? 0);
            facts.PhysicalCores = (int)(Sysctl.GetInt64("hw.physicalcpu") ?? 0);
            facts.Packages = (int)(Sysctl.GetInt64("hw.packages") ?? 0);

            // Hypervisor.framework is usable (it requires VT-x + EPT, or the Apple Silicon EL2).
            if (Sysctl.GetInt64("kern.hv_support") == 1)
            {
                facts.HardwareVirtualization ??= true;
                facts.FirmwareVirtualization ??= true;
                facts.SecondLevelAddressTranslation ??= true;
            }

            if (Sysctl.GetInt64("kern.hv_vmm_present") is { } present)
            {
                facts.HypervisorPresent ??= present != 0;
                if (present != 0 && appleSilicon && facts.HypervisorVendor == HypervisorVendor.Unknown)
                    facts.HypervisorVendor = HypervisorVendor.AppleHypervisor;
            }
        }

        private static void FillFreeBSD(CPUFacts facts)
        {
            facts.Model ??= Sysctl.GetString("hw.model");
            facts.LogicalProcessors = (int)(Sysctl.GetInt64("hw.ncpu") ?? 0);
            facts.PhysicalCores = (int)(Sysctl.GetInt64("kern.smp.cores") ?? 0);

            if (Sysctl.GetString("kern.vm_guest") is { } guest)
            {
                var vendor = Hypervisors.FromFreeBsdVmGuest(guest);
                facts.HypervisorPresent ??= vendor != HypervisorVendor.None;
                if (facts.HypervisorVendor == HypervisorVendor.Unknown)
                    facts.HypervisorVendor = vendor;
            }
        }
    }
}
