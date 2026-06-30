using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Verbeam.Core.Models;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

[Collection(NonParallelTestCollection.Name)]
public sealed class MemoryOidcApiTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "verbeam-oidc-tests-" + Guid.NewGuid());
    private readonly string _databasePath;
    private readonly string? _previousCachePath;
    private readonly string? _previousDefaultProvider;
    private readonly string? _previousBearerJwtEnabled;
    private readonly string? _previousBearerJwtIssuer;
    private readonly string? _previousBearerJwtAudience0;
    private readonly string? _previousBearerJwtHmacSecret;
    private readonly string? _previousUrls;
    private readonly WebApplicationFactory<Program> _factory;

    public MemoryOidcApiTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _databasePath = Path.Combine(_tempDirectory, "translations.sqlite");
        _previousCachePath = Environment.GetEnvironmentVariable("VB_Verbeam__CachePath");
        _previousDefaultProvider = Environment.GetEnvironmentVariable("VB_Verbeam__DefaultProvider");
        _previousBearerJwtEnabled = Environment.GetEnvironmentVariable("VB_Verbeam__Memory__BearerJwt__Enabled");
        _previousBearerJwtIssuer = Environment.GetEnvironmentVariable("VB_Verbeam__Memory__BearerJwt__Issuer");
        _previousBearerJwtAudience0 = Environment.GetEnvironmentVariable("VB_Verbeam__Memory__BearerJwt__Audiences__0");
        _previousBearerJwtHmacSecret = Environment.GetEnvironmentVariable("VB_Verbeam__Memory__BearerJwt__HmacSecret");
        _previousUrls = Environment.GetEnvironmentVariable("VB_Urls");

        Environment.SetEnvironmentVariable("VB_Verbeam__CachePath", _databasePath);
        Environment.SetEnvironmentVariable("VB_Verbeam__DefaultProvider", "mock");
        Environment.SetEnvironmentVariable("VB_Verbeam__Memory__BearerJwt__Enabled", "true");
        Environment.SetEnvironmentVariable("VB_Verbeam__Memory__BearerJwt__Issuer", "https://issuer.example");
        Environment.SetEnvironmentVariable("VB_Verbeam__Memory__BearerJwt__Audiences__0", "verbeam-memory");
        Environment.SetEnvironmentVariable("VB_Verbeam__Memory__BearerJwt__HmacSecret", "oidc-secret");
        Environment.SetEnvironmentVariable("VB_Urls", "http://localhost:0");

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Verbeam:CachePath"] = _databasePath,
                        ["Verbeam:DefaultProvider"] = "mock",
                        ["Verbeam:Memory:BearerJwt:Enabled"] = "true",
                        ["Verbeam:Memory:BearerJwt:Issuer"] = "https://issuer.example",
                        ["Verbeam:Memory:BearerJwt:Audiences:0"] = "verbeam-memory",
                        ["Verbeam:Memory:BearerJwt:HmacSecret"] = "oidc-secret"
                    });
                });
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IMemoryOidcClient>(
                        new FakeMemoryOidcClient(CreateHs256Jwt("alice", ["reviewers"])));
                });
            });
    }

    [Fact]
    public async Task MemoryOidcCallback_CreatesSessionForValidatedToken()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var loginResponse = await client.GetAsync("/memory/oidc/login?redirect=true");
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        Assert.Equal("https://issuer.example/authorize?state=fake-state", loginResponse.Headers.Location?.ToString());

        var callbackResponse = await client.GetAsync("/memory/oidc/callback?code=good-code&state=fake-state");
        callbackResponse.EnsureSuccessStatusCode();
        var sessionResult = await callbackResponse.Content.ReadFromJsonAsync<MemoryOidcSessionResult>(jsonOptions);
        Assert.NotNull(sessionResult);
        Assert.Equal("alice", sessionResult.Principal);
        Assert.Contains("reviewers", sessionResult.Groups);
        Assert.Equal("refresh-token", sessionResult.RefreshToken);
        Assert.False(string.IsNullOrWhiteSpace(sessionResult.SessionToken));

        var sessions = await client.GetFromJsonAsync<MemoryPrincipalSession[]>("/memory/principal-sessions?principal=alice", jsonOptions);
        Assert.NotNull(sessions);
        Assert.Contains(sessions, item => item.Id == sessionResult.Session.Id);

        var refreshResponse = await client.PostAsJsonAsync("/memory/oidc/refresh", new
        {
            refreshToken = "refresh-token"
        });
        refreshResponse.EnsureSuccessStatusCode();
        var refreshed = await refreshResponse.Content.ReadFromJsonAsync<MemoryOidcSessionResult>(jsonOptions);
        Assert.NotNull(refreshed);
        Assert.Equal("alice", refreshed.Principal);
        Assert.False(string.IsNullOrWhiteSpace(refreshed.SessionToken));

        var health = await client.GetFromJsonAsync<JsonElement>("/health", jsonOptions);
        Assert.Equal(
            "client_only",
            health.GetProperty("memory").GetProperty("oidc").GetProperty("refreshTokenStorage").GetString());
        AssertDatabaseFilesDoNotContain(_databasePath, "refresh-token");
    }

    [Fact]
    public async Task MemoryOidcEncryptedRefreshTokenVault_ReturnsHandleAndRevokesOnDeprovision()
    {
        var previousStorage = Environment.GetEnvironmentVariable("VB_Verbeam__Memory__Oidc__RefreshTokenStorage");
        var previousProtectionKey = Environment.GetEnvironmentVariable("VB_Verbeam__Memory__Oidc__RefreshTokenProtectionKey");
        Environment.SetEnvironmentVariable("VB_Verbeam__Memory__Oidc__RefreshTokenStorage", "encrypted_db");
        Environment.SetEnvironmentVariable("VB_Verbeam__Memory__Oidc__RefreshTokenProtectionKey", "test-protection-key-that-is-long-enough");
        var databasePath = Path.Combine(_tempDirectory, "encrypted-oidc-vault.sqlite");
        try
        {
            using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Verbeam:CachePath"] = databasePath
                    });
                });
            });
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

            var callbackResponse = await client.GetAsync("/memory/oidc/callback?code=good-code&state=fake-state");
            callbackResponse.EnsureSuccessStatusCode();
            var sessionResult = await callbackResponse.Content.ReadFromJsonAsync<MemoryOidcSessionResult>(jsonOptions);
            Assert.NotNull(sessionResult);
            Assert.Equal("alice", sessionResult.Principal);
            Assert.Equal(string.Empty, sessionResult.RefreshToken);
            Assert.False(string.IsNullOrWhiteSpace(sessionResult.RefreshTokenHandle));

            var health = await client.GetFromJsonAsync<JsonElement>("/health", jsonOptions);
            Assert.Equal(
                "encrypted_db",
                health.GetProperty("memory").GetProperty("oidc").GetProperty("refreshTokenStorage").GetString());
            AssertDatabaseFilesDoNotContain(databasePath, "refresh-token");

            var listResponse = await client.GetAsync("/memory/oidc/refresh-tokens?principal=alice");
            listResponse.EnsureSuccessStatusCode();
            var handles = await listResponse.Content.ReadFromJsonAsync<MemoryOidcRefreshTokenHandle[]>(jsonOptions);
            Assert.NotNull(handles);
            var listedHandle = Assert.Single(handles);
            Assert.Equal(sessionResult.RefreshTokenHandle, listedHandle.Id);
            Assert.Equal("alice", listedHandle.PrincipalId);
            Assert.Null(listedHandle.RevokedAt);

            var refreshResponse = await client.PostAsJsonAsync("/memory/oidc/refresh", new
            {
                refreshTokenHandle = sessionResult.RefreshTokenHandle
            });
            refreshResponse.EnsureSuccessStatusCode();
            var refreshed = await refreshResponse.Content.ReadFromJsonAsync<MemoryOidcSessionResult>(jsonOptions);
            Assert.NotNull(refreshed);
            Assert.Equal("alice", refreshed.Principal);
            Assert.Equal(string.Empty, refreshed.RefreshToken);
            Assert.Equal(sessionResult.RefreshTokenHandle, refreshed.RefreshTokenHandle);
            AssertDatabaseFilesDoNotContain(databasePath, "refresh-token");

            var usedListResponse = await client.GetAsync("/memory/oidc/refresh-tokens?principal=alice");
            usedListResponse.EnsureSuccessStatusCode();
            var usedHandles = await usedListResponse.Content.ReadFromJsonAsync<MemoryOidcRefreshTokenHandle[]>(jsonOptions);
            Assert.NotNull(usedHandles);
            Assert.NotNull(Assert.Single(usedHandles).LastUsedAt);

            var revokeResponse = await client.DeleteAsync($"/memory/oidc/refresh-tokens/{sessionResult.RefreshTokenHandle}");
            revokeResponse.EnsureSuccessStatusCode();

            var revokedListResponse = await client.GetAsync("/memory/oidc/refresh-tokens?principal=alice&includeRevoked=true");
            revokedListResponse.EnsureSuccessStatusCode();
            var revokedHandles = await revokedListResponse.Content.ReadFromJsonAsync<MemoryOidcRefreshTokenHandle[]>(jsonOptions);
            Assert.NotNull(revokedHandles);
            Assert.NotNull(Assert.Single(revokedHandles, item => item.Id == sessionResult.RefreshTokenHandle).RevokedAt);

            var deniedRevokedHandleRefresh = await client.PostAsJsonAsync("/memory/oidc/refresh", new
            {
                refreshTokenHandle = sessionResult.RefreshTokenHandle
            });
            Assert.Equal(HttpStatusCode.Forbidden, deniedRevokedHandleRefresh.StatusCode);

            var secondCallbackResponse = await client.GetAsync("/memory/oidc/callback?code=good-code&state=fake-state");
            secondCallbackResponse.EnsureSuccessStatusCode();
            var secondSession = await secondCallbackResponse.Content.ReadFromJsonAsync<MemoryOidcSessionResult>(jsonOptions);
            Assert.NotNull(secondSession);
            Assert.False(string.IsNullOrWhiteSpace(secondSession.RefreshTokenHandle));

            var deprovisionResponse = await client.PostAsJsonAsync("/memory/principals/deprovision", new
            {
                principal = "alice"
            });
            deprovisionResponse.EnsureSuccessStatusCode();
            var deprovisioned = await deprovisionResponse.Content.ReadFromJsonAsync<MemoryPrincipalDeprovisionResult>(jsonOptions);
            Assert.NotNull(deprovisioned);
            Assert.Equal(3, deprovisioned.RevokedSessions);
            Assert.Equal(1, deprovisioned.RevokedOidcRefreshTokens);

            var deniedRefresh = await client.PostAsJsonAsync("/memory/oidc/refresh", new
            {
                refreshTokenHandle = secondSession.RefreshTokenHandle
            });
            Assert.Equal(HttpStatusCode.Forbidden, deniedRefresh.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VB_Verbeam__Memory__Oidc__RefreshTokenStorage", previousStorage);
            Environment.SetEnvironmentVariable("VB_Verbeam__Memory__Oidc__RefreshTokenProtectionKey", previousProtectionKey);
        }
    }

    [Fact]
    public async Task MemoryOidcEncryptedRefreshTokenVault_RequiresProtectionKey()
    {
        var previousStorage = Environment.GetEnvironmentVariable("VB_Verbeam__Memory__Oidc__RefreshTokenStorage");
        var previousProtectionKey = Environment.GetEnvironmentVariable("VB_Verbeam__Memory__Oidc__RefreshTokenProtectionKey");
        Environment.SetEnvironmentVariable("VB_Verbeam__Memory__Oidc__RefreshTokenStorage", "encrypted_db");
        Environment.SetEnvironmentVariable("VB_Verbeam__Memory__Oidc__RefreshTokenProtectionKey", " ");
        var databasePath = Path.Combine(_tempDirectory, "encrypted-oidc-vault-unconfigured.sqlite");
        try
        {
            using var factory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Verbeam:CachePath"] = databasePath
                    });
                });
            });
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

            var health = await client.GetFromJsonAsync<JsonElement>("/health", jsonOptions);
            Assert.Equal(
                "encrypted_db_unconfigured",
                health.GetProperty("memory").GetProperty("oidc").GetProperty("refreshTokenStorage").GetString());

            var callbackResponse = await client.GetAsync("/memory/oidc/callback?code=good-code&state=fake-state");
            Assert.Equal(HttpStatusCode.BadRequest, callbackResponse.StatusCode);

            var sessions = await client.GetFromJsonAsync<MemoryPrincipalSession[]>("/memory/principal-sessions?principal=alice", jsonOptions);
            Assert.NotNull(sessions);
            Assert.Empty(sessions);
            AssertDatabaseFilesDoNotContain(databasePath, "refresh-token");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VB_Verbeam__Memory__Oidc__RefreshTokenStorage", previousStorage);
            Environment.SetEnvironmentVariable("VB_Verbeam__Memory__Oidc__RefreshTokenProtectionKey", previousProtectionKey);
        }
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Environment.SetEnvironmentVariable("VB_Verbeam__CachePath", _previousCachePath);
        Environment.SetEnvironmentVariable("VB_Verbeam__DefaultProvider", _previousDefaultProvider);
        Environment.SetEnvironmentVariable("VB_Verbeam__Memory__BearerJwt__Enabled", _previousBearerJwtEnabled);
        Environment.SetEnvironmentVariable("VB_Verbeam__Memory__BearerJwt__Issuer", _previousBearerJwtIssuer);
        Environment.SetEnvironmentVariable("VB_Verbeam__Memory__BearerJwt__Audiences__0", _previousBearerJwtAudience0);
        Environment.SetEnvironmentVariable("VB_Verbeam__Memory__BearerJwt__HmacSecret", _previousBearerJwtHmacSecret);
        Environment.SetEnvironmentVariable("VB_Urls", _previousUrls);

        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private static string CreateHs256Jwt(string principal, IReadOnlyList<string> groups)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object>
        {
            ["sub"] = principal,
            ["iss"] = "https://issuer.example",
            ["aud"] = "verbeam-memory",
            ["groups"] = groups,
            ["exp"] = now.AddMinutes(30).ToUnixTimeSeconds(),
            ["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds()
        };
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }, jsonOptions));
        var body = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, jsonOptions));
        var signingInput = Encoding.UTF8.GetBytes($"{header}.{body}");
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes("oidc-secret"), signingInput);
        return $"{header}.{body}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static void AssertDatabaseFilesDoNotContain(string databasePath, string plaintext)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var bytes = ReadSharedFile(path);
            Assert.Equal(-1, IndexOf(bytes, Encoding.UTF8.GetBytes(plaintext)));
        }
    }

    private static byte[] ReadSharedFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return bytes;
    }

    private static int IndexOf(byte[] source, byte[] needle)
    {
        if (needle.Length == 0)
        {
            return 0;
        }

        for (var index = 0; index <= source.Length - needle.Length; index++)
        {
            var found = true;
            for (var needleIndex = 0; needleIndex < needle.Length; needleIndex++)
            {
                if (source[index + needleIndex] != needle[needleIndex])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return index;
            }
        }

        return -1;
    }

    private sealed class FakeMemoryOidcClient(string idToken) : IMemoryOidcClient
    {
        public bool IsEnabled => true;

        public Task<MemoryOidcLoginStartResult?> StartLoginAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<MemoryOidcLoginStartResult?>(new(
                true,
                "https://issuer.example/authorize?state=fake-state",
                "fake-state",
                DateTimeOffset.UtcNow.AddMinutes(5)));

        public Task<MemoryOidcTokenResult?> ExchangeCodeAsync(
            string code,
            string state,
            CancellationToken cancellationToken = default)
            => Task.FromResult<MemoryOidcTokenResult?>(
                code == "good-code" && state == "fake-state"
                    ? new MemoryOidcTokenResult("Bearer", string.Empty, idToken, "refresh-token", 3600)
                    : null);

        public Task<MemoryOidcTokenResult?> RefreshAsync(
            string refreshToken,
            CancellationToken cancellationToken = default)
            => Task.FromResult<MemoryOidcTokenResult?>(
                refreshToken == "refresh-token"
                    ? new MemoryOidcTokenResult("Bearer", string.Empty, idToken, "refresh-token", 3600)
                    : null);
    }
}
