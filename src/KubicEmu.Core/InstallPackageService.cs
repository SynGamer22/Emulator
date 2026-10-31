using System.IO.Compression;
using System.Text.Json;

namespace KubicEmu.Core;

public sealed record InstallProgress(string Phase, string FileName, long CompletedBytes, long TotalBytes);

public sealed class InstallPackageService
{
    private const string ManifestName = ".kubicemu-install.json";
    private const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;

    public static double GetPackageSizeMiB(string packagePath) => new FileInfo(packagePath).Length / 1024d / 1024d;

    public async Task InstallOrUpdateAsync(
        string packagePath,
        string installDirectory,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("KubicEmu installation package was not found.", packagePath);
        if (string.IsNullOrWhiteSpace(installDirectory)) throw new ArgumentException("Choose an installation folder.", nameof(installDirectory));

        var root = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 0) throw new ArgumentException("The installation folder cannot be a drive root.", nameof(installDirectory));
        var stage = Path.Combine(Path.GetTempPath(), "KubicEmuSetup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var entries = ReadAndValidateEntries(archive, stage);
            if (!entries.Any(entry => string.Equals(entry.RelativePath, "KubicEmu.exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The package does not contain KubicEmu.exe.");
            var totalBytes = entries.Sum(entry => entry.Entry.Length);
            if (totalBytes <= 0 || totalBytes > MaximumExpandedBytes)
                throw new InvalidDataException("The package has an invalid or excessive expanded size.");

            long completed = 0;
            foreach (var item in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedFile = ResolveContainedPath(stage, item.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedFile)!);
                await using var source = item.Entry.Open();
                await using var destination = new FileStream(stagedFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
                var buffer = new byte[128 * 1024];
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    completed += count;
                    progress?.Report(new InstallProgress("Preparing", item.RelativePath, completed, totalBytes));
                }
            }

            Directory.CreateDirectory(root);
            var previousFiles = await ReadManifestAsync(root, cancellationToken);
            var nextFiles = new List<string>(entries.Count);
            completed = 0;
            foreach (var item in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = ResolveContainedPath(stage, item.RelativePath);
                var destinationPath = ResolveContainedPath(root, item.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
                var buffer = new byte[128 * 1024];
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    completed += count;
                    progress?.Report(new InstallProgress("Installing", item.RelativePath, completed, totalBytes));
                }
                nextFiles.Add(item.RelativePath);
            }

            var nextSet = new HashSet<string>(nextFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var obsolete in previousFiles.Where(file => !nextSet.Contains(file)))
            {
                var obsoletePath = ResolveContainedPath(root, obsolete);
                if (File.Exists(obsoletePath)) File.Delete(obsoletePath);
            }

            var manifestPath = Path.Combine(root, ManifestName);
            var temporaryManifest = manifestPath + ".tmp";
            await File.WriteAllTextAsync(temporaryManifest, JsonSerializer.Serialize(nextFiles), cancellationToken);
            File.Move(temporaryManifest, manifestPath, overwrite: true);
            progress?.Report(new InstallProgress("Complete", "", totalBytes, totalBytes));
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    private static List<(ZipArchiveEntry Entry, string RelativePath)> ReadAndValidateEntries(ZipArchive archive, string stage)
    {
        var entries = new List<(ZipArchiveEntry Entry, string RelativePath)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            var relativePath = entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            _ = ResolveContainedPath(stage, relativePath);
            if (!names.Add(relativePath)) throw new InvalidDataException("The package contains duplicate file paths.");
            entries.Add((entry, relativePath));
        }
        return entries;
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The package contains a path outside the installation folder.");
        return fullPath;
    }

    private static async Task<List<string>> ReadManifestAsync(string root, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, ManifestName);
        if (!File.Exists(path)) return [];
        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<List<string>>(content) ?? [];
        }
        catch (JsonException) { return []; }
    }
}