namespace Butterfly.SystemInfo.CPU.Detection
{
    // Identifies the hypervisor from what each source reports.
    internal static class Hypervisors
    {
        // CPUID leaf 0x40000000 (EBX, ECX, EDX), already trimmed.
        public static HypervisorVendor FromCpuidSignature(string? signature) => signature switch
        {
            "Microsoft Hv" => HypervisorVendor.HyperV,
            "KVMKVMKVM" or "Linux KVM Hv" => HypervisorVendor.KVM,
            "VMwareVMware" => HypervisorVendor.VMware,
            "VBoxVBoxVBox" => HypervisorVendor.VirtualBox,
            "XenVMMXenVMM" => HypervisorVendor.Xen,
            "TCGTCGTCGTCG" => HypervisorVendor.QEMU,
            "prl hyperv" or "lrpepyh  vr" => HypervisorVendor.Parallels,
            "bhyve bhyve" => HypervisorVendor.Bhyve,
            _ => HypervisorVendor.Unknown
        };

        // SMBIOS system manufacturer and product name. Unknown also means "probably physical hardware".
        public static HypervisorVendor FromSystemVendor(string? manufacturer, string? product)
        {
            manufacturer ??= string.Empty;
            product ??= string.Empty;

            if (Has(product, "VirtualBox") || Has(manufacturer, "innotek"))
                return HypervisorVendor.VirtualBox;
            if (Has(manufacturer, "VMware"))
                return HypervisorVendor.VMware;
            if (Has(product, "KVM"))
                return HypervisorVendor.KVM;
            if (Has(manufacturer, "QEMU"))
                return HypervisorVendor.QEMU;
            if (Has(manufacturer, "Microsoft Corporation") && Has(product, "Virtual Machine"))
                return HypervisorVendor.HyperV;
            if (Has(manufacturer, "Xen") || Has(product, "HVM domU"))
                return HypervisorVendor.Xen;
            if (Has(manufacturer, "Parallels"))
                return HypervisorVendor.Parallels;
            if (Has(product, "BHYVE"))
                return HypervisorVendor.Bhyve;
            if (Has(manufacturer, "Apple") && Has(product, "Virtual"))
                return HypervisorVendor.AppleHypervisor;
            // Cloud VMs built on KVM. EC2 bare-metal instances ("m5.metal") share the manufacturer.
            if ((Has(manufacturer, "Amazon EC2") && !product.EndsWith(".metal", StringComparison.OrdinalIgnoreCase)) || Has(product, "Google Compute Engine"))
                return HypervisorVendor.KVM;

            return HypervisorVendor.Unknown;
        }

        // FreeBSD's kern.vm_guest.
        public static HypervisorVendor FromFreeBsdVmGuest(string? guest) => guest switch
        {
            "none" => HypervisorVendor.None,
            "hv" => HypervisorVendor.HyperV,
            "kvm" => HypervisorVendor.KVM,
            "vmware" => HypervisorVendor.VMware,
            "vbox" => HypervisorVendor.VirtualBox,
            "xen" => HypervisorVendor.Xen,
            "bhyve" => HypervisorVendor.Bhyve,
            "parallels" => HypervisorVendor.Parallels,
            _ => HypervisorVendor.Unknown
        };

        private static bool Has(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
    }
}
