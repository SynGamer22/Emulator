using KubicEmu.Core;
using System.IO.Compression;
using System.Net;
using Xunit;

namespace KubicEmu.Tests;

public sealed class EmulatorTests
{
    [Fact]
    public void Settings_RejectUnsupportedAcceleration()
    {
        Assert.Contains("Acceleration", (new EmulatorSettings { Acceleration = "kvm" }).Validate());
    }

    [Fact]
    public void Settings_RejectLowMemory()
    {
        Assert.Contains("Memory", (new EmulatorSettings { MemoryMiB = 512 }).Validate());
    }

    [Fact]
    public void QemuArguments_IncludePersistentDiskAndAdbForwarding()
    {
        var disk = Path.GetTempFileName();
        try
        {
            var settings = new EmulatorSettings { DiskPath = disk, Acceleration = "tcg" };
            var args = QemuCommandBuilder.Build(settings);
            Assert.Contains("format=qcow2", string.Join(' ', args));
            Assert.Contains("hostfwd=tcp::5555-:5555", string.Join(' ', args));
            Assert.Contains("tcg", args);
        }
        finally { File.Delete(disk); }
    }

    [Fact]
    public void QemuArguments_RequireInstallerImageForInstallerBoot()
    {
        var disk = Path.GetTempFileName();
        try
        {
            Assert.Throws<FileNotFoundException>(() => QemuCommandBuilder.Build(new EmulatorSettings { DiskPath = disk }, true));
        }
        finally { File.Delete(disk); }
    }

    [Fact]
    public void InstallerBoot_PrefersCdromAndIncludesUsbInput()
    {
        var disk = Path.GetTempFileName();
        var iso = Path.GetTempFileName();
        try
        {
            var args = QemuCommandBuilder.Build(new EmulatorSettings { DiskPath = disk, AndroidIsoPath = iso }, true);
            Assert.Contains("order=d", args);
            Assert.Contains("qemu-xhci,id=usb", args);
            Assert.Contains("usb-tablet,bus=usb.0", args);
        }
        finally { File.Delete(disk); File.Delete(iso); }
    }

    [Fact]
    public async Task ImageVerification_UsesSha256()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "verified fixture");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(file)));
            Assert.True(await EmulatorController.VerifySha256Async(file, hash));
            Assert.False(await EmulatorController.VerifySha256Async(file, new string('0', 64)));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task SettingsStore_RoundTripsConfiguration()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "settings.json");
        try
        {
            var settings = new EmulatorSettings { MemoryMiB = 6144, Acceleration = "tcg" };
            await SettingsStore.SaveAsync(path, settings);
            var loaded = await SettingsStore.LoadAsync(path);
            Assert.Equal(settings, loaded);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ImageDownloader_ResumesPartialDownloadAndVerifiesHash()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var target = Path.Combine(folder, "android.iso");
        var body = System.Text.Encoding.UTF8.GetBytes("verified android image");
        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(target + ".partial", body[..8]);
            using var client = new HttpClient(new StubHttpHandler(request =>
            {
                Assert.Equal(8, request.Headers.Range?.Ranges.Single().From);
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body[8..]) };
            }));
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body));

            await new AndroidImageDownloader(client).DownloadVerifiedAsync(new Uri("https://images.example.test/android.iso"), target, hash);

            Assert.Equal(body, await File.ReadAllBytesAsync(target));
            Assert.False(File.Exists(target + ".partial"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ImageDownloader_DeletesImageWithIncorrectChecksum()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var target = Path.Combine(folder, "android.iso");
        try
        {
            using var client = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("unexpected content") }));
            await Assert.ThrowsAsync<InvalidDataException>(() => new AndroidImageDownloader(client).DownloadVerifiedAsync(
                new Uri("https://images.example.test/android.iso"), target, new string('0', 64)));
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".partial"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task CreateDisk_DoesNotOverwriteExistingData()
    {
        var disk = Path.GetTempFileName();
        var runner = new StubProcessRunner();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new EmulatorController(runner).CreateDiskAsync(new EmulatorSettings { DiskPath = disk }));
            Assert.Empty(runner.Calls);
        }
        finally { File.Delete(disk); }
    }

    [Fact]
    public async Task StartWithoutDiskOrInstaller_ExplainsFirstRunSetup()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var settings = new EmulatorSettings { DiskPath = Path.Combine(folder, "android.qcow2") };
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new EmulatorController(new StubProcessRunner()).StartAsync(settings, false));
            Assert.Contains("Select a valid Android-x86 installer ISO", error.Message);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task Installer_InstallsAndUpdatesOnlyManifestOwnedFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var installDirectory = Path.Combine(folder, "KubicEmu");
        var firstPackage = Path.Combine(folder, "first.zip");
        var secondPackage = Path.Combine(folder, "second.zip");
        try
        {
            Directory.CreateDirectory(folder);
            CreatePackage(firstPackage, ("KubicEmu.exe", "v1"), ("old-runtime.dll", "remove me"));
            var service = new InstallPackageService();
            await service.InstallOrUpdateAsync(firstPackage, installDirectory);
            Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(installDirectory, "KubicEmu.exe")));
            Assert.True(InstallPackageService.GetPackageSizeMiB(firstPackage) > 0);

            await File.WriteAllTextAsync(Path.Combine(installDirectory, "user-data.txt"), "preserve me");
            CreatePackage(secondPackage, ("KubicEmu.exe", "v2"), ("new-runtime.dll", "new file"));
            await service.InstallOrUpdateAsync(secondPackage, installDirectory);

            Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(installDirectory, "KubicEmu.exe")));
            Assert.True(File.Exists(Path.Combine(installDirectory, "new-runtime.dll")));
            Assert.False(File.Exists(Path.Combine(installDirectory, "old-runtime.dll")));
            Assert.Equal("preserve me", await File.ReadAllTextAsync(Path.Combine(installDirectory, "user-data.txt")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task Installer_RejectsZipPathTraversal()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var package = Path.Combine(folder, "unsafe.zip");
        var installDirectory = Path.Combine(folder, "KubicEmu");
        try
        {
            Directory.CreateDirectory(folder);
            CreatePackage(package, ("KubicEmu.exe", "app"), ("../escaped.txt", "outside"));
            await Assert.ThrowsAsync<InvalidDataException>(() => new InstallPackageService().InstallOrUpdateAsync(package, installDirectory));
            Assert.False(File.Exists(Path.Combine(folder, "escaped.txt")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    private static void CreatePackage(string path, params (string Name, string Content)[] files)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        public List<(string Executable, string[] Arguments)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add((executable, arguments.ToArray()));
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
}