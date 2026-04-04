using System;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.Net.Auth
{
    /// <summary>
    /// 线程安全的 Nonce ↔ FriendCode 映射表，用于解决 NextImpostor IP 认证的竞争问题。
    ///
    /// 问题根源：
    ///   NextImpostor 原版用 IP → FriendCode 的覆盖映射。
    ///   同一 NAT 地址后的多玩家同时连接时，后者覆盖前者，导致 FriendCode 对调。
    ///
    /// 修复方案：
    ///   每个 DTLS 会话分配一个唯一随机 uint32 Nonce，发回客户端。
    ///   客户端在 UDP 握手的 LastNonce 字段中携带该值。
    ///   服务端以 Nonce 为 key 精确查找 FriendCode，消费后立即删除（一次性）。
    ///   即使 100 个玩家共用同一 IP，每人有唯一 Nonce，绝无冲突。
    /// </summary>
    internal sealed class FriendCodeNonceStore : IDisposable
    {
        private readonly ILogger<FriendCodeNonceStore> _logger;
        private readonly int _ttlSeconds;

        // Nonce → (FriendCode, clientIp, issuedAt)
        private readonly ConcurrentDictionary<uint, NonceEntry> _store = new();

        private readonly Timer _cleanupTimer;

        public FriendCodeNonceStore(ILogger<FriendCodeNonceStore> logger, int ttlSeconds = 30)
        {
            _logger = logger;
            _ttlSeconds = ttlSeconds;
            _cleanupTimer = new Timer(_ => Cleanup(), null,
                TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        /// <summary>
        /// 为一个 DTLS 会话颁发 Nonce，绑定 FriendCode。
        /// 返回分配的 Nonce，服务端需将其发回客户端（tag=1 消息）。
        /// </summary>
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

        /// <summary>
        /// 消费一个 Nonce：找到后立即从 store 删除（一次性使用），返回对应 FriendCode。
        /// 若 Nonce 无效、已过期或已使用，返回 null。
        /// </summary>
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
