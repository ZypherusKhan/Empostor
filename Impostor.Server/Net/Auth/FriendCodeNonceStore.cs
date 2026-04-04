using System;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.Net.Auth
{
    internal sealed class FriendCodeNonceStore : IDisposable
    {
        private readonly ILogger<FriendCodeNonceStore> _logger;
        private readonly int _ttlSeconds;

        private readonly ConcurrentDictionary<uint, NonceEntry> _store = new();

        private readonly Timer _cleanupTimer;

        public FriendCodeNonceStore(ILogger<FriendCodeNonceStore> logger, int ttlSeconds = 30)
        {
            _logger = logger;
            _ttlSeconds = ttlSeconds;
            _cleanupTimer = new Timer(_ => Cleanup(), null,
                TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        public uint Issue(string friendCode, string clientIp)
        {
            uint nonce;
            var rng = new Random();
            var buf = new byte[4];

            // 生成不冲突的非零随机 uint32
            do
            {
                rng.NextBytes(buf);
                nonce = BitConverter.ToUInt32(buf, 0);
            }
            while (nonce == 0 || _store.ContainsKey(nonce));

            _store[nonce] = new NonceEntry(friendCode, clientIp, DateTime.UtcNow);

            _logger.LogDebug(
                "[Auth] Issued nonce 0x{Nonce:X8} → FriendCode={FriendCode} IP={Ip}",
                nonce, friendCode, clientIp);

            return nonce;
        }

        public string? Consume(uint nonce)
        {
            if (nonce == 0)
            {
                return null;
            }

            if (_store.TryRemove(nonce, out var entry))
            {
                if ((DateTime.UtcNow - entry.IssuedAt).TotalSeconds > _ttlSeconds)
                {
                    _logger.LogDebug("[Auth] Nonce 0x{Nonce:X8} expired", nonce);
                    return null;
                }

                _logger.LogDebug(
                    "[Auth] Consumed nonce 0x{Nonce:X8} → FriendCode={FriendCode}",
                    nonce, entry.FriendCode);
                return entry.FriendCode;
            }

            return null;
        }

        private void Cleanup()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-_ttlSeconds);
            var expired = new System.Collections.Generic.List<uint>();

            foreach (var kv in _store)
            {
                if (kv.Value.IssuedAt < cutoff)
                {
                    expired.Add(kv.Key);
                }
            }

            foreach (var k in expired)
            {
                _store.TryRemove(k, out _);
            }

            if (expired.Count > 0)
            {
                _logger.LogDebug("[Auth] Cleaned up {Count} expired nonces", expired.Count);
            }
        }

        public void Dispose() => _cleanupTimer.Dispose();

        private sealed record NonceEntry(string FriendCode, string ClientIp, DateTime IssuedAt);
    }
}
