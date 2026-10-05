namespace Butterfly.SystemInfo.OS
{
    public enum OSFamily : byte
    {
        Unknown,
        Windows,
        Linux,
        MacOS,
        FreeBSD
    }
    public enum OSKernelFamily : byte
    {
        Unknown,
        WindowsNT,
        Linux,
        XNU,
        FreeBSD
    }
    public sealed record OSInfoSnapshot
    {
        internal OSInfoSnapshot()
        { }

        public OSFamily Family { get; internal init; } = OSFamily.Unknown;
        public OSKernelFamily KernelFamily { get; internal init; } = OSKernelFamily.Unknown;

        /// <summary>Product or distribution name: "Windows 11 Pro", "Ubuntu", "macOS", "FreeBSD".</summary>
        public string? Name { get; internal init; }
        /// <summary>Product version: 10.0.26100.4061 (Windows, with the update revision), 24.04 (Ubuntu), 15.1 (macOS).</summary>
        public Version? Version { get; internal init; }
        /// <summary>Marketing version: "24H2" (Windows), "24.04.1 LTS (Noble Numbat)" (Linux), "14.1-RELEASE" (FreeBSD).</summary>
        public string? DisplayVersion { get; internal init; }
        /// <summary>Kernel release: "10.0.26100.4061", "6.8.0-45-generic", "24.1.0".</summary>
        public string? KernelVersion { get; internal init; }
        /// <summary>Linux distribution identifier from os-release ("ubuntu", "debian", "alpine").</summary>
        public string? DistributionId { get; internal init; }

        public bool Is64Bit { get; internal init; }
        /// <summary>The process runs inside a container (Docker, Podman, Kubernetes, LXC, Windows containers, FreeBSD jails).</summary>
        public bool IsContainer { get; internal init; }
        /// <summary>The process runs on Linux inside the Windows Subsystem for Linux.</summary>
        public bool IsWsl { get; internal init; }

        public static OSInfoSnapshot Unknown => new();
    }
}
