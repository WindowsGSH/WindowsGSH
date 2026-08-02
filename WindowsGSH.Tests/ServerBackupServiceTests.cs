using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using WindowsGSH.Core.Modules;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerBackupServiceTests
{
    [Fact]
    public async Task CreateBackupAsync_UsesModuleTargets_WhenPathsAreEmpty()
    {
        var instance = CreateInstance();
        Directory.CreateDirectory(Path.Combine(instance.InstallPath, "world"));
        await File.WriteAllTextAsync(Path.Combine(instance.InstallPath, "world", "level.dat"), "level");
        var module = new BackupModule([new ServerBackupTargetDefinition("world", "World", "world", IsDirectory: true)]);
        var service = new ServerBackupService(utcNow: () => new DateTimeOffset(2026, 5, 24, 18, 0, 0, TimeSpan.Zero));

        var result = await service.CreateBackupAsync(instance, module, relativePaths: []);

        Assert.True(result.Success);
        Assert.Equal(["world"], result.BackedUpPaths);
        using var archive = ZipFile.OpenRead(result.BackupPath!);
        Assert.NotNull(archive.GetEntry("world/level.dat"));
    }

    [Fact]
    public async Task CreateBackupAsync_RecordsMissingPaths_AndBacksUpFilesAndDirectories()
    {
        var instance = CreateInstance();
        Directory.CreateDirectory(Path.Combine(instance.InstallPath, "config"));
        await File.WriteAllTextAsync(Path.Combine(instance.InstallPath, "server.properties"), "port=25565");
        await File.WriteAllTextAsync(Path.Combine(instance.InstallPath, "config", "settings.json"), "{}");
        var service = new ServerBackupService();

        var result = await service.CreateBackupAsync(
            instance,
            module: null,
            relativePaths: ["server.properties", "config", "missing"]);

        Assert.Equal(["server.properties", "config"], result.BackedUpPaths);
        Assert.Equal(["missing"], result.MissingPaths);
        using var archive = ZipFile.OpenRead(result.BackupPath!);
        Assert.NotNull(archive.GetEntry("server.properties"));
        Assert.NotNull(archive.GetEntry("config/settings.json"));
    }

    [Fact]
    public async Task CreateBackupAsync_records_a_locked_file_as_failed_instead_of_aborting_the_whole_backup()
    {
        // Regression guard: backups are allowed while the server is running, and a running game
        // commonly holds world/database files open exclusively. AddFile previously had no per-file
        // error handling - the first IOException aborted the entire backup and left a corrupt
        // partial zip behind. One locked file must not prevent the rest of the backup from
        // completing.
        var instance = CreateInstance();
        var lockedPath = Path.Combine(instance.InstallPath, "world.db");
        var readablePath = Path.Combine(instance.InstallPath, "server.properties");
        await File.WriteAllTextAsync(lockedPath, "locked");
        await File.WriteAllTextAsync(readablePath, "port=25565");
        var service = new ServerBackupService();

        using (File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await service.CreateBackupAsync(instance, module: null, relativePaths: ["world.db", "server.properties"]);

            Assert.True(result.Success);
            Assert.Equal(["world.db"], result.FailedPaths);
            Assert.Equal(["server.properties"], result.BackedUpPaths);
            using var archive = ZipFile.OpenRead(result.BackupPath!);
            Assert.Null(archive.GetEntry("world.db"));
            Assert.NotNull(archive.GetEntry("server.properties"));
        }
    }

    [Fact]
    public async Task CreateBackupAsync_does_not_report_a_folder_as_backed_up_when_every_file_inside_it_failed()
    {
        // Regression guard: the folder branch used to add the folder to BackedUpPaths
        // unconditionally, regardless of whether any of its files actually made it into the
        // archive. A folder entirely held open by a running database (every file inside fails)
        // must not be reported as successfully backed up just because it was "processed."
        var instance = CreateInstance();
        var worldDir = Path.Combine(instance.InstallPath, "world");
        Directory.CreateDirectory(worldDir);
        var lockedFile = Path.Combine(worldDir, "level.dat");
        await File.WriteAllTextAsync(lockedFile, "locked");
        var service = new ServerBackupService();

        using (File.Open(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await service.CreateBackupAsync(instance, module: null, relativePaths: ["world"]);

            Assert.True(result.Success);
            Assert.DoesNotContain("world", result.BackedUpPaths);
            Assert.Contains("world", result.MissingPaths);
            Assert.Contains("world/level.dat", result.FailedPaths);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_removes_an_entry_when_the_source_fails_mid_copy()
    {
        var instance = CreateInstance();
        var sourcePath = Path.Combine(instance.InstallPath, "world.db");
        await File.WriteAllBytesAsync(sourcePath, new byte[8192]);
        var service = new ServerBackupService(
            openBackupSource: path => string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase)
                ? new FailingReadStream(new byte[8192])
                : File.OpenRead(path));

        var result = await service.CreateBackupAsync(instance, module: null, relativePaths: ["world.db"]);

        Assert.Equal(["world.db"], result.FailedPaths);
        using var archive = ZipFile.OpenRead(result.BackupPath!);
        Assert.Null(archive.GetEntry("world.db"));
        Assert.NotNull(archive.GetEntry("backup-manifest.json"));
    }

    [Fact]
    public async Task CreateBackupAsync_propagates_a_staging_write_failure_and_removes_the_partial_archive()
    {
        var instance = CreateInstance();
        await File.WriteAllBytesAsync(Path.Combine(instance.InstallPath, "world.db"), new byte[8192]);
        var service = new ServerBackupService(
            createBackupStaging: _ => new FailingWriteStream());

        await Assert.ThrowsAsync<IOException>(
            () => service.CreateBackupAsync(instance, module: null, relativePaths: ["world.db"]));

        Assert.Empty(Directory.GetFiles(service.ResolveBackupFolder(instance), "*.zip"));
    }

    [Fact]
    public async Task CreateBackupAsync_persists_failed_files_from_a_partially_backed_up_folder()
    {
        var instance = CreateInstance();
        var worldDir = Path.Combine(instance.InstallPath, "world");
        Directory.CreateDirectory(worldDir);
        var lockedFile = Path.Combine(worldDir, "locked.dat");
        await File.WriteAllTextAsync(lockedFile, "locked");
        await File.WriteAllTextAsync(Path.Combine(worldDir, "readable.dat"), "readable");
        var service = new ServerBackupService();

        using (File.Open(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await service.CreateBackupAsync(instance, module: null, relativePaths: ["world"]);
            var preview = service.GetBackupPreview(instance, result.BackupPath!);

            Assert.Contains("world", result.BackedUpPaths);
            Assert.Contains("world/locked.dat", result.FailedPaths);
            Assert.Contains("world/locked.dat", preview.FailedTargets);
            Assert.Contains("world", preview.FolderTargets);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_atomically_reserves_distinct_paths_for_concurrent_backups()
    {
        var destination = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"), "shared-backups");
        var first = WithBackupSettings(CreateInstance(), destination);
        var second = WithBackupSettings(CreateInstance(), destination);
        await File.WriteAllTextAsync(Path.Combine(first.InstallPath, "server.properties"), "first");
        await File.WriteAllTextAsync(Path.Combine(second.InstallPath, "server.properties"), "second");
        var fixedTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = new ServerBackupService(utcNow: () => fixedTime);

        var results = await Task.WhenAll(
            service.CreateBackupAsync(first, null, ["server.properties"]),
            service.CreateBackupAsync(second, null, ["server.properties"]));

        Assert.NotEqual(results[0].BackupPath, results[1].BackupPath);
        Assert.All(results, result => Assert.True(File.Exists(result.BackupPath)));
        Assert.Equal(2, Directory.GetFiles(destination, "*.zip").Length);
    }

    [Fact]
    public async Task CreateBackupAsync_serializes_creation_and_retention_for_a_shared_destination()
    {
        var destination = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"), "shared-backups");
        var first = WithBackupSettings(CreateInstance(), destination);
        var second = WithBackupSettings(CreateInstance(), destination);
        await File.WriteAllBytesAsync(Path.Combine(first.InstallPath, "world.dat"), new byte[8 * 1024 * 1024]);
        await File.WriteAllTextAsync(Path.Combine(second.InstallPath, "world.dat"), "second");
        var fixedTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var firstTask = new ServerBackupService(utcNow: () => fixedTime)
            .CreateBackupAsync(first, null, ["world.dat"], retentionCount: 1);
        var secondTask = new ServerBackupService(utcNow: () => fixedTime)
            .CreateBackupAsync(second, null, ["world.dat"], retentionCount: 1);
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result => Assert.True(result.Success));
        var retained = Assert.Single(Directory.GetFiles(destination, "*.zip"));
        using var archive = ZipFile.OpenRead(retained);
        Assert.NotNull(archive.GetEntry("backup-manifest.json"));
        Assert.NotNull(archive.GetEntry("world.dat"));
    }

    [Fact]
    public async Task CreateBackupAsync_does_not_prune_a_complete_backup_after_an_incomplete_backup()
    {
        var instance = CreateInstance();
        var sourcePath = Path.Combine(instance.InstallPath, "world.db");
        await File.WriteAllTextAsync(sourcePath, "complete world");
        var currentTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = new ServerBackupService(utcNow: () => currentTime);
        var complete = await service.CreateBackupAsync(instance, null, ["world.db"], retentionCount: 1);
        currentTime += TimeSpan.FromSeconds(1);

        using (File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var incomplete = await service.CreateBackupAsync(instance, null, ["world.db"], retentionCount: 1);

            Assert.Equal(["world.db"], incomplete.FailedPaths);
            Assert.Contains("Retention cleanup skipped", incomplete.BuildWarningSuffix(), StringComparison.Ordinal);
            Assert.True(File.Exists(complete.BackupPath));
            Assert.True(File.Exists(incomplete.BackupPath));
            Assert.Equal(2, Directory.GetFiles(service.ResolveBackupFolder(instance), "*.zip").Length);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_does_not_reserve_an_archive_when_target_enumeration_fails()
    {
        var instance = CreateInstance();
        var service = new ServerBackupService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateBackupAsync(instance, null, ThrowingPaths()));

        Assert.Empty(Directory.GetFiles(service.ResolveBackupFolder(instance), "*.zip"));
    }

    [Fact]
    public async Task CreateBackupAsync_preserves_the_completed_archive_when_retention_pruning_fails()
    {
        var instance = CreateInstance();
        var service = new ServerBackupService(utcNow: () => new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        var backupsFolder = service.ResolveBackupFolder(instance);
        Directory.CreateDirectory(backupsFolder);
        var oldBackup = Path.Combine(backupsFolder, "20260101-000000.zip");
        await File.WriteAllTextAsync(oldBackup, "locked old backup");
        File.SetLastWriteTimeUtc(oldBackup, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await File.WriteAllTextAsync(Path.Combine(instance.InstallPath, "server.properties"), "x");

        using (File.Open(oldBackup, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await service.CreateBackupAsync(instance, null, ["server.properties"], retentionCount: 1);

            Assert.True(result.Success);
            Assert.True(File.Exists(result.BackupPath));
            Assert.Contains("Retention cleanup failed", result.Message, StringComparison.Ordinal);
            Assert.Contains("Retention cleanup failed", result.BuildWarningSuffix(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_deletes_the_partial_zip_when_cancelled_mid_backup()
    {
        // Regression guard: a cancelled/aborted backup (or any failure not handled by the per-file
        // try/catch above) previously left a corrupt partial .zip behind in the backups folder,
        // where it would show up in ListBackups, count against retention, and fail on restore.
        //
        // Pause the first source read so cancellation happens at a known point after archive
        // construction starts but before the directory loop advances to the second file. Watching
        // for the reserved zip on a separate worker was still scheduler-dependent on loaded CI:
        // the backup could finish before that watcher was ever scheduled.
        var instance = CreateInstance();
        Directory.CreateDirectory(Path.Combine(instance.InstallPath, "world"));
        await File.WriteAllBytesAsync(Path.Combine(instance.InstallPath, "world", "first.dat"), new byte[256]);
        await File.WriteAllBytesAsync(Path.Combine(instance.InstallPath, "world", "second.dat"), new byte[256]);

        var fixedTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRead = new ManualResetEventSlim();
        var pauseNextRead = 1;
        var service = new ServerBackupService(
            utcNow: () => fixedTime,
            openBackupSource: path => Interlocked.Exchange(ref pauseNextRead, 0) == 1
                ? new PausingReadStream(File.ReadAllBytes(path), readStarted, releaseRead)
                : File.OpenRead(path));
        var backupsFolder = service.ResolveBackupFolder(instance);
        var expectedBackupPath = Path.Combine(backupsFolder, $"{fixedTime.LocalDateTime:yyyyMMdd-HHmmss}.zip");

        using var cts = new CancellationTokenSource();
        Task<BackupResult>? backupTask = null;
        try
        {
            backupTask = service.CreateBackupAsync(
                instance,
                module: null,
                relativePaths: ["world"],
                cancellationToken: cts.Token);
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            cts.Cancel();
            releaseRead.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backupTask);
        }
        finally
        {
            releaseRead.Set();
            if (backupTask != null)
            {
                try
                {
                    await backupTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // The cancellation/failure is asserted above; cleanup must still release the worker.
                }
            }
        }

        Assert.False(File.Exists(expectedBackupPath));
    }

    [Fact]
    public async Task CreateBackupAsync_does_not_block_the_calling_thread_while_zipping()
    {
        // Regression guard: CreateBackupAsync used to be async in name only - it built the entire
        // zip inline and returned Task.FromResult, so a caller awaiting it directly on a UI/dispatcher
        // thread froze that thread for the whole backup. If the work is genuinely dispatched to a
        // background thread, the returned task should not already be complete immediately after the
        // call returns, for a backup with enough data to take a non-trivial amount of time.
        var instance = CreateInstance();
        Directory.CreateDirectory(Path.Combine(instance.InstallPath, "world"));
        for (var i = 0; i < 200; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(instance.InstallPath, "world", $"chunk-{i}.dat"), new byte[256 * 1024]);
        }

        var service = new ServerBackupService();

        var task = service.CreateBackupAsync(instance, module: null, relativePaths: ["world"]);

        Assert.False(task.IsCompleted);
        var result = await task;
        Assert.True(result.Success);
    }

    [Fact]
    public async Task CreateBackupAsync_UsesDefaultDestination_WhenCustomDestinationIsBlank()
    {
        var instance = CreateInstance();
        await File.WriteAllTextAsync(Path.Combine(instance.InstallPath, "server.properties"), "x");
        var service = new ServerBackupService();

        var result = await service.CreateBackupAsync(instance, null, ["server.properties"]);

        Assert.Equal(Path.Combine(instance.ServerFolder, "backups"), Path.GetDirectoryName(result.BackupPath));
    }

    [Fact]
    public async Task CreateBackupAsync_UsesCustomAbsoluteDestination()
    {
        var instance = CreateInstance();
        var destination = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"), "custom-backups");
        instance = WithBackupSettings(instance, destination);
        await File.WriteAllTextAsync(Path.Combine(instance.InstallPath, "server.properties"), "x");
        var service = new ServerBackupService();

        var result = await service.CreateBackupAsync(instance, null, ["server.properties"]);

        Assert.Equal(destination, Path.GetDirectoryName(result.BackupPath));
        Assert.Equal(result.BackupPath, Assert.Single(service.ListBackups(instance)));
    }

    [Fact]
    public async Task RestoreAndDelete_WorkWithCustomDestination()
    {
        var instance = CreateInstance();
        var destination = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"), "custom-backups");
        instance = WithBackupSettings(instance, destination);
        var target = Path.Combine(instance.InstallPath, "server.properties");
        await File.WriteAllTextAsync(target, "before");
        var service = new ServerBackupService();
        var backup = await service.CreateBackupAsync(instance, null, ["server.properties"]);
        await File.WriteAllTextAsync(target, "changed");

        var restore = await service.RestoreBackupAsync(instance, backup.BackupPath!);
        var delete = await service.DeleteBackupAsync(instance, backup.BackupPath!);

        Assert.True(restore.Success);
        Assert.Equal("before", await File.ReadAllTextAsync(target));
        Assert.True(delete.Success);
        Assert.False(File.Exists(backup.BackupPath));
    }

    [Fact]
    public void ResolveBackupFolder_ResolvesRelativeDestination_FromServerFolder()
    {
        var instance = WithBackupSettings(CreateInstance(), Path.Combine("archives", "daily"));
        var service = new ServerBackupService();

        var result = service.ResolveBackupFolder(instance);

        Assert.Equal(Path.Combine(instance.ServerFolder, "archives", "daily"), result);
    }

    [Fact]
    public void ValidateBackupDestination_RejectsDestinationInsideInstallPath()
    {
        var instance = CreateInstance();
        instance = WithBackupSettings(instance, Path.Combine(instance.InstallPath, "backups"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServerBackupService().ValidateBackupDestination(instance));

        Assert.Contains("cannot be inside", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateBackupDestination_RejectsUnwritableFilePath()
    {
        var instance = CreateInstance();
        var filePath = Path.Combine(instance.ServerFolder, "not-a-folder");
        File.WriteAllText(filePath, "x");
        instance = WithBackupSettings(instance, filePath);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServerBackupService().ValidateBackupDestination(instance));

        Assert.Contains("not writable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CustomTargets_AreAddedToModuleDefaults_AndPreviewSeparatesTypes()
    {
        var instance = CreateInstance();
        Directory.CreateDirectory(Path.Combine(instance.InstallPath, "world"));
        await File.WriteAllTextAsync(Path.Combine(instance.InstallPath, "world", "level.dat"), "level");
        await File.WriteAllTextAsync(Path.Combine(instance.InstallPath, "server.properties"), "x");
        var module = new BackupModule([new ServerBackupTargetDefinition("world", "World", "world", IsDirectory: true)]);
        var service = new ServerBackupService();

        var result = await service.CreateBackupAsync(instance, module, ["server.properties"]);
        Directory.Delete(Path.Combine(instance.InstallPath, "world"), recursive: true);
        File.Delete(Path.Combine(instance.InstallPath, "server.properties"));
        var preview = service.GetBackupPreview(instance, result.BackupPath!);

        Assert.Equal(["world", "server.properties"], result.BackedUpPaths);
        Assert.Equal(["server.properties"], preview.FileTargets);
        Assert.Equal(["world"], preview.FolderTargets);
        Assert.Equal(2, preview.NewFileCount);
        Assert.Equal(0, preview.ExistingFileCount);
    }

    [Fact]
    public async Task BackupPreview_ReportsFilesThatWouldBeOverwritten()
    {
        var instance = CreateInstance();
        var target = Path.Combine(instance.InstallPath, "server.properties");
        await File.WriteAllTextAsync(target, "before");
        var service = new ServerBackupService();
        var result = await service.CreateBackupAsync(instance, null, ["server.properties"]);

        var preview = service.GetBackupPreview(instance, result.BackupPath!);

        Assert.Equal(1, preview.ExistingFileCount);
        Assert.Equal(0, preview.NewFileCount);
        Assert.Equal("1 overwrite, 0 new", preview.RestoreSummary);
    }

    [Fact]
    public async Task CreateBackupAsync_WritesManifestWithModuleVersion()
    {
        var instance = CreateInstance();
        await File.WriteAllTextAsync(Path.Combine(instance.InstallPath, "server.properties"), "x");
        var service = new ServerBackupService(utcNow: () => new DateTimeOffset(2026, 5, 24, 19, 0, 0, TimeSpan.Zero));

        var result = await service.CreateBackupAsync(instance, new BackupModule(), ["server.properties"]);

        using var archive = ZipFile.OpenRead(result.BackupPath!);
        var manifest = archive.GetEntry("backup-manifest.json")!;
        using var reader = new StreamReader(manifest.Open());
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.Equal("test", document.RootElement.GetProperty("moduleId").GetString());
        Assert.Equal("9.8.7", document.RootElement.GetProperty("moduleVersion").GetString());
        Assert.Equal("server.properties", document.RootElement.GetProperty("paths")[0].GetString());
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData(@"C:\outside.txt")]
    [InlineData(@"\\server\share\outside.txt")]
    [InlineData("nested\\..\\../outside.txt")]
    [InlineData(".")]
    public async Task RestoreBackupAsync_RejectsEntriesOutsideInstallPath(string entryName)
    {
        var instance = CreateInstance();
        var backups = Path.Combine(instance.ServerFolder, "backups");
        Directory.CreateDirectory(backups);
        var backupPath = Path.Combine(backups, "malicious.zip");
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            var good = archive.CreateEntry("world/file.txt");
            await using (var stream = good.Open())
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync("good");
            }

            var bad = archive.CreateEntry(entryName);
            await using var badStream = bad.Open();
            await using var badWriter = new StreamWriter(badStream);
            await badWriter.WriteAsync("bad");
        }

        var service = new ServerBackupService();
        var preview = service.GetBackupPreview(instance, backupPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreBackupAsync(instance, backupPath));

        Assert.Equal(1, preview.SkippedUnsafeFileCount);
        Assert.DoesNotContain(entryName, preview.Files);
        Assert.False(File.Exists(Path.Combine(instance.InstallPath, "world", "file.txt")));
        Assert.False(File.Exists(Path.Combine(instance.ServerFolder, "outside.txt")));
    }

    [Fact]
    public async Task RestoreBackupAsync_BlocksRunningServerUnlessConfirmed()
    {
        var instance = CreateInstance();
        var backupPath = await CreateSimpleBackupAsync(instance);
        var service = new ServerBackupService(isRunning: (_, _) => true);

        var blocked = await service.RestoreBackupAsync(instance, backupPath, new BackupModule());
        var allowed = await service.RestoreBackupAsync(instance, backupPath, new BackupModule(), confirmedWhileRunning: true);

        Assert.False(blocked.Success);
        Assert.True(allowed.Success);
    }

    [Fact]
    public async Task DeleteBackupAsync_DeletesBackupInsideServerBackupFolder()
    {
        var instance = CreateInstance();
        var backupPath = await CreateSimpleBackupAsync(instance);
        var service = new ServerBackupService();

        var result = await service.DeleteBackupAsync(instance, backupPath);

        Assert.True(result.Success);
        Assert.False(File.Exists(backupPath));
    }

    private static async Task<string> CreateSimpleBackupAsync(ServerInstance instance)
    {
        await File.WriteAllTextAsync(Path.Combine(instance.InstallPath, "server.properties"), "x");
        var result = await new ServerBackupService().CreateBackupAsync(instance, null, ["server.properties"]);
        return result.BackupPath!;
    }

    private static IEnumerable<string> ThrowingPaths()
    {
        yield return "server.properties";
        throw new InvalidOperationException("Target enumeration failed.");
    }

    private sealed class FailingReadStream(byte[] data) : MemoryStream(data)
    {
        private bool _hasRead;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_hasRead)
            {
                throw new IOException("Simulated mid-read failure.");
            }

            _hasRead = true;
            return base.Read(buffer, offset, Math.Min(count, 128));
        }

        public override int Read(Span<byte> buffer)
        {
            if (_hasRead)
            {
                throw new IOException("Simulated mid-read failure.");
            }

            _hasRead = true;
            return base.Read(buffer[..Math.Min(buffer.Length, 128)]);
        }
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("Simulated destination full.");

        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new IOException("Simulated destination full.");
    }

    private sealed class PausingReadStream(
        byte[] data,
        TaskCompletionSource readStarted,
        ManualResetEventSlim releaseRead) : MemoryStream(data)
    {
        private int _hasPaused;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Exchange(ref _hasPaused, 1) == 0)
            {
                readStarted.TrySetResult();
                releaseRead.Wait();
            }

            return base.Read(buffer, offset, count);
        }
    }

    private static ServerInstance CreateInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests", Guid.NewGuid().ToString("N"));
        var serverFolder = Path.Combine(root, "1");
        var installPath = Path.Combine(serverFolder, "files");
        Directory.CreateDirectory(installPath);
        return new ServerInstance(
            "1",
            "Test Server",
            "test",
            serverFolder,
            installPath,
            Path.Combine(serverFolder, "ServerConfig.json"),
            new Dictionary<string, object?>());
    }

    private static ServerInstance WithBackupSettings(ServerInstance instance, string destination)
    {
        return instance with
        {
            AppSettings = instance.AppSettings with
            {
                Backup = instance.AppSettings.Backup with { Destination = destination }
            }
        };
    }

    private sealed class BackupModule : IGameServerModule
    {
        private readonly IReadOnlyList<ServerBackupTargetDefinition> _targets;

        public BackupModule(IReadOnlyList<ServerBackupTargetDefinition>? targets = null)
        {
            _targets = targets ?? [];
        }

        public string Id => "test";
        public string Name => "Test";
        public string Version => "9.8.7";
        public ModuleCapabilities Capabilities => new(false, false, false, false, false, false, true, false);
        public ModuleRuntimeDefinition Runtime => new("server.exe", ["server"]);
        public SteamInstallDefinition? SteamInstall => null;
        public Task<InstallPlan> CreateInstallPlanAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<ConfigFieldDefinition> GetConfigFields() => [];
        public Task<IReadOnlyDictionary<string, object?>> ReadConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        public Task WriteConfigFileSettingsAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<QueryResult> QueryAsync(ServerInstance instance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> ExecuteRconCommandAsync(ServerInstance instance, string command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<ServerBackupTargetDefinition> GetBackupTargets() => _targets;
        public IReadOnlyList<ServerAddonDefinition> GetAddonDefinitions() => [];
        public ServerAddonStatus GetAddonStatus(ServerInstance instance, string addonId) => new(addonId, false, false, "");
        public Task InstallAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAddonAsync(ServerInstance instance, string addonId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<Process>> StartAddonProcessesAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Process>>([]);
        public string GetServerName(IReadOnlyDictionary<string, object?> settings) => "Test Server";
        public ServerDisplayInfo GetDisplayInfo(ServerInstance instance) => new("127.0.0.1", "27015", "");
        public Task<ProcessStartInfo> CreateStartInfoAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult(new ProcessStartInfo("server.exe"));
        public Task<Process?> StartAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.FromResult<Process?>(null);
        public Task StopAsync(ServerInstance instance, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsInstallValid(ServerInstance instance) => true;
        public string? GetConsoleLogPath(ServerInstance instance) => null;
    }
}

