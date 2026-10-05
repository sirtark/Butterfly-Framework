using System.Globalization;
using System.Xml.Linq;

namespace Butterfly.Virtualization.Libvirt
{
    // Builds the libvirt domain XML (https://libvirt.org/formatdomain.html) for a specification.
    internal static class LibvirtDomainXml
    {
        /// <param name="kvm">Hardware acceleration is available (/dev/kvm); otherwise QEMU emulates the processor (TCG).</param>
        /// <param name="x86">The host is x86; otherwise ARM64 ("virt" machine, UEFI only).</param>
        public static XDocument Build(VirtualMachineSpec spec, bool kvm, bool x86)
        {
            var uefi = spec.Firmware == VirtualMachineFirmware.Uefi;

            var type = new XElement("type", "hvm");
            if (!x86)
                type.SetAttributeValue("machine", "virt");
            else if (uefi)
                type.SetAttributeValue("machine", "q35");

            var os = new XElement("os", type);
            if (uefi)
            {
                os.SetAttributeValue("firmware", "efi");
                os.Add(new XElement("firmware",
                    Feature("secure-boot", spec.SecureBoot),
                    Feature("enrolled-keys", spec.SecureBoot)));
            }
            if (spec.InstallationMedia is not null)
                os.Add(new XElement("boot", new XAttribute("dev", "cdrom")));
            os.Add(new XElement("boot", new XAttribute("dev", "hd")));

            var features = new XElement("features", new XElement("acpi"));
            if (x86)
                features.Add(new XElement("apic"));
            // Secure boot on x86 needs System Management Mode to protect the UEFI variables.
            if (x86 && spec.SecureBoot)
                features.Add(new XElement("smm", new XAttribute("state", "on")));

            var devices = new XElement("devices");
            for (var i = 0; i < spec.Disks.Count; i++)
            {
                var disk = spec.Disks[i];
                devices.Add(new XElement("disk", new XAttribute("type", "file"), new XAttribute("device", "disk"),
                    new XElement("driver", new XAttribute("name", "qemu"), new XAttribute("type", DiskFormat(disk.Path))),
                    new XElement("source", new XAttribute("file", disk.Path)),
                    new XElement("target", new XAttribute("dev", "vd" + (char)('a' + i)), new XAttribute("bus", "virtio"))));
            }

            if (spec.InstallationMedia is not null)
            {
                // ARM's "virt" machine has no SATA controller.
                var bus = x86 ? "sata" : "scsi";
                if (!x86)
                    devices.Add(new XElement("controller", new XAttribute("type", "scsi"), new XAttribute("model", "virtio-scsi")));
                devices.Add(new XElement("disk", new XAttribute("type", "file"), new XAttribute("device", "cdrom"),
                    new XElement("driver", new XAttribute("name", "qemu"), new XAttribute("type", "raw")),
                    new XElement("source", new XAttribute("file", spec.InstallationMedia)),
                    new XElement("target", new XAttribute("dev", "sda"), new XAttribute("bus", bus)),
                    new XElement("readonly")));
            }

            if (spec.Network is not null)
            {
                devices.Add(new XElement("interface", new XAttribute("type", "network"),
                    new XElement("source", new XAttribute("network", spec.Network)),
                    new XElement("model", new XAttribute("type", "virtio"))));
            }

            devices.Add(new XElement("console", new XAttribute("type", "pty")));
            devices.Add(new XElement("graphics", new XAttribute("type", "vnc"), new XAttribute("autoport", "yes"), new XAttribute("listen", "127.0.0.1")));

            var domain = new XElement("domain", new XAttribute("type", kvm ? "kvm" : "qemu"),
                new XElement("name", spec.Name),
                new XElement("memory", new XAttribute("unit", "bytes"), spec.MemoryBytes.ToString(CultureInfo.InvariantCulture)),
                new XElement("vcpu", spec.ProcessorCount.ToString(CultureInfo.InvariantCulture)),
                os,
                features);
            // Passing the host processor through is only possible with hardware acceleration.
            if (kvm)
                domain.Add(new XElement("cpu", new XAttribute("mode", "host-passthrough")));
            domain.Add(devices);

            return new XDocument(domain);
        }

        // qemu-img and the disk driver need the image format; anything that is not qcow2 is treated as a raw image.
        public static string DiskFormat(string path) =>
            Path.GetExtension(path).Equals(".qcow2", StringComparison.OrdinalIgnoreCase) ? "qcow2" : "raw";

        private static XElement Feature(string name, bool enabled) =>
            new("feature", new XAttribute("enabled", enabled ? "yes" : "no"), new XAttribute("name", name));
    }
}
