using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace BusTrack.Api.Services;

public static class CacheDomains
{
    public const string Stops = "stops";
    public const string Routes = "routes";
    public const string Fares = "fares";
    public const string Schedules = "schedules";
    public const string Live = "live";
    public const string Recent = "recent";
    public const string Users = "users";
}

public static class CacheTtls
{
    public static readonly TimeSpan Stops = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan Routes = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Fares = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan FilterOptions = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SearchToday = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SearchFuture = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan Upcoming = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan Recent = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Live = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan UserProfile = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DomainVersion = TimeSpan.FromHours(24);
}

public interface ITransitCache
{
    bool IsRedisConfigured { get; }

    Task<T> GetOrCreateAsync<T>(
        string domain,
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default);

    Task<T> GetOrCreateDirectAsync<T>(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default);

    Task RemoveDirectAsync(string key, CancellationToken ct = default);
    Task InvalidateDomainAsync(string domain, CancellationToken ct = default);
    Task InvalidateDomainsAsync(IEnumerable<string> domains, CancellationToken ct = default);
}

public sealed class TransitCacheService : ITransitCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache;
    private readonly ILogger<TransitCacheService> _logger;
    private readonly ConcurrentDictionary<string, KeyGate> _gates = new(StringComparer.Ordinal);
    private long _versionSeq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public TransitCacheService(
        IDistributedCache cache,
        ILogger<TransitCacheService> logger,
        bool isRedisConfigured = false)
    {
        _cache = cache;
        _logger = logger;
        IsRedisConfigured = isRedisConfigured;
    }

    public bool IsRedisConfigured { get; }

    public async Task<T> GetOrCreateAsync<T>(
        string domain,
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default)
    {
        var version = await GetOrCreateDomainVersionAsync(domain, ct);
        if (version is null)
            return await factory(ct);

        var versionedKey = $"{domain}:{version}:{key}";
        return await GetOrCreateInternalAsync(versionedKey, ttl, factory, ct);
    }

    public Task<T> GetOrCreateDirectAsync<T>(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default) =>
        GetOrCreateInternalAsync(key, ttl, factory, ct);

    public async Task RemoveDirectAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await _cache.RemoveAsync(key, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cache remove failed for key {CacheKey}; continuing cleanly.", key);
        }
    }

    public async Task InvalidateDomainAsync(string domain, CancellationToken ct = default)
    {
        var nextVersion = NextVersionToken();
        try
        {
            await _cache.SetStringAsync(
                VersionKey(domain),
                nextVersion,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtls.DomainVersion },
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cache domain invalidation failed for {CacheDomain}; continuing cleanly.", domain);
        }
    }

    public async Task InvalidateDomainsAsync(IEnumerable<string> domains, CancellationToken ct = default)
    {
        foreach (var domain in domains.Distinct(StringComparer.Ordinal))
        {
            await InvalidateDomainAsync(domain, ct);
        }
    }

    private async Task<T> GetOrCreateInternalAsync<T>(
        string fullKey,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct)
    {
        var cached = await TryReadAsync<T>(fullKey, ct);
        if (cached.Found)
            return cached.Value!;

        var gate = AcquireGate(fullKey);
        await gate.Semaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the per-key gate to avoid stampedes on cold cache misses.
            cached = await TryReadAsync<T>(fullKey, ct);
            if (cached.Found)
                return cached.Value!;

            var fresh = await factory(ct);
            await TryWriteAsync(fullKey, fresh, ttl, ct);
            return fresh;
        }
        finally
        {
            gate.Semaphore.Release();
            ReleaseGate(fullKey, gate);
        }
    }

    private async Task<string?> GetOrCreateDomainVersionAsync(string domain, CancellationToken ct)
    {
        var versionKey = VersionKey(domain);
        try
        {
            var existing = await _cache.GetStringAsync(versionKey, ct);
            if (!string.IsNullOrEmpty(existing))
                return existing;

            var created = NextVersionToken();
            await _cache.SetStringAsync(
                versionKey,
                created,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtls.DomainVersion },
                ct);
            return created;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cache version read failed for domain {CacheDomain}; falling back to database.", domain);
            return null;
        }
    }

    private async Task<(bool Found, T? Value)> TryReadAsync<T>(string fullKey, CancellationToken ct)
    {
        try
        {
            var bytes = await _cache.GetAsync(fullKey, ct);
            if (bytes is null || bytes.Length == 0)
                return (false, default);

            var envelope = JsonSerializer.Deserialize<CacheEnvelope<T>>(bytes, JsonOptions);
            return envelope is null ? (false, default) : (true, envelope.Value);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cache read failed for key {CacheKey}; falling back to database.", fullKey);
            return (false, default);
        }
    }

    private async Task TryWriteAsync<T>(string fullKey, T value, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new CacheEnvelope<T>(value), JsonOptions);
            await _cache.SetAsync(
                fullKey,
                bytes,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Cache write failed for key {CacheKey}; continuing without caching.", fullKey);
        }
    }

    private static string VersionKey(string domain) => $"ver:{domain}";

    private string NextVersionToken() => Interlocked.Increment(ref _versionSeq).ToString("x");

    private KeyGate AcquireGate(string key)
    {
        while (true)
        {
            var gate = _gates.GetOrAdd(key, static _ => new KeyGate());
            if (gate.TryAddRef())
                return gate;
            _gates.TryRemove(new KeyValuePair<string, KeyGate>(key, gate));
        }
    }

    private void ReleaseGate(string key, KeyGate gate)
    {
        if (gate.ReleaseRef())
        {
            _gates.TryRemove(new KeyValuePair<string, KeyGate>(key, gate));
        }
    }

    public static ConfigurationOptions ParseRedisConfiguration(string rawConnection)
    {
        var trimmed = rawConnection.Trim();
        ConfigurationOptions options;
        if (trimmed.StartsWith("redis://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(trimmed);
            var endPoint = uri.IsDefaultPort
                ? $"{uri.Host}:6379"
                : $"{uri.Host}:{uri.Port}";
            options = new ConfigurationOptions
            {
                Ssl = trimmed.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase)
            };
            options.EndPoints.Add(endPoint);

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var sep = uri.UserInfo.IndexOf(':');
                if (sep >= 0)
                {
                    var user = Uri.UnescapeDataString(uri.UserInfo[..sep]);
                    var pass = Uri.UnescapeDataString(uri.UserInfo[(sep + 1)..]);
                    if (!string.IsNullOrEmpty(user) && !user.Equals("default", StringComparison.OrdinalIgnoreCase))
                        options.User = user;
                    options.Password = pass;
                }
                else
                {
                    options.Password = Uri.UnescapeDataString(uri.UserInfo);
                }
            }
        }
        else
        {
            options = ConfigurationOptions.Parse(trimmed);
        }

        // Fail fast rather than stalling API responses if Redis is temporarily unavailable.
        options.AbortOnConnectFail = false;
        if (options.ConnectRetry > 1) options.ConnectRetry = 1;
        if (options.ConnectTimeout > 3000) options.ConnectTimeout = 2000;
        if (options.SyncTimeout > 3000) options.SyncTimeout = 2000;
        if (options.AsyncTimeout > 3000) options.AsyncTimeout = 2000;
        return options;
    }

    public static string UserProfileKey(Guid userId) => $"user:profile:{userId:N}";

    private sealed record CacheEnvelope<T>(T Value);

    private sealed class KeyGate
    {
        private int _refs;
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public bool TryAddRef()
        {
            while (true)
            {
                var current = Volatile.Read(ref _refs);
                if (current < 0) return false;
                if (Interlocked.CompareExchange(ref _refs, current + 1, current) == current)
                    return true;
            }
        }

        public bool ReleaseRef()
        {
            if (Interlocked.Decrement(ref _refs) == 0)
            {
                return Interlocked.CompareExchange(ref _refs, -1, 0) == 0;
            }
            return false;
        }
    }
}

/// <summary>
/// No-op cache used when optional DI consumers construct services without an IDistributedCache.
/// </summary>
public sealed class NullTransitCache : ITransitCache
{
    public static readonly NullTransitCache Instance = new();

    private NullTransitCache() { }

    public bool IsRedisConfigured => false;

    public Task<T> GetOrCreateAsync<T>(
        string domain,
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default) => factory(ct);

    public Task<T> GetOrCreateDirectAsync<T>(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default) => factory(ct);

    public Task RemoveDirectAsync(string key, CancellationToken ct = default) => Task.CompletedTask;

    public Task InvalidateDomainAsync(string domain, CancellationToken ct = default) => Task.CompletedTask;

    public Task InvalidateDomainsAsync(IEnumerable<string> domains, CancellationToken ct = default) => Task.CompletedTask;
}
