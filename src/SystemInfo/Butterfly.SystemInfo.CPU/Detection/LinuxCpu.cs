using Butterfly.SystemInfo.Native;
using System.Globalization;

namespace Butterfly.SystemInfo.CPU.Detection
{
    internal static class LinuxCpu
    {
        private const string CpuDirectory = "/sys/devices/system/cpu/";

        public static void Fill(CPUFacts facts)
        {
            FillFromCpuInfo(facts, KeyValueText.Parse(SystemFiles.ReadText("/proc/cpuinfo"), ':'));
            CountProcessors(facts);

            // The KVM device only exists when the kernel could enable hardware virtualization.
            if (File.Exists("/dev/kvm"))
            {
                facts.HardwareVirtualization ??= true;
                facts.FirmwareVirtualization ??= true;
            }

            if (SystemFiles.ReadLine("/sys/hypervisor/type") == "xen" && facts.HypervisorVendor == HypervisorVendor.Unknown)
            {
                facts.HypervisorPresent = true;
                facts.HypervisorVendor = HypervisorVendor.Xen;
            }

            facts.SystemManufacturer = SystemFiles.ReadLine("/sys/class/dmi/id/sys_vendor");
            facts.SystemProduct = SystemFiles.ReadLine("/sys/class/dmi/id/product_name");
        }

        internal static void FillFromCpuInfo(CPUFacts facts, IReadOnlyDictionary<string, string> cpuInfo)
        {
            facts.Vendor ??= cpuInfo.GetValueOrDefault("vendor_id") ?? ArmImplementers.Name(cpuInfo.GetValueOrDefault("CPU implementer"));
            facts.Model ??= cpuInfo.GetValueOrDefault("model name") ?? cpuInfo.GetValueOrDefault("uarch") ?? cpuInfo.GetValueOrDefault("cpu model");

            // x86 lists "flags", ARM lists "Features".
            var flags = (cpuInfo.GetValueOrDefault("flags") ?? cpuInfo.GetValueOrDefault("Features") ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);

            if (flags.Contains("sve"))
                facts.Features |= CPUFeatures.SVE;
            // The kernel only lists ept/npt when the hypervisor support is usable.
            if (flags.Contains("ept") || flags.Contains("npt"))
                facts.SecondLevelAddressTranslation ??= true;
        }

        private static void CountProcessors(CPUFacts facts)
        {
            var online = CpuList.Parse(SystemFiles.ReadLine(CpuDirectory + "online"));
            if (online.Count == 0)
                return;

            var cores = new HashSet<(string? Package, string Core)>();
            var packages = new HashSet<string>();
            foreach (var cpu in online)
            {
                var topology = CpuDirectory + "cpu" + cpu.ToString(CultureInfo.InvariantCulture) + "/topology/";
                var package = SystemFiles.ReadLine(topology + "physical_package_id");
                var core = SystemFiles.ReadLine(topology + "core_id");
                if (core is not null)
                    cores.Add((package, core));
                if (package is not null)
                    packages.Add(package);
            }

            facts.LogicalProcessors = online.Count;
            facts.PhysicalCores = cores.Count;
            facts.Packages = packages.Count;
        }
    }

    // Kernel CPU lists: "0-3,6,8-11".
    internal static class CpuList
    {
        public static IReadOnlyList<int> Parse(string? text)
        {
            var cpus = new List<int>();
            if (string.IsNullOrWhiteSpace(text))
                return cpus;

            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var dash = part.IndexOf('-');
                if (dash < 0)
                {
                    if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var cpu))
                        cpus.Add(cpu);
                }
                else if (int.TryParse(part.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var first)
                      && int.TryParse(part.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var last))
                {
                    for (var cpu = first; cpu <= last; cpu++)
                        cpus.Add(cpu);
                }
            }
            return cpus;
        }
    }

    // "CPU implementer" of /proc/cpuinfo (MIDR_EL1.Implementer).
    internal static class ArmImplementers
    {
        public static string? Name(string? implementer)
        {
            if (implementer is null)
                return null;

            var hex = implementer.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? implementer[2..] : implementer;
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                return null;

            return code switch
            {
                0x41 => "ARM",
                0x42 => "Broadcom",
                0x43 => "Cavium",
                0x46 => "Fujitsu",
                0x48 => "HiSilicon",
                0x4E => "NVIDIA",
                0x50 => "Applied Micro",
                0x51 => "Qualcomm",
                0x53 => "Samsung",
                0x56 => "Marvell",
                0x61 => "Apple",
                0x69 => "Intel",
                0x6D => "Microsoft",
                0xC0 => "Ampere",
                _ => null
            };
        }
    }
}
