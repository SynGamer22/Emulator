using System.IO;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using KubicEmu.Core;

namespace KubicEmu.App;

public partial class MainWindow : Window
{
    private readonly EmulatorController _controller = new(new ProcessRunner());
    private EmulatorSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadSettingsAsync();
        _controller.Log += message => Dispatcher.Invoke(() => AddLog(message));
        _controller.RunningChanged += running => Dispatcher.Invoke(() => SetRunning(running));
        Closing += async (_, _) =>
        {
            if (_controller.IsRunning) await _controller.StopAsync(_settings);
            await _controller.DisposeAsync();
        };
    }

    private async Task LoadSettingsAsync()
    {
        try { _settings = await SettingsStore.LoadAsync(SettingsStore.DefaultPath); }
        catch (Exception ex) { AddLog($"Could not load settings: {ex.Message}"); }
        PopulateFields();
        RefreshDeviceStatus();
    }

    private void PopulateFields()
    {
        IsoPath.Text = _settings.AndroidIsoPath;
        DiskPath.Text = _settings.DiskPath;
        ImageDownloadUrl.Text = _settings.ImageDownloadUrl;
        ImageSha256.Text = _settings.ImageSha256;
        QemuPath.Text = _settings.QemuPath;
        QemuImgPath.Text = _settings.QemuImgPath;
        AdbPath.Text = _settings.AdbPath;
        Memory.Text = _settings.MemoryMiB.ToString();
        CpuCores.Text = _settings.CpuCores.ToString();
        DiskSize.Text = _settings.DiskSizeGiB.ToString();
        ScreenWidth.Text = _settings.ScreenWidth.ToString();
        ScreenHeight.Text = _settings.ScreenHeight.ToString();
        Dpi.Text = _settings.Dpi.ToString();
        Acceleration.SelectedIndex = _settings.Acceleration == "tcg" ? 1 : 0;
    }

    private EmulatorSettings ReadFields() => _settings with
    {
        AndroidIsoPath = IsoPath.Text.Trim(), DiskPath = DiskPath.Text.Trim(),
        ImageDownloadUrl = ImageDownloadUrl.Text.Trim(), ImageSha256 = ImageSha256.Text.Trim(),
        QemuPath = QemuPath.Text.Trim(), QemuImgPath = QemuImgPath.Text.Trim(), AdbPath = AdbPath.Text.Trim(),
        MemoryMiB = int.Parse(Memory.Text), CpuCores = int.Parse(CpuCores.Text), DiskSizeGiB = int.Parse(DiskSize.Text),
        ScreenWidth = int.Parse(ScreenWidth.Text), ScreenHeight = int.Parse(ScreenHeight.Text), Dpi = int.Parse(Dpi.Text),
        Acceleration = Acceleration.SelectedIndex == 1 ? "tcg" : "whpx"
    };

    private async Task SaveSettingsAsync()
    {
        _settings = ReadFields();
        var error = _settings.Validate();
        if (error.Length > 0) throw new ArgumentException(error);
        await SettingsStore.SaveAsync(SettingsStore.DefaultPath, _settings);
        AddLog("Settings saved.");
        RefreshDeviceStatus();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try { await SaveSettingsAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
            var firstLaunch = !File.Exists(_settings.DiskPath);
            if (firstLaunch && !File.Exists(_settings.AndroidIsoPath))
                throw new InvalidOperationException("No Android virtual disk exists yet. Select an Android-x86 ISO in Settings, then start Android to create a disk and launch its installer.");

            await _controller.StartAsync(_settings, bootFromInstaller: firstLaunch);
            if (firstLaunch)
                BootStatus.Text = "A new persistent disk was created. Install Android-x86 from the QEMU window, then restart without the ISO.";
            else
            {
                BootStatus.Text = "Android is starting. The QEMU window shows the virtual device.";
                _ = MonitorBootAsync();
            }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Installer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
            await _controller.StartAsync(_settings, bootFromInstaller: true);
            BootStatus.Text = "Install Android-x86 onto the virtual disk in the QEMU window. Reboot without the ISO when installation finishes.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task MonitorBootAsync()
    {
        var booted = await _controller.WaitForBootAsync(_settings, TimeSpan.FromMinutes(5));
        if (booted)
        {
            try
            {
                await _controller.AdbAsync(_settings, "shell", "wm", "size", $"{_settings.ScreenWidth}x{_settings.ScreenHeight}");
                await _controller.AdbAsync(_settings, "shell", "wm", "density", _settings.Dpi.ToString());
            }
            catch (Exception ex) { Dispatcher.Invoke(() => AddLog($"Could not apply display settings: {ex.Message}")); }
        }
        Dispatcher.Invoke(() => BootStatus.Text = booted ? "Android boot completed; ADB is connected." : "Android did not report boot completion. Check the VM and ADB settings.");
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try { await _controller.StopAsync(_settings); BootStatus.Text = "Android stopped."; }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _controller.StopAsync(_settings);
            await _controller.StartAsync(_settings, bootFromInstaller: false);
            BootStatus.Text = "Android restarting...";
            _ = MonitorBootAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Android package (*.apk)|*.apk", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { await _controller.InstallApkAsync(_settings, dialog.FileName); BootStatus.Text = "APK installed."; }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = "KubicEmu-screenshot.png" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            const string remotePath = "/sdcard/kubicemu-screenshot.png";
            await _controller.AdbAsync(_settings, "shell", "screencap", "-p", remotePath);
            await _controller.AdbAsync(_settings, "pull", remotePath, dialog.FileName);
            await _controller.AdbAsync(_settings, "shell", "rm", remotePath);
            BootStatus.Text = "Screenshot saved.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = RequirePackageName();
            await _controller.AdbAsync(_settings, "shell", "monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1");
            BootStatus.Text = $"Launched {package}.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = RequirePackageName();
            await _controller.AdbAsync(_settings, "uninstall", package);
            BootStatus.Text = $"Uninstalled {package}.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Rotate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var current = await _controller.AdbAsync(_settings, "shell", "settings", "get", "system", "user_rotation");
            var rotation = current.StandardOutput.Trim() == "1" ? "0" : "1";
            await _controller.AdbAsync(_settings, "shell", "settings", "put", "system", "accelerometer_rotation", "0");
            await _controller.AdbAsync(_settings, "shell", "settings", "put", "system", "user_rotation", rotation);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void VolumeUp_Click(object sender, RoutedEventArgs e) => await SendVolumeKeyAsync("24");
    private async void VolumeDown_Click(object sender, RoutedEventArgs e) => await SendVolumeKeyAsync("25");

    private async Task SendVolumeKeyAsync(string keyCode)
    {
        try { await _controller.AdbAsync(_settings, "shell", "input", "keyevent", keyCode); }
        catch (Exception ex) { ShowError(ex); }
    }

    private string RequirePackageName()
    {
        var package = PackageName.Text.Trim();
        if (package.Length == 0) throw new ArgumentException("Enter an Android package name.");
        return package;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var apk = files.FirstOrDefault(file => string.Equals(Path.GetExtension(file), ".apk", StringComparison.OrdinalIgnoreCase));
        if (apk is null) { BootStatus.Text = "Drop an APK file to install it."; return; }
        try { await _controller.InstallApkAsync(_settings, apk); BootStatus.Text = $"Installed {Path.GetFileName(apk)}."; }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void CreateDisk_Click(object sender, RoutedEventArgs e)
    {
        try { await SaveSettingsAsync(); await _controller.CreateDiskAsync(_settings); RefreshDeviceStatus(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void DownloadImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
            if (!Uri.TryCreate(_settings.ImageDownloadUrl, UriKind.Absolute, out var source)) throw new ArgumentException("Enter a valid HTTPS Android image URL.");
            var dialog = new SaveFileDialog { Filter = "Android installer (*.iso)|*.iso", FileName = "android-x86.iso", CheckPathExists = true };
            if (dialog.ShowDialog(this) != true) return;
            BootStatus.Text = "Downloading Android image...";
            var progress = new Progress<long>(bytes => Dispatcher.Invoke(() => BootStatus.Text = $"Downloaded {bytes / (1024 * 1024)} MiB..."));
            await new AndroidImageDownloader().DownloadVerifiedAsync(source, dialog.FileName, _settings.ImageSha256, progress);
            IsoPath.Text = dialog.FileName;
            await SaveSettingsAsync();
            RefreshDeviceStatus();
            BootStatus.Text = "Android image downloaded and verified.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void BrowseIso_Click(object sender, RoutedEventArgs e) => BrowseFile(IsoPath, "Android-x86 ISO (*.iso)|*.iso");
    private void BrowseDisk_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "QEMU disk image (*.qcow2)|*.qcow2", FileName = "android-data.qcow2", CheckPathExists = true };
        if (dialog.ShowDialog(this) == true) DiskPath.Text = dialog.FileName;
    }
    private void BrowseQemu_Click(object sender, RoutedEventArgs e) => BrowseFile(QemuPath, "QEMU executable (qemu-system-x86_64.exe)|qemu-system-x86_64.exe|Executable (*.exe)|*.exe");
    private void BrowseQemuImg_Click(object sender, RoutedEventArgs e) => BrowseFile(QemuImgPath, "QEMU image tool (qemu-img.exe)|qemu-img.exe|Executable (*.exe)|*.exe");
    private void BrowseAdb_Click(object sender, RoutedEventArgs e) => BrowseFile(AdbPath, "ADB executable (adb.exe)|adb.exe|Executable (*.exe)|*.exe");

    private void BrowseFile(System.Windows.Controls.TextBox target, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FileName;
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        StatusText.Text = running ? "QEMU running" : "Not running";
    }

    private void RefreshDeviceStatus()
    {
        ImageStatus.Text = File.Exists(_settings.AndroidIsoPath) ? Path.GetFileName(_settings.AndroidIsoPath) : "Not selected";
        DiskStatus.Text = File.Exists(_settings.DiskPath) ? "Persistent disk ready" : "Not created";
        AccelerationStatus.Text = _settings.Acceleration.ToUpperInvariant();
    }

    private void AddLog(string message) => LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    private void ShowError(Exception ex) { AddLog($"ERROR: {ex.Message}"); MessageBox.Show(this, ex.Message, "KubicEmu", MessageBoxButton.OK, MessageBoxImage.Error); }
}