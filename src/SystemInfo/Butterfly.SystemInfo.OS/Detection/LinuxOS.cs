using Butterfly.SystemInfo.Native;

namespace Butterfly.SystemInfo.OS.Detection
{
    internal static class LinuxOS
    {
        private static readonly string[] ContainerCgroups = ["/docker", "/kubepods", "/containerd", "/libpod", "/podman", "/lxc", "/garden"];

        public static OSInfoSnapshot Detect()
        {
            var release = KeyValueText.Parse(SystemFiles.ReadText("/etc/os-release") ?? SystemFiles.ReadText("/usr/lib/os-release"), '=');
            var kernel = SystemFiles.ReadLine("/proc/sys/kernel/osrelease");

            return new OSInfoSnapshot
            {
                Family = OSFamily.Linux,
                KernelFamily = OSKernelFamily.Linux,
                Name = release.GetValueOrDefault("NAME") ?? "Linux",
                Version = OSDetector.ParseVersion(release.GetValueOrDefault("VERSION_ID")),
                DisplayVersion = release.GetValueOrDefault("VERSION") ?? release.GetValueOrDefault("VERSION_ID"),
                KernelVersion = kernel,
                DistributionId = release.GetValueOrDefault("ID"),
                Is64Bit = Environment.Is64BitOperatingSystem,
                IsContainer = File.Exists("/.dockerenv")
                           || File.Exists("/run/.containerenv")
                           || IsContainerCgroup(SystemFiles.ReadText("/proc/self/cgroup"))
                           || OSDetector.RunningInContainerVariable(),
                IsWsl = IsWslKernel(kernel) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"))
            };
        }

        // cgroup v1 paths name the runtime ("12:memory:/docker/3f2a..."). cgroup v2 hides them inside the container
        // ("0::/"), which is why the marker files are checked as well.
        internal static bool IsContainerCgroup(string? cgroup) =>
            cgroup is not null && ContainerCgroups.Any(marker => cgroup.Contains(marker, StringComparison.Ordinal));

        // WSL kernels: "5.15.153.1-microsoft-standard-WSL2", "4.4.0-19041-Microsoft".
        internal static bool IsWslKernel(string? kernel) => kernel?.Contains("microsoft", StringComparison.OrdinalIgnoreCase) == true;
    }
}
