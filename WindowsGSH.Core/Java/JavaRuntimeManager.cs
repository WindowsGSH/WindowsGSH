using WindowsGSH.Core.Servers;

namespace WindowsGSH.Core.Java;

public sealed class JavaRuntimeManager
{
    private readonly JavaRuntimeLocator _locator;

    public JavaRuntimeManager(JavaRuntimeLocator? locator = null)
    {
        _locator = locator ?? new JavaRuntimeLocator();
    }

    public IReadOnlyList<JavaRuntimeInfo> Discover(IEnumerable<string>? knownRuntimePaths = null)
    {
        return _locator.Discover(knownRuntimePaths);
    }

    public Task<IReadOnlyList<JavaRuntimeInfo>> DiscoverAsync(
        IEnumerable<string>? knownRuntimePaths = null,
        CancellationToken cancellationToken = default)
    {
        var paths = knownRuntimePaths?.ToArray();
        return Task.Run(() => Discover(paths), cancellationToken);
    }

    public JavaRuntimeValidationResult Validate(string? configuredPath, int minimumMajor)
    {
        var runtime = _locator.Locate(configuredPath);
        if (!runtime.Found)
        {
            return new JavaRuntimeValidationResult(false, runtime, $"Java {minimumMajor}+ is required. {runtime.ErrorMessage}");
        }

        if (!runtime.MajorVersion.HasValue)
        {
            return new JavaRuntimeValidationResult(false, runtime, "Java was found, but its major version could not be parsed.");
        }

        return runtime.MajorVersion.Value < minimumMajor
            ? new JavaRuntimeValidationResult(false, runtime, $"Java {runtime.MajorVersion.Value} was found, but Java {minimumMajor}+ is required.")
            : new JavaRuntimeValidationResult(true, runtime, $"Java {runtime.MajorVersion.Value} is ready.");
    }

    public JavaRuntimeValidationResult Validate(
        ServerJavaSettings settings,
        int minimumMajor,
        ManagedJavaStore? managedStore = null)
    {
        var effectivePath = ResolveEffectiveJavaPath(settings, managedStore);

        if (!string.IsNullOrWhiteSpace(settings.ManagedRuntimeId) && managedStore != null)
        {
            if (managedStore.TryGetJavaExecutablePath(settings.ManagedRuntimeId) == null)
            {
                return new JavaRuntimeValidationResult(
                    false,
                    JavaRuntimeInfo.Missing($"Managed runtime '{settings.ManagedRuntimeId}' is selected but not installed."),
                    $"Managed Java runtime '{settings.ManagedRuntimeId}' is not installed. Install it in App Settings → Java Runtimes.");
            }
        }

        return Validate(effectivePath, minimumMajor);
    }

    public static string? ResolveEffectiveJavaPath(
        ServerJavaSettings settings,
        ManagedJavaStore? managedStore)
    {
        if (!string.IsNullOrWhiteSpace(settings.ManagedRuntimeId) && managedStore != null)
        {
            return managedStore.TryGetJavaExecutablePath(settings.ManagedRuntimeId);
        }

        return string.IsNullOrWhiteSpace(settings.RuntimePath) ? null : settings.RuntimePath;
    }

    public static string BuildJvmCommandLine(ServerJavaSettings settings)
    {
        ValidateMemory(settings);
        var memoryArguments = $"-Xms{settings.InitialMemoryMb}M -Xmx{settings.MaximumMemoryMb}M";
        return string.IsNullOrWhiteSpace(settings.AdditionalJvmArguments)
            ? memoryArguments
            : $"{memoryArguments} {settings.AdditionalJvmArguments.Trim()}";
    }

    public static void ValidateMemory(ServerJavaSettings settings)
    {
        if (settings.InitialMemoryMb < 256)
        {
            throw new InvalidOperationException("Initial Java memory must be at least 256 MB.");
        }

        if (settings.MaximumMemoryMb < settings.InitialMemoryMb)
        {
            throw new InvalidOperationException("Maximum Java memory must be greater than or equal to initial Java memory.");
        }
    }
}

public sealed record JavaRuntimeValidationResult(
    bool IsValid,
    JavaRuntimeInfo Runtime,
    string Message);
