namespace KubicEmu.Core;

public static class QemuCommandBuilder
{
    public static IReadOnlyList<string> Build(EmulatorSettings settings, bool bootFromInstaller = false)
    {
        var error = settings.Validate();
        if (error.Length > 0) throw new ArgumentException(error, nameof(settings));
        if (!File.Exists(settings.DiskPath)) throw new FileNotFoundException("Virtual disk does not exist. Create it before starting Android.", settings.DiskPath);
        if (bootFromInstaller && !File.Exists(settings.AndroidIsoPath)) throw new FileNotFoundException("Select a valid Android-x86 installer ISO.", settings.AndroidIsoPath);

        var args = new List<string>
        {
            "-machine", "q35",
            "-accel", settings.Acceleration,
            "-cpu", "max",
            "-smp", settings.CpuCores.ToString(),
            "-m", settings.MemoryMiB.ToString(),
            "-device", "virtio-vga",
            "-device", "qemu-xhci,id=usb",
            "-device", "usb-tablet,bus=usb.0",
            "-device", "ich9-ahci,id=sata"
        };

        if (bootFromInstaller)
        {
            args.AddRange(["-boot", "order=d", "-drive", $"file={Path.GetFullPath(settings.AndroidIsoPath)},media=cdrom,if=none,id=cdrom", "-device", "ide-cd,drive=cdrom,bus=sata.0"]);
        }
        else args.AddRange(["-boot", "order=c"]);

        args.AddRange([
            "-drive", $"file={Path.GetFullPath(settings.DiskPath)},if=none,id=androiddisk,format=qcow2",
            "-device", "virtio-blk-pci,drive=androiddisk",
            "-netdev", $"user,id=net0,hostfwd=tcp::{settings.AdbPort}-:5555",
            "-device", "virtio-net-pci,netdev=net0",
            "-rtc", "base=localtime",
            "-name", "KubicEmu Android"
        ]);
        return args;
    }
}