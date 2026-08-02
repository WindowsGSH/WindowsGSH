using System.Security.Cryptography;
using WindowsGSH.Core.Web;
using WindowsGSH.Core.Web.Auth;
using WindowsGSH.Data;
using Xunit;

namespace WindowsGSH.Tests;

public class AuthenticatedWebHostFixture : IAsyncLifetime
{
    private static int _nextPort = 16000;
    private readonly bool _allowLegacyWebSocketQueryStringAuth;
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"authenticated-web-fixture-{Guid.NewGuid():N}.db");
    private WebHostService? _host;

    public AuthenticatedWebHostFixture()
        : this(allowLegacyWebSocketQueryStringAuth: false)
    {
    }

    protected AuthenticatedWebHostFixture(bool allowLegacyWebSocketQueryStringAuth)
    {
        _allowLegacyWebSocketQueryStringAuth = allowLegacyWebSocketQueryStringAuth;
        Port = Interlocked.Increment(ref _nextPort);
        Key = RandomNumberGenerator.GetBytes(32);
        AppDatabase.Initialize(_dbPath);
        Repository = new WebUserRepository(_dbPath);
    }

    public int Port { get; }
    public byte[] Key { get; }
    public WebUserRepository Repository { get; }
    public string DatabasePath => _dbPath;

    public async Task InitializeAsync()
    {
        var (configureServices, configurePipeline, _) = WebHostSetup.CreateAuth(Repository, Key);
        _host = new WebHostService();
        Assert.True(
            await _host.TryStartAsync(
                new WebHostOptions(
                    Port,
                    AllowLegacyWebSocketQueryStringAuth: _allowLegacyWebSocketQueryStringAuth),
                configureServices,
                configurePipeline),
            _host.LastStartErrorForTests());
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_host != null)
            {
                await _host.StopWithTimeoutForTestsAsync();
            }
        }
        finally
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }
}

public sealed class LegacyAuthenticatedWebHostFixture()
    : AuthenticatedWebHostFixture(allowLegacyWebSocketQueryStringAuth: true);
