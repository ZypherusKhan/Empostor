using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.Net.Auth
{
    internal sealed class IpFriendCodeCache : IDisposable
    {
        private readonly ILogger<IpFriendCodeCache> _logger;
        private readonly ConcurrentDictionary<string, IpEntry> _store = new();
        private readonly Timer _cleanupTimer;
        private const int TtlSeconds = 30;

        public IpFriendCodeCache(ILogger<IpFriendCodeCache> logger)
        {
            _logger = logger;
            _cleanupTimer = new Timer(_ => Cleanup(), null,
                TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        public void Set(IPAddress clientIp, string friendCode)
        {
            var key = Normalize(clientIp);
            _store[key] = new IpEntry(friendCode, DateTime.UtcNow);

            // IPv4-mapped IPv6 同时写入 IPv4 版本
            if (clientIp.IsIPv4MappedToIPv6)
            {
                _store[clientIp.MapToIPv4().ToString()] = new IpEntry(friendCode, DateTime.UtcNow);
            }

            _logger.LogDebug("[IpAuth] Set IP={Ip} → FriendCode={FriendCode}", key, friendCode);
        }

        public string? Get(IPAddress clientIp)
        {
            var key = Normalize(clientIp);
            if (_store.TryGetValue(key, out var entry))
            {
                if ((DateTime.UtcNow - entry.StoredAt).TotalSeconds <= TtlSeconds)
                {
                    return entry.FriendCode;
                }
                _store.TryRemove(key, out _);
            }
            return null;
        }

        private static string Normalize(IPAddress addr)
            => addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4().ToString() : addr.ToString();

        private void Cleanup()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-TtlSeconds);
            foreach (var kv in _store)
            {
                if (kv.Value.StoredAt < cutoff)
                    _store.TryRemove(kv.Key, out _);
            }
        }

        public void Dispose() => _cleanupTimer.Dispose();

        private sealed record IpEntry(string FriendCode, DateTime StoredAt);
    }
}
