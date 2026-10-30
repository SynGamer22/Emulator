using System.Text.Json;

namespace KubicEmu.Core;

public sealed record EmulatorSettings
{
    public string QemuPath { get; init; } = "qemu-system-x86_64.exe";
    public string QemuImgPath { get; init; } = "qemu-img.exe";
    public string AdbPath { get; init; } = "adb.exe";
    public string AndroidIsoPath { get; init; } = "";
    public string DiskPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KubicEmu", "android-data.qcow2");
    public string ImageDownloadUrl { get; init; } = "";
    public string ImageSha256 { get; init; } = "";
    public string Acceleration { get; init; } = "whpx";
    public int MemoryMiB { get; init; } = 4096;
    public int CpuCores { get; init; } = 4;
    public int DiskSizeGiB { get; init; } = 32;
    public int ScreenWidth { get; init; } = 1080;
    public int ScreenHeight { get; init; } = 1920;
    public int Dpi { get; init; } = 320;
    public int AdbPort { get; init; } = 5555;

    public string Validate()
    {
        if (MemoryMiB is < 1024 or > 32768) return "Memory must be between 1024 and 32768 MiB.";
        if (CpuCores is < 1 or > 64) return "CPU cores must be between 1 and 64.";
        if (DiskSizeGiB is < 8 or > 2048) return "Disk size must be between 8 and 2048 GiB.";
        if (ScreenWidth is < 320 or > 7680 || ScreenHeight is < 320 or > 7680) return "Screen dimensions must be between 320 and 7680 pixels.";
        if (Dpi is < 72 or > 640) return "DPI must be between 72 and 640.";
        if (AdbPort is < 1024 or > 65535) return "ADB port must be between 1024 and 65535.";
        if (Acceleration is not ("whpx" or "tcg")) return "Acceleration must be whpx or tcg.";
        return "";
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KubicEmu", "settings.json");

    public static async Task<EmulatorSettings> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new EmulatorSettings();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<EmulatorSettings>(stream, JsonOptions, cancellationToken)
            ?? new EmulatorSettings();
    }

    public static async Task SaveAsync(string path, EmulatorSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }
}