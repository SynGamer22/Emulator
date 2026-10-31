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

1. Extract `KubicEmu-Setup-win-x64.zip` and run `KubicEmu.Setup.exe`. Keep `KubicEmu-win-x64.zip` beside it. Choose an install location; the setup screen reports package size and live expanded-byte progress. If `KubicEmu.exe` already exists there, setup offers an update. It updates files from the package and removes obsolete files only when tracked in its KubicEmu manifest; unrelated files and Android data remain untouched.
2. Install QEMU for Windows and Android SDK Platform Tools. Enable **Windows Hypervisor Platform** in Windows Features and reboot if WHPX will be used.
3. Launch KubicEmu and open **Settings**. Browse to the QEMU tools and `adb.exe` if they are not on `PATH`.
4. To download an image within the app, enter its publisher-provided HTTPS URL and exact SHA-256 hash, then choose **Download and verify ISO**. Interrupted downloads retain a `.partial` file and resume when the server supports HTTP range requests. Existing final files are never overwritten.
5. Alternatively browse to an ISO you already downloaded. Configure memory/CPU. On first start, KubicEmu creates its persistent disk under `%LOCALAPPDATA%\KubicEmu` by default and boots the installer automatically. If disk creation or QEMU startup fails, the next start continues to select installer mode while the disk is still absent.
6. In the QEMU window, install Android-x86 onto the virtual disk. Do not select or format any host drive. Shut the VM down when installation finishes, then choose **Start Android** again.
7. Enable Android debugging and ensure `adbd` listens on TCP port 5555 inside the guest; the host connects through QEMU's localhost port forwarding. Android-x86 images differ in how this is enabled.
8. Once boot is complete, use **Install APK**, drag an APK onto the app, or enter an installed package name to launch/uninstall it. Display size/DPI, screenshot, rotation, and volume actions are also available.

The first boot and Android installation are manual. The application does not currently provision an Android image automatically, install the guest OS unattended, or embed the QEMU display in its WPF window.

## Build and Test

Requires the .NET 10 SDK. On Windows:

```powershell
dotnet restore KubicEmu.sln
dotnet test KubicEmu.sln --configuration Release
dotnet publish src/KubicEmu.App/KubicEmu.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/win-x64
dotnet publish src/KubicEmu.Setup/KubicEmu.Setup.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/setup
```

On Linux, cross-compilation of the WPF project is enabled with `EnableWindowsTargeting`; publish may need NuGet access to download the Windows Desktop and runtime packs:

```bash
dotnet test tests/KubicEmu.Tests/KubicEmu.Tests.csproj --configuration Release
dotnet build src/KubicEmu.App/KubicEmu.App.csproj --configuration Release
dotnet publish src/KubicEmu.App/KubicEmu.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/win-x64
```

To produce the app ZIP and setup/update bundle from the repository root, run `bash scripts/build-release.sh`. It creates `artifacts/KubicEmu-win-x64.zip` and `artifacts/KubicEmu-Setup-win-x64.zip`. Extract the latter as a pair; the setup executable requires the app ZIP alongside it.

The release output is not a standalone Android emulator package: QEMU, ADB, and the Android image remain separate prerequisites. The current Linux container has no QEMU or ADB installation and does not grant the build user access to `/dev/kvm`, so an Android VM integration test could not be run here. A booted-VM integration test must be run on a Windows machine with those dependencies and a compatible Android image; no successful Android boot is claimed.