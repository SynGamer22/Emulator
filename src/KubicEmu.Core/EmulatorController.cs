using System.Diagnostics;
using System.Security.Cryptography;

namespace KubicEmu.Core;

public sealed class EmulatorController(IProcessRunner runner) : IAsyncDisposable
{
    private Process? _qemu;
    public bool IsRunning => _qemu is { HasExited: false };
    public event Action<string>? Log;
    public event Action<bool>? RunningChanged;

    public async Task CreateDiskAsync(EmulatorSettings settings, CancellationToken cancellationToken = default)
    {
        var error = settings.Validate();
        if (error.Length > 0) throw new ArgumentException(error, nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.DiskPath)) throw new ArgumentException("Choose a virtual disk path first.", nameof(settings));
        var diskPath = Path.GetFullPath(settings.DiskPath);
        if (File.Exists(diskPath)) throw new IOException("The virtual disk already exists; it was not overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
        var result = await runner.RunAsync(settings.QemuImgPath,
            ["create", "-f", "qcow2", diskPath, $"{settings.DiskSizeGiB}G"], cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"qemu-img failed: {result.StandardError}");
        Log?.Invoke($"Created persistent virtual disk: {diskPath}");
    }

    public async Task StartAsync(EmulatorSettings settings, bool bootFromInstaller, CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        var error = settings.Validate();
        if (error.Length > 0) throw new ArgumentException(error);
        if (!File.Exists(settings.DiskPath))
        {
            if (!File.Exists(settings.AndroidIsoPath))
                throw new InvalidOperationException("No Android virtual disk exists yet. Select a valid Android-x86 installer ISO in Settings; KubicEmu will create a persistent disk and boot its installer.");
            bootFromInstaller = true;
            await CreateDiskAsync(settings, cancellationToken);
        }
        var args = QemuCommandBuilder.Build(settings, bootFromInstaller);
        var startInfo = new ProcessStartInfo(settings.QemuPath) { UseShellExecute = false };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        startInfo.RedirectStandardError = true;
        _qemu = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _qemu.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log?.Invoke(e.Data); };
        _qemu.Exited += (_, _) => { RunningChanged?.Invoke(false); Log?.Invoke($"QEMU exited with code {_qemu?.ExitCode}."); };
        if (!_qemu.Start()) throw new InvalidOperationException("QEMU did not start.");
        _qemu.BeginErrorReadLine();
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        if (_qemu.HasExited && settings.Acceleration == "whpx")
        {
            Log?.Invoke("WHPX did not start the VM; retrying with software emulation (TCG).");
            _qemu.Dispose();
            _qemu = null;
            await StartAsync(settings with { Acceleration = "tcg" }, bootFromInstaller, cancellationToken);
            return;
        }
        if (_qemu.HasExited) throw new InvalidOperationException($"QEMU exited during startup with code {_qemu.ExitCode}. Check the activity log and QEMU installation.");
        RunningChanged?.Invoke(true);
        Log?.Invoke("QEMU started. Its window contains the Android display.");
        await Task.CompletedTask;
    }

    public async Task StopAsync(EmulatorSettings? settings = null, CancellationToken cancellationToken = default)
    {
        if (!IsRunning || _qemu is null) return;
        if (settings is not null)
        {
            try
            {
                await AdbAsync(settings, "shell", "reboot", "-p");
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
                Log?.Invoke("Requested Android guest power-off.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log?.Invoke($"Guest power-off was unavailable; closing QEMU: {ex.Message}");
            }
        }
        _qemu.CloseMainWindow();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try { await _qemu.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _qemu.Kill(entireProcessTree: true);
            await _qemu.WaitForExitAsync(cancellationToken);
        }
        RunningChanged?.Invoke(false);
    }

    public async Task<ProcessResult> AdbAsync(EmulatorSettings settings, params string[] arguments)
    {
        var endpoint = $"127.0.0.1:{settings.AdbPort}";
        await EnsureAdbConnectedAsync(settings);
        var result = await runner.RunAsync(settings.AdbPath,
            ["-s", endpoint, .. arguments]);
        if (result.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
        return result;
    }

    public async Task InstallApkAsync(EmulatorSettings settings, string apkPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(apkPath) || !string.Equals(Path.GetExtension(apkPath), ".apk", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Select an existing APK file.", apkPath);
        await EnsureAdbConnectedAsync(settings);
        var result = await runner.RunAsync(settings.AdbPath,
            ["-s", $"127.0.0.1:{settings.AdbPort}", "install", "-r", apkPath], cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"APK installation failed: {result.StandardError}{result.StandardOutput}");
        Log?.Invoke(result.StandardOutput.Trim());
    }

    private async Task EnsureAdbConnectedAsync(EmulatorSettings settings)
    {
        var endpoint = $"127.0.0.1:{settings.AdbPort}";
        var result = await runner.RunAsync(settings.AdbPath, ["connect", endpoint]);
        if (result.ExitCode != 0) throw new InvalidOperationException($"ADB could not connect to {endpoint}: {result.StandardError}");
    }

    public async Task<bool> WaitForBootAsync(EmulatorSettings settings, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunning) return false;
            try
            {
                var result = await AdbAsync(settings, "shell", "getprop", "sys.boot_completed");
                if (result.StandardOutput.Trim() == "1") return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        return false;
    }

    public static async Task<bool> VerifySha256Async(string path, string expectedHash, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHash));
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally { _qemu?.Dispose(); }
    }
}