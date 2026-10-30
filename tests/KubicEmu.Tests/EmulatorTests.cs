using KubicEmu.Core;
using System.Net;
using System.Net.Http.Headers;
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