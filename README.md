# KubicEmu

KubicEmu is a Windows WPF controller for a genuine Android-x86 virtual machine. It runs QEMU as a separate desktop process, keeps the Android disk in a persistent qcow2 image, and uses Android Debug Bridge (ADB) for APK and device actions. It does not emulate Android in the desktop UI.

## Architecture

- `KubicEmu.App`: Windows-only WPF interface and user-profile settings.
- `KubicEmu.Core`: QEMU argument construction, process management, ADB commands, settings validation, SHA-256 verification, and resumable downloads.
- `KubicEmu.Tests`: hardware-independent tests for configuration, command construction, checksums, resume behavior, and preservation of existing disk files.
- QEMU opens the Android display in its own window. WHPX is selected by default; if it exits immediately, startup retries with QEMU's TCG software accelerator.
- A qcow2 disk is created only when the target does not already exist. KubicEmu does not delete or replace user disk images.

## Requirements

- Windows 10/11 x64. WHPX acceleration requires the Windows Hypervisor Platform feature and CPU virtualization enabled in firmware. TCG is a slower fallback.
- QEMU for Windows, including `qemu-system-x86_64.exe` and `qemu-img.exe`.
- Android SDK Platform Tools (`adb.exe`).
- A compatible Android-x86_64 installer ISO. KubicEmu does not bundle Android. Obtain the image and its SHA-256 checksum directly from its publisher; check the image's license and compatibility before use.
- At least 4 GiB assigned to the VM plus memory for Windows. The default VM disk is 32 GiB and grows dynamically.

Android-x86 is an independent community project, not Google's Android Emulator. Compatibility, security updates, and application support vary by image release. Play Store/Google Mobile Services are not included by KubicEmu.

## First Run

1. Install QEMU for Windows and Android SDK Platform Tools. Enable **Windows Hypervisor Platform** in Windows Features and reboot if WHPX will be used.
2. Launch KubicEmu and open **Settings**. Browse to the QEMU tools and `adb.exe` if they are not on `PATH`.
3. To download an image within the app, enter its publisher-provided HTTPS URL and exact SHA-256 hash, then choose **Download and verify ISO**. Interrupted downloads retain a `.partial` file and resume when the server supports HTTP range requests. Existing final files are never overwritten.
4. Alternatively browse to an ISO you already downloaded. Select the matching disk location and configure memory/CPU, then choose **Create virtual disk**. The disk is stored under `%LOCALAPPDATA%\KubicEmu` by default.
5. Choose **Boot installer**. In the QEMU window, install Android-x86 onto the provided virtual disk. Do not select or format any host drive. Shut the VM down when installation finishes.
6. Start Android without the installer ISO. Enable Android debugging and ensure `adbd` listens on TCP port 5555 inside the guest; the host connects through QEMU's localhost port forwarding. Android-x86 images differ in how this is enabled.
7. Once boot is complete, use **Install APK**, drag an APK onto the app, or enter an installed package name to launch/uninstall it. Display size/DPI, screenshot, rotation, and volume actions are also available.

The first boot and Android installation are manual. The application does not currently provision an Android image automatically, install the guest OS unattended, or embed the QEMU display in its WPF window.

## Build and Test

Requires the .NET 10 SDK. On Windows:

```powershell
dotnet restore KubicEmu.sln
dotnet test KubicEmu.sln --configuration Release
dotnet publish src/KubicEmu.App/KubicEmu.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/win-x64
```

On Linux, cross-compilation of the WPF project is enabled with `EnableWindowsTargeting`; publish may need NuGet access to download the Windows Desktop and runtime packs:

```bash
dotnet test tests/KubicEmu.Tests/KubicEmu.Tests.csproj --configuration Release
dotnet build src/KubicEmu.App/KubicEmu.App.csproj --configuration Release
dotnet publish src/KubicEmu.App/KubicEmu.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/win-x64
```

The release output is not a standalone Android emulator package: QEMU, ADB, and the Android image remain separate prerequisites. The current Linux container has no QEMU or ADB installation and does not grant the build user access to `/dev/kvm`, so an Android VM integration test could not be run here. A booted-VM integration test must be run on a Windows machine with those dependencies and a compatible Android image; no successful Android boot is claimed.