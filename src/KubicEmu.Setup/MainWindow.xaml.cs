using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using KubicEmu.Core;
using Microsoft.Win32;

namespace KubicEmu.Setup;

public partial class MainWindow : Window
{
    private readonly InstallPackageService _installer = new();
    private CancellationTokenSource? _installCancellation;

    public MainWindow()
    {
        InitializeComponent();
        PackagePath.Text = Path.Combine(AppContext.BaseDirectory, "KubicEmu-win-x64.zip");
        InstallFolder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "KubicEmu");
        RefreshState();
    }

    private void BrowsePackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "KubicEmu package (*.zip)|*.zip", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) PackagePath.Text = dialog.FileName;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose KubicEmu install location", Multiselect = false };
        if (dialog.ShowDialog(this) == true) InstallFolder.Text = dialog.FolderName;
    }

    private void PackagePath_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshState();
    private void InstallFolder_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshState();

    private void RefreshState()
    {
        if (PackageSize is null || InstallButton is null) return;
        var packagePath = PackagePath.Text.Trim();
        if (File.Exists(packagePath))
        {
            var sizeMiB = InstallPackageService.GetPackageSizeMiB(packagePath);
            PackageSize.Text = $"Package download size: {sizeMiB.ToString("F1", CultureInfo.CurrentCulture)} MB";
        }
        else PackageSize.Text = "Package size: select KubicEmu-win-x64.zip";

        var target = InstallFolder.Text.Trim();
        var installed = target.Length > 0 && File.Exists(Path.Combine(target, "KubicEmu.exe"));
        InstallKind.Text = installed ? "Existing installation found" : "New installation";
        InstallButton.Content = installed ? "Update" : "Install";
        InstallButton.IsEnabled = File.Exists(packagePath) && target.Length > 0 && _installCancellation is null;
        LaunchButton.IsEnabled = installed && _installCancellation is null;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (Process.GetProcessesByName("KubicEmu").Any(process => process.Id != Environment.ProcessId))
        {
            MessageBox.Show(this, "Close KubicEmu before installing an update.", "KubicEmu Setup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _installCancellation = new CancellationTokenSource();
        RefreshState();
        CancelButton.IsEnabled = true;
        InstallProgress.Value = 0;
        try
        {
            InstallStatus.Text = "Preparing package...";
            var progress = new Progress<InstallProgress>(UpdateProgress);
            await _installer.InstallOrUpdateAsync(PackagePath.Text.Trim(), InstallFolder.Text.Trim(), progress, _installCancellation.Token);
            InstallStatus.Text = "KubicEmu is ready in the selected folder.";
            ProgressDetail.Text = "Installation complete";
            InstallProgress.Value = 100;
            MessageBox.Show(this, "KubicEmu was installed successfully.", "KubicEmu Setup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            InstallStatus.Text = "Installation canceled. Run setup again to complete or repair the installation.";
            ProgressDetail.Text = "Canceled";
        }
        catch (Exception ex)
        {
            InstallStatus.Text = "Installation failed.";
            ProgressDetail.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "KubicEmu Setup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _installCancellation.Dispose();
            _installCancellation = null;
            CancelButton.IsEnabled = false;
            RefreshState();
        }
    }

    private void UpdateProgress(InstallProgress progress)
    {
        var totalMiB = progress.TotalBytes / 1024d / 1024d;
        var completeMiB = progress.CompletedBytes / 1024d / 1024d;
        InstallProgress.Value = progress.TotalBytes == 0 ? 0 : progress.CompletedBytes * 100d / progress.TotalBytes;
        InstallStatus.Text = progress.Phase == "Complete" ? "Finalizing installation..." : $"{progress.Phase}: {progress.FileName}";
        ProgressDetail.Text = $"{completeMiB:F1} / {totalMiB:F1} MB";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _installCancellation?.Cancel();

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var executable = Path.Combine(InstallFolder.Text.Trim(), "KubicEmu.exe");
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! });
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "KubicEmu Setup", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}