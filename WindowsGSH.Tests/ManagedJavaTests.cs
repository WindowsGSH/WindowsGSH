using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using WindowsGSH.Core.Java;
using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ManagedJavaTests
{
    // ── ManagedJavaRelease ───────────────────────────────────────────────────

    [Fact]
    public void Release_Id_CombinesVendorMajorAndArchitecture()
    {
        var release = MakeRelease(vendor: "Temurin", major: 21, arch: "x64");

        Assert.Equal("Temurin-21-x64", release.Id);
    }

    [Fact]
    public void Release_InstallSubPath_UsesSeparator()
    {
        var release = MakeRelease(vendor: "Temurin", major: 21, arch: "x64");

        Assert.Equal(Path.Combine("Temurin", "21", "x64"), release.InstallSubPath);
    }

    // ── ManagedJavaCatalogue — refresh fallback (P1-1) ───────────────────────

    [Fact]
    public async Task Catalogue_RefreshFromApi_RetainsPreviousEntry_WhenVersionFetchFails()
    {
        // Arrange: catalogue already has Java 21 cached.
        var existing = MakeRelease(vendor: "Temurin", major: 21, arch: "x64");
        var catalogue = new ManagedJavaCatalogue([existing]);

        // API returns [21] as LTS but the per-version call fails.
        var ltsJson = """{"available_lts_releases":[21]}""";
        var errorJson = """{"error":"service unavailable"}"""; // not a valid release array — will throw

        var responses = new Queue<byte[]>([
            System.Text.Encoding.UTF8.GetBytes(ltsJson),
            System.Text.Encoding.UTF8.GetBytes(errorJson)
        ]);
        using var handler = new FakeMultiResponseHttpHandler(responses);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.adoptium.net") };
        var client = new AdoptiumApiClient(http);

        // Act
        var results = await catalogue.RefreshFromApiAsync(client);

        // Assert: the previous Java 21 entry is preserved.
        Assert.Single(results);
        Assert.Equal(existing.ReleaseVersion, results[0].ReleaseVersion);
        Assert.Equal(21, results[0].MajorVersion);
    }

    [Fact]
    public void Catalogue_LoadsLastKnownGood_WhenPrimaryCacheIsCorrupt()
    {
        var root = CreateTempDir();
        try
        {
            var cachePath = Path.Combine(root, "catalogue.json");
            var fallbackPath = Path.Combine(root, "catalogue.last-good.json");
            var existing = MakeRelease();
            File.WriteAllText(cachePath, """{"FetchedAt":""");
            File.WriteAllText(
                fallbackPath,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    FetchedAt = DateTimeOffset.UtcNow,
                    Releases = new[] { existing }
                }));

            var catalogue = new ManagedJavaCatalogue(cachePath);

            Assert.Equal(existing, Assert.Single(catalogue.GetReleases()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Catalogue_Refresh_ReplacesCorruptPrimaryAtomically()
    {
        var root = CreateTempDir();
        try
        {
            var cachePath = Path.Combine(root, "catalogue.json");
            File.WriteAllText(cachePath, "corrupt");
            var catalogue = new ManagedJavaCatalogue(cachePath);
            var ltsJson = """{"available_lts_releases":[21]}""";
            var releaseJson = CreateReleaseApiJson(21, "jdk-21.0.11+10");
            var responses = new Queue<byte[]>([
                System.Text.Encoding.UTF8.GetBytes(ltsJson),
                System.Text.Encoding.UTF8.GetBytes(releaseJson)
            ]);
            using var handler = new FakeMultiResponseHttpHandler(responses);
            using var http = new HttpClient(handler);

            await catalogue.RefreshFromApiAsync(new AdoptiumApiClient(http));

            var reloaded = new ManagedJavaCatalogue(cachePath);
            Assert.Equal(21, Assert.Single(reloaded.GetReleases()).MajorVersion);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── ManagedJavaInstallService — rollback failure preserves backup (P1-2) ─

    [Fact]
    public async Task InstallAsync_PreservesBackup_AndReportsLocation_WhenRollbackFails()
    {
        var root = CreateTempDir();
        try
        {
            var archiveContent = CreateMinimalJreZip();
            var sha256 = Convert.ToHexString(SHA256.HashData(archiveContent));
            var release = MakeRelease(sha256: sha256, maxBytes: archiveContent.Length + 1024);
            var installDir = Path.Combine(root, release.InstallSubPath);
            var existingExe = Path.Combine(installDir, "bin", "java.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(existingExe)!);
            File.WriteAllText(existingExe, "original java");
            var moveCount = 0;
            void MoveDirectory(string source, string destination)
            {
                moveCount++;
                if (moveCount == 1)
                {
                    Directory.Move(source, destination);
                    return;
                }

                throw new IOException(moveCount == 2 ? "Swap failed." : "Rollback failed.");
            }

            using var handler = new FakeHttpHandler(archiveContent);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var service = new ManagedJavaInstallService(root, http, File.Exists, MoveDirectory);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(release));

            Assert.Contains("automatic rollback also failed", ex.Message);
            var backupDir = Assert.Single(Directory.EnumerateDirectories(
                Path.GetDirectoryName(installDir)!,
                $"{Path.GetFileName(installDir)}.old.*"));
            Assert.True(File.Exists(Path.Combine(backupDir, "bin", "java.exe")));
            Assert.Contains(backupDir, ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── AdoptiumApiClient — JSON parsing ────────────────────────────────────

    [Fact]
    public async Task AdoptiumApiClient_ParsesAvailableLtsVersions()
    {
        var json = """{"available_lts_releases":[8,11,17,21,25],"available_releases":[8,11,17,21,25]}""";
        using var handler = new FakeHttpHandler(System.Text.Encoding.UTF8.GetBytes(json));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.adoptium.net") };
        var client = new AdoptiumApiClient(http);

        var versions = await client.GetAvailableLtsVersionsAsync();

        Assert.Equal([8, 11, 17, 21, 25], versions);
    }

    [Fact]
    public async Task AdoptiumApiClient_ParsesReleaseItem_JdkVersionFormat()
    {
        var json = """
            [{
              "release_name": "jdk-21.0.11+10",
              "release_link": "https://example.test/releases/tag/jdk-21.0.11+10",
              "binary": {
                "package": {
                  "link": "https://example.test/OpenJDK21U-jre_x64_windows.zip",
                  "checksum": "be26677aaa20b39a62edcaab4c8857a8b76673b0f45abc0b6143b142b62717e4",
                  "size": 49005708
                }
              }
            }]
            """;
        using var handler = new FakeHttpHandler(System.Text.Encoding.UTF8.GetBytes(json));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.adoptium.net") };
        var client = new AdoptiumApiClient(http);

        var release = await client.GetLatestWindowsX64JreAsync(21);

        Assert.NotNull(release);
        Assert.Equal("Temurin", release.Vendor);
        Assert.Equal(21, release.MajorVersion);
        Assert.Equal("x64", release.Architecture);
        Assert.Equal("21.0.11+10", release.ReleaseVersion);
        Assert.Equal("BE26677AAA20B39A62EDCAAB4C8857A8B76673B0F45ABC0B6143B142B62717E4", release.ArchiveSha256);
        Assert.Equal("https://example.test/OpenJDK21U-jre_x64_windows.zip", release.DownloadUrl);
        Assert.True(release.MaxArchiveSizeBytes >= 49005708);
    }

    [Fact]
    public async Task AdoptiumApiClient_ParsesReleaseItem_Java8NameFormat()
    {
        var json = """
            [{
              "release_name": "jdk8u492-b09",
              "release_link": "https://example.test/releases/tag/jdk8u492-b09",
              "binary": {
                "package": {
                  "link": "https://example.test/OpenJDK8U-jre_x64_windows.zip",
                  "checksum": "bb25b002556afc7ef158cd95ec6270dddb3eecba69acdd7abb9d28b2e9ff0f5e",
                  "size": 45000000
                }
              }
            }]
            """;
        using var handler = new FakeHttpHandler(System.Text.Encoding.UTF8.GetBytes(json));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.adoptium.net") };
        var client = new AdoptiumApiClient(http);

        var release = await client.GetLatestWindowsX64JreAsync(8);

        Assert.NotNull(release);
        Assert.Equal(8, release.MajorVersion);
        Assert.Equal("8u492-b09", release.ReleaseVersion);
        Assert.Equal("BB25B002556AFC7EF158CD95EC6270DDDB3EECBA69ACDD7ABB9D28B2E9FF0F5E", release.ArchiveSha256);
    }

    [Fact]
    public async Task AdoptiumApiClient_ReturnsNullWhenResponseIsEmpty()
    {
        using var handler = new FakeHttpHandler(System.Text.Encoding.UTF8.GetBytes("[]"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.adoptium.net") };
        var client = new AdoptiumApiClient(http);

        var release = await client.GetLatestWindowsX64JreAsync(21);

        Assert.Null(release);
    }

    // ── ManagedJavaCatalogue ─────────────────────────────────────────────────

    [Fact]
    public void Catalogue_GetRelease_FindsByIdCaseInsensitive()
    {
        var release = MakeRelease("Temurin", 21, "x64");
        var catalogue = new ManagedJavaCatalogue([release]);

        Assert.Equal(release, catalogue.GetRelease("temurin-21-x64"));
        Assert.Equal(release, catalogue.GetRelease("TEMURIN-21-X64"));
    }

    [Fact]
    public void Catalogue_GetRelease_ReturnsNullForUnknownId()
    {
        var catalogue = new ManagedJavaCatalogue([]);

        Assert.Null(catalogue.GetRelease("Unknown-99-x64"));
    }

    // ── ManagedJavaInstallService — path traversal protection ────────────────

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("nested/../../escape.txt")]
    [InlineData(@"C:\escape.txt")]
    [InlineData(@"\\server\share\escape.txt")]
    [InlineData("nested\\..\\../escape.txt")]
    [InlineData(".")]
    public void ExtractArchiveSafely_RejectsPathTraversal(string entryName)
    {
        var root = CreateTempDir();
        try
        {
            var archivePath = Path.Combine(root, "unsafe.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(entryName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("should not exist");
            }

            Assert.Throws<InvalidDataException>(() =>
                ManagedJavaInstallService.ExtractArchiveSafely(
                    archivePath,
                    Path.Combine(root, "extract")));

            Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExtractArchiveSafely_ExtractsValidArchive()
    {
        var root = CreateTempDir();
        try
        {
            var archivePath = Path.Combine(root, "good.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                archive.CreateEntry("bin/");
                var entry = archive.CreateEntry("bin/java.exe");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("fake java");
            }

            var extractionRoot = Path.Combine(root, "extract");
            Directory.CreateDirectory(Path.Combine(extractionRoot, "bin"));
            File.WriteAllText(Path.Combine(extractionRoot, "bin", "java.exe"), "old");
            ManagedJavaInstallService.ExtractArchiveSafely(archivePath, extractionRoot);

            Assert.True(File.Exists(Path.Combine(root, "extract", "bin", "java.exe")));
            Assert.Equal("fake java", File.ReadAllText(Path.Combine(root, "extract", "bin", "java.exe")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_FindsJavaExecutableInTwoNestedArchiveDirectories()
    {
        var root = CreateTempDir();
        try
        {
            byte[] archiveContent;
            using (var memory = new MemoryStream())
            {
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = archive.CreateEntry("package/jdk-21-jre/bin/java.exe");
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write("fake java executable");
                }

                archiveContent = memory.ToArray();
            }

            var release = MakeRelease(
                sha256: Convert.ToHexString(SHA256.HashData(archiveContent)),
                maxBytes: archiveContent.Length + 1024);
            using var handler = new FakeHttpHandler(archiveContent);
            using var http = new HttpClient(handler);
            var service = new ManagedJavaInstallService(root, http, File.Exists);

            await service.InstallAsync(release);

            Assert.EndsWith(
                Path.Combine("package", "jdk-21-jre", "bin", "java.exe"),
                service.GetJavaExecutablePath(release),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── ManagedJavaInstallService — download size protection ────────────────

    [Fact]
    public async Task InstallAsync_FailsWhenArchiveSha256IsEmpty()
    {
        var root = CreateTempDir();
        try
        {
            var release = MakeRelease(sha256: string.Empty);
            var service = new ManagedJavaInstallService(root, new HttpClient(), File.Exists);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.InstallAsync(release));

            Assert.Contains("SHA-256 checksum", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_DeletesTempFileOnChecksumFailure()
    {
        var root = CreateTempDir();
        try
        {
            var archiveContent = CreateMinimalJreZip();
            var wrongSha256 = new string('A', 64);
            var release = MakeRelease(sha256: wrongSha256, maxBytes: archiveContent.Length + 1024);

            using var handler = new FakeHttpHandler(archiveContent);
            using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var service = new ManagedJavaInstallService(root, httpClient, File.Exists);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.InstallAsync(release));

            Assert.False(Directory.GetFiles(Path.GetTempPath(), "WindowsGSH.java.*.zip").Length > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_FailsWhenDownloadExceedsMaxSize()
    {
        var root = CreateTempDir();
        try
        {
            var archiveContent = CreateMinimalJreZip();
            var release = MakeRelease(sha256: "A".PadRight(64, 'A'), maxBytes: 10);

            using var handler = new FakeHttpHandler(archiveContent);
            using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var service = new ManagedJavaInstallService(root, httpClient, File.Exists);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.InstallAsync(release));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_CancellationTokenPropagates()
    {
        var root = CreateTempDir();
        try
        {
            var release = MakeRelease(sha256: new string('A', 64), maxBytes: 1024 * 1024);
            using var cts = new CancellationTokenSource();

            using var handler = new FakeHttpHandler(CreateMinimalJreZip(), cancelAfterBytes: 5);
            using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var service = new ManagedJavaInstallService(root, httpClient, File.Exists);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.InstallAsync(release, cancellationToken: cts.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── ManagedJavaInstallService — path traversal guard (P1-2) ─────────────

    [Fact]
    public void GetInstallDirectory_Throws_WhenVendorIsTraversal()
    {
        var root = CreateTempDir();
        try
        {
            var release = MakeRelease(vendor: "..");
            var service = new ManagedJavaInstallService(root, new HttpClient(), File.Exists);

            Assert.Throws<InvalidOperationException>(() => service.GetInstallDirectory(release));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetInstallDirectory_Succeeds_ForLegitimateRelease()
    {
        var root = CreateTempDir();
        try
        {
            var release = MakeRelease(vendor: "Temurin", major: 21, arch: "x64");
            var service = new ManagedJavaInstallService(root, new HttpClient(), File.Exists);

            var dir = service.GetInstallDirectory(release);

            Assert.StartsWith(root, dir, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── ManagedJavaInstallService — staging + atomic swap (P1-3) ────────────

    [Fact]
    public async Task InstallAsync_PreservesExistingRuntime_WhenChecksumFails()
    {
        var root = CreateTempDir();
        try
        {
            var archiveContent = CreateMinimalJreZip();
            var release = MakeRelease(sha256: new string('A', 64), maxBytes: archiveContent.Length + 1024);

            // Place a working runtime at the install directory.
            var installDir = Path.Combine(root, release.InstallSubPath);
            var existingExe = Path.Combine(installDir, "bin", "java.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(existingExe)!);
            File.WriteAllText(existingExe, "original java");

            using var handler = new FakeHttpHandler(archiveContent);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var service = new ManagedJavaInstallService(root, http, File.Exists);

            await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(release));

            Assert.True(File.Exists(existingExe), "Original runtime was destroyed when checksum failed.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_PreservesExistingRuntime_WhenExtractionFails()
    {
        var root = CreateTempDir();
        try
        {
            // Build a zip that passes the SHA-256 check but fails during extraction (path traversal entry).
            byte[] badZip;
            string sha256;
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = archive.CreateEntry("../escape.txt");
                    using var w = new StreamWriter(entry.Open());
                    w.Write("traversal");
                }
                badZip = ms.ToArray();
            }
            using (var sha = SHA256.Create())
                sha256 = Convert.ToHexString(sha.ComputeHash(badZip));

            var release = MakeRelease(sha256: sha256, maxBytes: badZip.Length + 1024);

            // Place a working runtime at the install directory.
            var installDir = Path.Combine(root, release.InstallSubPath);
            var existingExe = Path.Combine(installDir, "bin", "java.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(existingExe)!);
            File.WriteAllText(existingExe, "original java");

            using var handler = new FakeHttpHandler(badZip);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var service = new ManagedJavaInstallService(root, http, File.Exists);

            // Checksum passes; extraction fails due to path traversal.
            await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(release));

            Assert.True(File.Exists(existingExe), "Original runtime was destroyed when extraction failed.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── ServerLifecycleService — managed path injection (P1-1) ───────────────

    [Fact]
    public void WithResolvedJavaPath_InjectsManagedPathIntoRuntimePath()
    {
        // This is tested indirectly via ManagedJavaStore.TryGetJavaExecutablePath.
        // We verify ResolveEffectiveJavaPath returns the managed path when ManagedRuntimeId is set.
        var root = CreateTempDir();
        try
        {
            var release = MakeRelease(vendor: "Temurin", major: 21, arch: "x64");

            // Place a fake java.exe so IsInstalled returns true
            var installDir = Path.Combine(root, release.InstallSubPath);
            var javaExe = Path.Combine(installDir, "bin", "java.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(javaExe)!);
            File.WriteAllText(javaExe, "fake java");

            var catalogue = new ManagedJavaCatalogue([release]);
            var installService = new ManagedJavaInstallService(root, new HttpClient(), File.Exists);
            var store = new ManagedJavaStore(installService, catalogue);

            var settings = MakeJavaSettings(managedRuntimeId: release.Id);
            var effectivePath = JavaRuntimeManager.ResolveEffectiveJavaPath(settings, store);

            Assert.Equal(javaExe, effectivePath, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Store_ResolvesInstalledRuntime_WhenCatalogueIsEmpty()
    {
        var root = CreateTempDir();
        try
        {
            var release = MakeRelease(vendor: "Temurin", major: 21, arch: "x64");
            var javaExe = Path.Combine(root, release.InstallSubPath, "bin", "java.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(javaExe)!);
            File.WriteAllText(javaExe, "fake java");

            var installService = new ManagedJavaInstallService(root, new HttpClient(), File.Exists);
            var store = new ManagedJavaStore(installService, new ManagedJavaCatalogue([]));

            Assert.Equal(
                javaExe,
                store.TryGetJavaExecutablePath(release.Id),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("Temurin")]
    [InlineData("Temurin-not-a-version-x64")]
    [InlineData("../Temurin-21-x64")]
    public void Store_DoesNotReconstructInvalidRuntimeId(string runtimeId)
    {
        var store = new ManagedJavaStore(
            new ManagedJavaInstallService(),
            new ManagedJavaCatalogue([]));

        Assert.Null(store.TryGetJavaExecutablePath(runtimeId));
    }

    // ── ManagedJavaStore — reference tracking ────────────────────────────────

    [Fact]
    public void Store_CanRemove_ReturnsFalseWhenServerReferencesRuntime()
    {
        var settings = new[]
        {
            MakeJavaSettings(managedRuntimeId: "Temurin-21-x64"),
            MakeJavaSettings(managedRuntimeId: string.Empty)
        };

        var store = new ManagedJavaStore(new ManagedJavaInstallService(), new ManagedJavaCatalogue());

        Assert.False(store.CanRemove("Temurin-21-x64", settings));
        Assert.True(store.CanRemove("Temurin-17-x64", settings));
    }

    [Fact]
    public void Store_Remove_ThrowsWhenRuntimeIsReferenced()
    {
        var settings = new[] { MakeJavaSettings(managedRuntimeId: "Temurin-21-x64") };
        var store = new ManagedJavaStore(new ManagedJavaInstallService(), new ManagedJavaCatalogue());

        Assert.Throws<InvalidOperationException>(() =>
            store.Remove("Temurin-21-x64", settings));
    }

    [Fact]
    public void Store_IsReferenced_CaseInsensitive()
    {
        var settings = new[] { MakeJavaSettings(managedRuntimeId: "temurin-21-x64") };
        var store = new ManagedJavaStore(new ManagedJavaInstallService(), new ManagedJavaCatalogue());

        Assert.True(store.IsReferenced("TEMURIN-21-X64", settings));
    }

    [Fact]
    public void Store_BlocksRemoval_WhenServerUsesManagedJavaPathDirectly()
    {
        var root = CreateTempDir();
        try
        {
            var release = MakeRelease(vendor: "Temurin", major: 21, arch: "x64");
            var javaExe = Path.Combine(root, release.InstallSubPath, "bin", "java.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(javaExe)!);
            File.WriteAllText(javaExe, "fake java");

            var installService = new ManagedJavaInstallService(root, new HttpClient(), File.Exists);
            var store = new ManagedJavaStore(
                installService,
                new ManagedJavaCatalogue([release]));
            var settings = new[] { MakeJavaSettings(runtimePath: javaExe) };

            Assert.True(store.IsReferenced(release.Id, settings));
            Assert.False(store.CanRemove(release.Id, settings));
            Assert.Throws<InvalidOperationException>(() => store.Remove(release.Id, settings));
            Assert.True(File.Exists(javaExe));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── JavaRuntimeManager — managed selection ───────────────────────────────

    [Fact]
    public void ResolveEffectiveJavaPath_PrefersCustomPathOverNull()
    {
        var settings = MakeJavaSettings(runtimePath: @"C:\java\bin\java.exe");

        var path = JavaRuntimeManager.ResolveEffectiveJavaPath(settings, managedStore: null);

        Assert.Equal(@"C:\java\bin\java.exe", path);
    }

    [Fact]
    public void ResolveEffectiveJavaPath_ReturnsNullForSystemJava()
    {
        var settings = MakeJavaSettings();

        var path = JavaRuntimeManager.ResolveEffectiveJavaPath(settings, managedStore: null);

        Assert.Null(path);
    }

    // ── ServerJavaSettings — serialisation round-trip ───────────────────────

    [Fact]
    public void ServerJavaSettings_ManagedRuntimeId_RoundTrips()
    {
        var original = MakeJavaSettings(managedRuntimeId: "Temurin-21-x64");
        var json = original.ToJsonObject().ToJsonString();

        using var doc = System.Text.Json.JsonDocument.Parse(
            $$"""{"java":{{json}}}""");
        var restored = ServerJavaSettings.FromRootElement(doc.RootElement);

        Assert.Equal("Temurin-21-x64", restored.ManagedRuntimeId);
    }

    [Fact]
    public void ServerJavaSettings_OmitsManagedRuntimeIdWhenEmpty()
    {
        var settings = MakeJavaSettings();
        var json = settings.ToJsonObject().ToJsonString();

        Assert.DoesNotContain("managedRuntimeId", json);
    }

    [Fact]
    public void ServerJavaSettings_DefaultPreservesExistingRuntimePath()
    {
        var json = """{"java":{"runtimePath":"C:\\old\\java.exe","initialMemoryMb":1024,"maximumMemoryMb":4096}}""";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var settings = ServerJavaSettings.FromRootElement(doc.RootElement);

        Assert.Equal(@"C:\old\java.exe", settings.RuntimePath);
        Assert.Equal(string.Empty, settings.ManagedRuntimeId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ManagedJavaRelease MakeRelease(
        string vendor = "Temurin",
        int major = 21,
        string arch = "x64",
        string sha256 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        long maxBytes = 10 * 1024 * 1024)
    {
        return new ManagedJavaRelease(
            Vendor: vendor,
            MajorVersion: major,
            Architecture: arch,
            ReleaseVersion: "21.0.0+0",
            DownloadUrl: "https://example.test/jre.zip",
            ArchiveSha256: sha256,
            MaxArchiveSizeBytes: maxBytes,
            LicenseUrl: "https://example.test/license",
            SourceUrl: "https://example.test/source");
    }

    private static ServerJavaSettings MakeJavaSettings(
        string runtimePath = "",
        string managedRuntimeId = "")
    {
        return new ServerJavaSettings(
            RuntimePath: runtimePath,
            ManagedRuntimeId: managedRuntimeId,
            MemoryPreset: "4 GB",
            InitialMemoryMb: 1024,
            MaximumMemoryMb: 4096,
            AdditionalJvmArguments: string.Empty);
    }

    private static byte[] CreateMinimalJreZip()
    {
        using var memStream = new MemoryStream();
        using (var archive = new ZipArchive(memStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("jdk-21/bin/java.exe");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("fake java executable");
        }

        return memStream.ToArray();
    }

    private static string CreateReleaseApiJson(int major, string releaseName)
    {
        return $$"""
            [{
              "release_name": "{{releaseName}}",
              "release_link": "https://example.test/releases/{{major}}",
              "binary": {
                "package": {
                  "link": "https://example.test/java{{major}}.zip",
                  "checksum": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                  "size": 1024
                }
              }
            }]
            """;
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "WindowsGSH.Tests.Java", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeMultiResponseHttpHandler : HttpMessageHandler
    {
        private readonly Queue<byte[]> _responses;

        public FakeMultiResponseHttpHandler(Queue<byte[]> responses)
        {
            _responses = responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = _responses.Count > 0
                ? _responses.Dequeue()
                : System.Text.Encoding.UTF8.GetBytes("[]");

            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly byte[] _content;
        private readonly int _cancelAfterBytes;

        public FakeHttpHandler(byte[] content, int cancelAfterBytes = int.MaxValue)
        {
            _content = content;
            _cancelAfterBytes = cancelAfterBytes;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var streamContent = new CancellableStreamContent(
                new MemoryStream(_content), _cancelAfterBytes, cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = streamContent
            };
            return Task.FromResult(response);
        }
    }

    private sealed class CancellableStreamContent : HttpContent
    {
        private readonly Stream _stream;
        private readonly int _cancelAfterBytes;
        private readonly CancellationToken _token;

        public CancellableStreamContent(Stream stream, int cancelAfterBytes, CancellationToken token)
        {
            _stream = stream;
            _cancelAfterBytes = cancelAfterBytes;
            _token = token;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[1024];
            int totalWritten = 0;
            int read;
            while ((read = await _stream.ReadAsync(buffer, _token)) > 0)
            {
                _token.ThrowIfCancellationRequested();
                if (totalWritten + read > _cancelAfterBytes)
                {
                    throw new OperationCanceledException("FakeHttpHandler: cancel after bytes limit reached.", _token);
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), _token);
                totalWritten += read;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _stream.Length;
            return true;
        }
    }
}
