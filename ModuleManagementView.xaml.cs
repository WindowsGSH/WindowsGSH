using System.IO;
using System.IO.Compression;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Navigation;
using WindowsGSH.Core;
using WindowsGSH.Core.Modules;
using WpfButton = System.Windows.Controls.Button;
using WpfMessageBox = System.Windows.MessageBox;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace WindowsGSH;

public partial class ModuleManagementView : WpfUserControl
{
    private const long MaxSearchImageBytes = 512L * 1024;

    private readonly ModuleImportService _moduleImportService = new();
    private readonly ModuleDevelopmentDiagnosticsService _diagnosticsService = new();
    private readonly ModuleRepositorySearchService _repositorySearchService = new();
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };
    private CancellationTokenSource? _moduleSearchCancellation;
    private int _moduleSearchGeneration;
    private bool _isImportingSearchResult;

    public ModuleManagementView()
    {
        InitializeComponent();
    }

    public event EventHandler? ModulesChanged;

    public void RefreshModules()
    {
        try
        {
            var installed = _moduleImportService.GetInstalledModules();
            var disabled = _moduleImportService.GetDisabledModules();
            InstalledModulesItemsControl.ItemsSource = installed;
            DisabledModulesItemsControl.ItemsSource = disabled;
            NoInstalledModulesTextBlock.Visibility = installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoDisabledModulesTextBlock.Visibility = disabled.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            InstalledModulesItemsControl.ItemsSource = Array.Empty<ModuleImportCandidate>();
            DisabledModulesItemsControl.ItemsSource = Array.Empty<ModuleImportCandidate>();
            NoInstalledModulesTextBlock.Visibility = Visibility.Visible;
            NoDisabledModulesTextBlock.Visibility = Visibility.Visible;
            ShowDiagnosticsError("Module refresh failed", ex);
        }
    }

    private async void ValidateModuleFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a WindowsGSH module folder containing module.json",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        try
        {
            ValidateModuleFolderButton.IsEnabled = false;
            ShowDiagnosticsProgress("Validating Module", $"Checking {dialog.SelectedPath}...");
            var selectedPath = dialog.SelectedPath;
            var report = await Task.Run(() => _diagnosticsService.ValidateFolder(selectedPath));
            ShowDiagnostics(report);
            AppLogService.Add(report.Success
                ? $"Validated module {report.Name} ({report.Id})."
                : $"Module validation failed for {selectedPath}.");
        }
        catch (Exception ex)
        {
            ShowDiagnosticsError("Module validation failed", ex);
        }
        finally
        {
            ValidateModuleFolderButton.IsEnabled = true;
        }
    }

    private async void ReloadModulesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReloadModulesButton.IsEnabled = false;
            ShowDiagnosticsProgress("Reloading Modules", "Refreshing module lists and rebuilding module descriptors...");
            OnModulesChanged();
            var descriptors = await Task.Run(() => new ModuleRegistry().GetModuleDescriptors());
            ShowReloadDiagnostics(descriptors);
            AppLogService.Add("Reloaded modules.");
        }
        catch (Exception ex)
        {
            ShowDiagnosticsError("Module reload failed", ex);
        }
        finally
        {
            ReloadModulesButton.IsEnabled = true;
        }
    }

    private void ImportModuleZipButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import WindowsGSH Module ZIP",
            Filter = "ZIP files (*.zip)|*.zip|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        if (!ConfirmModuleImport() ||
            (ZipContainsCSharpFile(dialog.FileName) &&
             !ConfirmCSharpModuleImport($"local file: {dialog.FileName}", TryComputeFileSha256(dialog.FileName))))
        {
            return;
        }

        try
        {
            var module = _moduleImportService.ImportFromZip(dialog.FileName);
            AppLogService.Add($"Imported module {module.Name} ({module.Id}).");
            OnModulesChanged();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(Window.GetWindow(this), ex.Message, "Module Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ModuleSearchButton_Click(object sender, RoutedEventArgs e)
    {
        await SearchModuleRepositoriesAsync();
    }

    private async void ModuleSearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            await SearchModuleRepositoriesAsync();
        }
    }

    private async Task SearchModuleRepositoriesAsync()
    {
        if (ModuleSearchButton.IsEnabled == false)
        {
            return;
        }

        var searchTerm = ModuleSearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            ModuleSearchStatusTextBlock.Text = "Enter a game name, for example TF2.";
            return;
        }

        _moduleSearchCancellation?.Cancel();
        _moduleSearchCancellation = new CancellationTokenSource();
        var cancellationToken = _moduleSearchCancellation.Token;
        var searchGeneration = ++_moduleSearchGeneration;

        try
        {
            ModuleSearchButton.IsEnabled = false;
            ModuleSearchTextBox.IsEnabled = false;
            ModuleSearchResultsItemsControl.ItemsSource = Array.Empty<ModuleSearchResultCard>();
            ModuleSearchStatusTextBlock.Text = $"Searching for WindowsGSH.{searchTerm} repositories...";

            var results = await _repositorySearchService.SearchAsync(searchTerm, cancellationToken);
            if (searchGeneration != _moduleSearchGeneration || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var cards = CreateSearchResultCards(results);
            ModuleSearchResultsItemsControl.ItemsSource = cards;
            ModuleSearchStatusTextBlock.Text = results.Count == 0
                ? $"No WindowsGSH.{searchTerm} repositories found."
                : $"Found {results.Count} WindowsGSH.{searchTerm} module repository result(s).";
            _ = LoadSearchImagesAsync(cards, searchGeneration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (searchGeneration == _moduleSearchGeneration)
            {
                ModuleSearchStatusTextBlock.Text = "Module search failed: " + ex.Message;
            }
        }
        finally
        {
            if (searchGeneration == _moduleSearchGeneration)
            {
                ModuleSearchButton.IsEnabled = true;
                ModuleSearchTextBox.IsEnabled = true;
            }
        }
    }

    private async void ImportSearchResultButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: ModuleSearchResultCard card } importButton)
        {
            return;
        }

        if (_isImportingSearchResult)
        {
            return;
        }

        var result = card.Result;
        if (!ConfirmModuleImport())
        {
            return;
        }

        try
        {
            _isImportingSearchResult = true;
            importButton.IsEnabled = false;
            ModuleSearchButton.IsEnabled = false;
            ModuleSearchTextBox.IsEnabled = false;
            ModuleSearchResultsItemsControl.IsEnabled = false;
            ModuleSearchStatusTextBlock.Text = $"Downloading {result.DisplayName}...";
            var zipPath = await DownloadRepositoryZipAsync(result);
            ModuleImportCandidate module;
            try
            {
                if (ZipContainsCSharpFile(zipPath) &&
                    !ConfirmCSharpModuleImport($"{result.DisplayName} ({result.Url})", TryComputeFileSha256(zipPath)))
                {
                    ModuleSearchStatusTextBlock.Text = $"Import cancelled for {result.DisplayName}.";
                    return;
                }

                module = _moduleImportService.ImportFromZip(zipPath);
            }
            finally
            {
                TryDeleteDirectory(Path.GetDirectoryName(zipPath)!);
            }

            AppLogService.Add($"Imported module {module.Name} ({module.Id}) from repository search result {result.DisplayName}.");
            ModuleSearchStatusTextBlock.Text = $"Imported {module.Name}.";
            OnModulesChanged();
        }
        catch (Exception ex)
        {
            ModuleSearchStatusTextBlock.Text = "Module import failed: " + ex.Message;
            WpfMessageBox.Show(Window.GetWindow(this), ex.Message, "Module Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isImportingSearchResult = false;
            importButton.IsEnabled = true;
            ModuleSearchButton.IsEnabled = true;
            ModuleSearchTextBox.IsEnabled = true;
            ModuleSearchResultsItemsControl.IsEnabled = true;
        }
    }

    private void RepositoryLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Uri?.AbsoluteUri))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private static IReadOnlyList<ModuleSearchResultCard> CreateSearchResultCards(
        IReadOnlyList<ModuleRepositorySearchResult> results)
    {
        return results.Select(result => new ModuleSearchResultCard(result)).ToArray();
    }

    private async Task LoadSearchImagesAsync(
        IReadOnlyList<ModuleSearchResultCard> cards,
        int searchGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            await Parallel.ForEachAsync(
                cards,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = 4
                },
                async (card, token) =>
                {
                    var imagePath = await TryDownloadSearchImageAsync(card.Result, token);
                    if (!string.IsNullOrWhiteSpace(imagePath) &&
                        searchGeneration == _moduleSearchGeneration &&
                        !token.IsCancellationRequested)
                    {
                        await Dispatcher.InvokeAsync(() => card.AuthorImagePath = imagePath);
                    }
                });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<string> TryDownloadSearchImageAsync(
        ModuleRepositorySearchResult result,
        CancellationToken cancellationToken)
    {
        var cacheDirectory = AppPaths.GetPath("module-search-cache", "avatars");
        Directory.CreateDirectory(cacheDirectory);

        foreach (var imageUrl in GetSearchImageCandidates(result))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                AddWindowsGshHeaders(request);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var bytes = await DownloadImageBytesWithLimitAsync(request, MaxSearchImageBytes, timeout.Token);
                if (bytes.Length == 0)
                {
                    continue;
                }

                var safeName = ModuleRepositorySearchService.NormalizeSearchToken(result.FullName);
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    safeName = Guid.NewGuid().ToString("N");
                }

                var fileName = $"{safeName}-{ModuleRepositorySearchService.NormalizeSearchToken(imageUrl)}.png";
                var imagePath = Path.Combine(cacheDirectory, fileName);
                await File.WriteAllBytesAsync(imagePath, bytes, cancellationToken);
                return imagePath;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (HttpRequestException)
            {
            }
            catch (IOException)
            {
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> GetSearchImageCandidates(ModuleRepositorySearchResult result)
    {
        var branch = EscapeUrlPathSegment(string.IsNullOrWhiteSpace(result.DefaultBranch) ? "main" : result.DefaultBranch);
        var moduleFolderName = EscapeUrlPathSegment(GetModuleFolderName(result.Name));
        if (!string.IsNullOrWhiteSpace(result.FullName) && !string.IsNullOrWhiteSpace(moduleFolderName))
        {
            yield return $"https://raw.githubusercontent.com/{result.FullName}/{branch}/{moduleFolderName}.mod/author.png";
        }

        if (!string.IsNullOrWhiteSpace(result.FullName))
        {
            yield return $"https://raw.githubusercontent.com/{result.FullName}/{branch}/author.png";
        }

        if (!string.IsNullOrWhiteSpace(result.OwnerAvatarUrl))
        {
            yield return result.OwnerAvatarUrl;
        }

        if (!string.IsNullOrWhiteSpace(result.Owner))
        {
            yield return $"https://github.com/{result.Owner}.png?size=64";
        }
    }

    private static string GetModuleFolderName(string repositoryName)
    {
        if (repositoryName.StartsWith(ModuleRepositorySearchService.RequiredRepositoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return repositoryName[ModuleRepositorySearchService.RequiredRepositoryPrefix.Length..];
        }

        return string.Empty;
    }

    private static string EscapeUrlPathSegment(string value)
    {
        return Uri.EscapeDataString(value);
    }

    /// <summary>
    /// Converts a GitHub repo page URL to its archive ZIP download URL.
    /// https://github.com/{owner}/{repo}              → …/archive/HEAD.zip (resolves to the repo's actual default branch)
    /// https://github.com/{owner}/{repo}/tree/{branch} → …/archive/refs/heads/{branch}.zip
    /// Any other URL is returned unchanged.
    /// </summary>
    internal static Uri ResolveModuleDownloadUri(Uri uri, string? defaultBranch = null)
    {
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var segments = uri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2)
        {
            return uri;
        }

        var owner = segments[0];
        var repository = TrimGitSuffix(segments[1]);
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
        {
            return uri;
        }

        if (segments.Length == 2)
        {
            return string.IsNullOrWhiteSpace(defaultBranch)
                ? new Uri($"https://github.com/{owner}/{repository}/archive/HEAD.zip")
                : new Uri($"https://github.com/{owner}/{repository}/archive/refs/heads/{EscapeUrlPath(defaultBranch)}.zip");
        }

        if (segments.Length >= 4 &&
            string.Equals(segments[2], "tree", StringComparison.OrdinalIgnoreCase))
        {
            var branch = string.Join('/', segments.Skip(3).Where(segment => !string.IsNullOrWhiteSpace(segment)));
            if (!string.IsNullOrWhiteSpace(branch))
            {
                return new Uri($"https://github.com/{owner}/{repository}/archive/refs/heads/{EscapeUrlPath(branch)}.zip");
            }
        }

        return uri;
    }

    private static async Task<Uri> ResolveModuleDownloadUriAsync(Uri uri)
    {
        if (TryGetPlainGitHubRepository(uri, out var owner, out var repository))
        {
            var defaultBranch = await TryGetGitHubDefaultBranchAsync(owner, repository);
            return ResolveModuleDownloadUri(uri, defaultBranch);
        }

        return ResolveModuleDownloadUri(uri);
    }

    private static bool TryGetPlainGitHubRepository(Uri uri, out string owner, out string repository)
    {
        owner = string.Empty;
        repository = string.Empty;

        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length != 2)
        {
            return false;
        }

        owner = segments[0];
        repository = TrimGitSuffix(segments[1]);
        return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repository);
    }

    private static async Task<string?> TryGetGitHubDefaultBranchAsync(string owner, string repository)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}"));
            AddWindowsGshHeaders(request);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            return document.RootElement.TryGetProperty("default_branch", out var branchElement) &&
                branchElement.ValueKind == JsonValueKind.String
                ? branchElement.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string TrimGitSuffix(string repository)
    {
        return repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repository[..^4]
            : repository;
    }

    private static string EscapeUrlPath(string value)
    {
        return string.Join('/', value.Split('/').Select(EscapeUrlPathSegment));
    }

    private static async Task<string> DownloadRepositoryZipAsync(ModuleRepositorySearchResult result)
    {
        var zipUrl = !string.IsNullOrWhiteSpace(result.ZipUrl)
            ? result.ZipUrl
            : $"https://github.com/{result.FullName}/archive/refs/heads/{(string.IsNullOrWhiteSpace(result.DefaultBranch) ? "main" : result.DefaultBranch)}.zip";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WindowsGSHModuleSearch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var safeName = string.Join("_", (result.Name ?? "module").Split(Path.GetInvalidFileNameChars()));
        var zipPath = Path.Combine(tempRoot, safeName + ".zip");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, zipUrl);
            AddWindowsGshHeaders(request);
            await using var local = File.Create(zipPath);
            await DownloadToStreamWithLimitAsync(request, local, ModuleZipImportValidator.DefaultLimits.MaxZipFileBytes);
            return zipPath;
        }
        catch
        {
            TryDeleteDirectory(tempRoot);
            throw;
        }
    }

    private static async Task<byte[]> DownloadImageBytesWithLimitAsync(
        HttpRequestMessage request,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var response = await SharedHttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(contentType) ||
            !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
        {
            return [];
        }

        await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        await CopyStreamWithLimitAsync(remote, memory, maxBytes, cancellationToken);
        return memory.ToArray();
    }

    private static async Task DownloadToStreamWithLimitAsync(
        HttpRequestMessage request,
        Stream destination,
        long maxBytes)
    {
        using var response = await SharedHttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
        {
            throw new InvalidOperationException($"Module ZIP is too large. Maximum ZIP size is {maxBytes} bytes.");
        }

        await using var remote = await response.Content.ReadAsStreamAsync();
        await CopyStreamWithLimitAsync(remote, destination, maxBytes, CancellationToken.None);
    }

    private static async Task CopyStreamWithLimitAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var totalBytes = 0L;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            totalBytes += read;
            if (totalBytes > maxBytes)
            {
                throw new InvalidOperationException($"Download is too large. Maximum size is {maxBytes} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void AddWindowsGshHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WindowsGSH", "1.0"));
    }

    private void ImportModuleFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a WindowsGSH module folder containing module.json",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var moduleFolder = ModuleImportService.ResolveModuleFolder(dialog.SelectedPath) ?? dialog.SelectedPath;
        if (!ConfirmModuleImport() ||
            (FolderContainsCSharpFile(moduleFolder) &&
             !ConfirmCSharpModuleImport($"local folder: {moduleFolder}", TryComputeFolderSha256(moduleFolder))))
        {
            return;
        }

        try
        {
            var module = _moduleImportService.ImportFromFolder(dialog.SelectedPath);
            AppLogService.Add($"Imported module {module.Name} ({module.Id}).");
            OnModulesChanged();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(Window.GetWindow(this), ex.Message, "Module Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void UpdateFromRepoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: ModuleImportCandidate module } button)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(module.SourceUrl))
        {
            return;
        }

        if (!Uri.TryCreate(module.SourceUrl, UriKind.Absolute, out var sourceUri) ||
            (sourceUri.Scheme != Uri.UriSchemeHttp && sourceUri.Scheme != Uri.UriSchemeHttps))
        {
            WpfMessageBox.Show(
                Window.GetWindow(this),
                "The module's source URL is not a valid HTTP or HTTPS address.",
                "Update Module Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!ConfirmModuleUpdate(module.Name))
        {
            return;
        }

        button.IsEnabled = false;
        ShowDiagnosticsProgress("Updating Module", $"Downloading update for {module.Name}...");

        var moduleId = module.Id;
        var tempRoot = Path.Combine(Path.GetTempPath(), "WindowsGSHModuleUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var safeName = string.Join("_", moduleId.Split(Path.GetInvalidFileNameChars()));
        var zipPath = Path.Combine(tempRoot, safeName + ".zip");

        try
        {
            var downloadUri = await ResolveModuleDownloadUriAsync(sourceUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            AddWindowsGshHeaders(request);
            await using (var local = File.Create(zipPath))
            {
                await DownloadToStreamWithLimitAsync(request, local, ModuleZipImportValidator.DefaultLimits.MaxZipFileBytes);
            }

            if (ZipContainsCSharpFile(zipPath) &&
                !ConfirmCSharpModuleImport($"{module.Name} ({module.SourceUrl})", TryComputeFileSha256(zipPath)))
            {
                DiagnosticsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var updated = await Task.Run(() => _moduleImportService.UpdateFromZip(moduleId, zipPath, module.SourceUrl));
            AppLogService.Add($"Updated module {updated.Name} ({updated.Id}) from {module.SourceUrl}.");
            DiagnosticsTitleTextBlock.Text = "Module Updated";
            DiagnosticsSummaryTextBlock.Text = $"{updated.Name} has been updated and re-trusted.";
            DiagnosticsItemsControl.ItemsSource = new[]
            {
                ModuleDevelopmentDiagnostic.Info("module.update.success", updated.Path, $"New hash: {updated.ShortHash}")
            };
            OnModulesChanged();
        }
        catch (Exception ex)
        {
            ShowDiagnosticsError("Module update failed", ex);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
            button.IsEnabled = true;
        }
    }

    private async void UpdateFromFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: ModuleImportCandidate module } button)
        {
            return;
        }

        if (!ConfirmModuleUpdate(module.Name))
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Select Updated Module ZIP for {module.Name}",
            Filter = "ZIP files (*.zip)|*.zip|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        if (ZipContainsCSharpFile(dialog.FileName) &&
            !ConfirmCSharpModuleImport($"{module.Name} - local file: {dialog.FileName}", TryComputeFileSha256(dialog.FileName)))
        {
            return;
        }

        button.IsEnabled = false;
        ShowDiagnosticsProgress("Updating Module", $"Applying update for {module.Name}...");

        try
        {
            var updated = await Task.Run(() => _moduleImportService.UpdateFromZip(module.Id, dialog.FileName));
            AppLogService.Add($"Updated module {updated.Name} ({updated.Id}) from file.");
            DiagnosticsTitleTextBlock.Text = "Module Updated";
            DiagnosticsSummaryTextBlock.Text = $"{updated.Name} has been updated and re-trusted.";
            DiagnosticsItemsControl.ItemsSource = new[]
            {
                ModuleDevelopmentDiagnostic.Info("module.update.success", updated.Path, $"New hash: {updated.ShortHash}")
            };
            OnModulesChanged();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(Window.GetWindow(this), ex.Message, "Module Update Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private bool ConfirmModuleUpdate(string moduleName)
    {
        var result = WpfMessageBox.Show(
            Window.GetWindow(this),
            $"You are about to update {moduleName}. Check the module's changelog before proceeding.\n\nIf this update changes required server settings, you may need to reconfigure affected servers after updating.",
            "Update Module",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.OK;
    }

    private void DisableModuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: ModuleImportCandidate module })
        {
            return;
        }

        try
        {
            var serversUsingModule = GetServersUsingModule(module.Id);
            if (serversUsingModule.Count > 0)
            {
                WpfMessageBox.Show(
                    Window.GetWindow(this),
                    $"Cannot disable {module.Name} while servers are using it. Delete these servers first:{Environment.NewLine}{FormatServerUsage(serversUsingModule)}",
                    "Disable Module Blocked",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _moduleImportService.Disable(module.Id);
            AppLogService.Add($"Disabled module {module.Name} ({module.Id}).");
            OnModulesChanged();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(Window.GetWindow(this), ex.Message, "Disable Module Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void EnableModuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: ModuleImportCandidate module })
        {
            return;
        }

        try
        {
            _moduleImportService.Enable(module.Id);
            AppLogService.Add($"Enabled module {module.Name} ({module.Id}).");
            OnModulesChanged();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(Window.GetWindow(this), ex.Message, "Enable Module Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteDisabledModuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: ModuleImportCandidate module })
        {
            return;
        }

        var result = WpfMessageBox.Show(
            Window.GetWindow(this),
            $"Delete disabled module {module.Name} ({module.Id})? This removes the imported module files from the disabled modules folder.",
            "Delete Disabled Module",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            _moduleImportService.DeleteDisabled(module.Id);
            AppLogService.Add($"Deleted disabled module {module.Name} ({module.Id}).");
            RefreshModules();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(Window.GetWindow(this), ex.Message, "Delete Module Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnModulesChanged()
    {
        ModuleRegistry.InvalidateCache();
        RefreshModules();
        ModulesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowDiagnostics(ModuleDevelopmentDiagnosticReport report)
    {
        DiagnosticsPanel.Visibility = Visibility.Visible;
        DiagnosticsTitleTextBlock.Text = report.Success ? "Module Validation Passed" : "Module Validation Failed";
        DiagnosticsSummaryTextBlock.Text = report.Success
            ? $"{report.Name} ({report.Id}) v{report.Version} - {report.ModuleType}. {FormatCapabilities(report.Capabilities)}"
            : $"{report.Name}: review the diagnostics below.";
        DiagnosticsItemsControl.ItemsSource = report.Diagnostics.Count == 0
            ? new[] { ModuleDevelopmentDiagnostic.Info("module.noWarnings", report.ModulePath, "No warnings were reported.") }
            : report.Diagnostics;
    }

    private void ShowReloadDiagnostics(IReadOnlyList<ModuleDescriptor> descriptors)
    {
        DiagnosticsPanel.Visibility = Visibility.Visible;
        DiagnosticsTitleTextBlock.Text = "Modules Reloaded";
        DiagnosticsSummaryTextBlock.Text = $"Installed and disabled module lists were refreshed. Loaded {descriptors.Count} enabled module(s).";
        DiagnosticsItemsControl.ItemsSource = descriptors
            .SelectMany(CreateReloadDiagnostics)
            .ToArray();
    }

    private void ShowDiagnosticsProgress(string title, string summary)
    {
        DiagnosticsPanel.Visibility = Visibility.Visible;
        DiagnosticsTitleTextBlock.Text = title;
        DiagnosticsSummaryTextBlock.Text = summary;
        DiagnosticsItemsControl.ItemsSource = new[]
        {
            new ModuleDevelopmentDiagnostic(
                ModuleValidationSeverity.Info,
                "module.operation.running",
                string.Empty,
                "Working...")
        };
    }

    private static IEnumerable<ModuleDevelopmentDiagnostic> CreateReloadDiagnostics(ModuleDescriptor descriptor)
    {
        yield return ModuleDevelopmentDiagnostic.Info(
            "module.loaded",
            descriptor.ModulePath,
            $"{descriptor.Name} ({descriptor.Id}) v{descriptor.Version} - {descriptor.ModuleType}. {FormatCapabilities(descriptor.EffectiveCapabilities)}");

        if (!string.IsNullOrWhiteSpace(descriptor.Provenance.Warning))
        {
            yield return new ModuleDevelopmentDiagnostic(
                ModuleValidationSeverity.Warning,
                "module.provenance.warning",
                descriptor.ModulePath,
                descriptor.Provenance.Warning);
        }

        foreach (var warning in descriptor.ValidationWarnings)
        {
            yield return new ModuleDevelopmentDiagnostic(
                warning.Severity,
                warning.Code,
                warning.Path,
                warning.Message);
        }
    }

    private void ShowDiagnosticsError(string title, Exception exception)
    {
        AppLogService.Add($"{title}: {exception.Message}");
        DiagnosticsPanel.Visibility = Visibility.Visible;
        DiagnosticsTitleTextBlock.Text = title;
        DiagnosticsSummaryTextBlock.Text = "WindowsGSH could not complete the module operation. Review the diagnostic below.";
        DiagnosticsItemsControl.ItemsSource = new[]
        {
            new ModuleDevelopmentDiagnostic(
                ModuleValidationSeverity.Error,
                "module.operation.failed",
                string.Empty,
                exception.Message)
        };
        WpfMessageBox.Show(Window.GetWindow(this), exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string FormatCapabilities(ModuleCapabilities? capabilities)
    {
        if (capabilities == null)
        {
            return "Capabilities unavailable.";
        }

        var enabled = new[]
        {
            capabilities.SupportsInstall ? "install" : null,
            capabilities.SupportsUpdate ? "update" : null,
            capabilities.SupportsQuery ? "query" : null,
            capabilities.SupportsRcon ? "rcon" : null,
            capabilities.SupportsConsoleCommands ? "console" : null,
            capabilities.SupportsApiActions ? "api" : null,
            capabilities.SupportsBackups ? "backups" : null,
            capabilities.SupportsDirectConnection ? "direct connection" : null,
            capabilities.RequiresJava ? "java" : null
        }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();

        return enabled.Length == 0
            ? "No optional capabilities detected."
            : "Capabilities: " + string.Join(", ", enabled) + ".";
    }

    private static IReadOnlyList<ModuleServerUsage> GetServersUsingModule(string moduleId)
    {
        var serversPath = AppPaths.GetPath("servers");
        if (!Directory.Exists(serversPath))
        {
            return [];
        }

        var servers = new List<ModuleServerUsage>();
        foreach (var serverFolder in Directory.EnumerateDirectories(serversPath))
        {
            var configPath = Path.Combine(serverFolder, "ServerConfig.json");
            if (!File.Exists(configPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                var root = document.RootElement;
                if (!root.TryGetProperty("moduleId", out var moduleProperty) ||
                    moduleProperty.ValueKind != JsonValueKind.String ||
                    !string.Equals(moduleProperty.GetString(), moduleId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = root.TryGetProperty("name", out var nameProperty) && nameProperty.ValueKind == JsonValueKind.String
                    ? nameProperty.GetString() ?? Path.GetFileName(serverFolder)
                    : Path.GetFileName(serverFolder);
                servers.Add(new ModuleServerUsage(name ?? serverFolder, Path.GetFileName(serverFolder) ?? serverFolder));
            }
            catch
            {
            }
        }

        return servers;
    }

    private static string FormatServerUsage(IReadOnlyList<ModuleServerUsage> servers)
    {
        var serverNames = servers
            .Take(6)
            .Select(server => $"- {server.Name}");
        var text = string.Join(Environment.NewLine, serverNames);
        return servers.Count > 6
            ? text + Environment.NewLine + $"- and {servers.Count - 6} more"
            : text;
    }

    private bool ConfirmModuleImport()
    {
        var result = WpfMessageBox.Show(
            Window.GetWindow(this),
            "Modules are third-party/user-provided files. WindowsGSH does not create, own, review, sign, or guarantee imported modules. If you download and run a module, responsibility for that module is yours.",
            "Import Module",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.OK;
    }

    private bool ConfirmCSharpModuleImport(string source, string? sha256Hash)
    {
        var hashLine = string.IsNullOrWhiteSpace(sha256Hash) ? string.Empty : $"\nSHA-256: {sha256Hash}";
        var result = WpfMessageBox.Show(
            Window.GetWindow(this),
            "C# modules compile and run arbitrary code as your Windows user account when WindowsGSH loads them. " +
            "WindowsGSH does not sandbox, review, or approve C# modules - it only records provenance and a hash " +
            $"after import.\n\nSource: {source}{hashLine}\n\n" +
            "Only continue if you trust where this module came from.",
            "Import C# Module",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.OK;
    }

    private static string? TryComputeFileSha256(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryComputeFolderSha256(string folderPath)
    {
        try
        {
            return ModuleProvenanceService.ComputeModuleHash(folderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool FolderContainsCSharpFile(string folderPath)
    {
        return Directory.Exists(folderPath) &&
            Directory.EnumerateFiles(folderPath, "*.cs", SearchOption.AllDirectories).Any();
    }

    private static bool ZipContainsCSharpFile(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.Entries.Any(entry => entry.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class ModuleSearchResultCard(ModuleRepositorySearchResult result) : INotifyPropertyChanged
    {
        private string _authorImagePath = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ModuleRepositorySearchResult Result { get; } = result;

        public string AuthorImagePath
        {
            get => _authorImagePath;
            set
            {
                if (string.Equals(_authorImagePath, value, StringComparison.Ordinal))
                {
                    return;
                }

                _authorImagePath = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AuthorImagePath)));
            }
        }

        public string DisplayName => Result.DisplayName;

        public string StatsText => Result.StatsText;

        public string DisplayDescription => Result.DisplayDescription;

        public string DisplayDetails => Result.DisplayDetails;

        public string DisplayMetadata => Result.DisplayMetadata;

        public string Url => Result.Url;
    }
}
