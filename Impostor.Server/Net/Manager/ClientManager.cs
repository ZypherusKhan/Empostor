using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Impostor.Api.Config;
using Impostor.Api.Events.Managers;
using Impostor.Api.Innersloth;
using Impostor.Api.Net;
using Impostor.Api.Net.Manager;
using Next.Hazel;
using Impostor.Server.Events.Client;
using Impostor.Server.Net.Auth;
using Impostor.Server.Net.Factories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Impostor.Server.Net.Manager
{
    internal partial class ClientManager
    {
        private readonly ILogger<ClientManager> _logger;
        private readonly IEventManager _eventManager;
        private readonly ConcurrentDictionary<int, ClientBase> _clients;
        private readonly ICompatibilityManager _compatibilityManager;
        private readonly CompatibilityConfig _compatibilityConfig;
        private readonly IClientFactory _clientFactory;
        private readonly AuthConfig _authConfig;

        // IpAuth：IP → FriendCode（NextImpostor 原版覆盖映射）
        private readonly IpFriendCodeCache _ipCache;

        // NonceAuth：Nonce → FriendCode（精确一次性匹配）
        private readonly FriendCodeNonceStore _nonceStore;

        private int _idLast;

        public ClientManager(
            ILogger<ClientManager> logger,
            IEventManager eventManager,
            IClientFactory clientFactory,
            ICompatibilityManager compatibilityManager,
            IOptions<CompatibilityConfig> compatibilityConfig,
            IOptions<AuthConfig> authConfig,
            IpFriendCodeCache ipCache,
            FriendCodeNonceStore nonceStore)
        {
            _logger = logger;
            _eventManager = eventManager;
            _clientFactory = clientFactory;
            _clients = new ConcurrentDictionary<int, ClientBase>();
            _compatibilityManager = compatibilityManager;
            _compatibilityConfig = compatibilityConfig.Value;
            _authConfig = authConfig.Value;
            _ipCache = ipCache;
            _nonceStore = nonceStore;

            if (_compatibilityConfig.AllowFutureGameVersions
                || _compatibilityConfig.AllowHostAuthority
                || _compatibilityConfig.AllowVersionMixing)
            {
                _logger.LogWarning("One or more compatibility options were enabled, please mention these when seeking support:");

                if (_compatibilityConfig.AllowFutureGameVersions)
                    _logger.LogWarning("AllowFutureGameVersions, which allows future Among Us versions to connect that were unknown at the time this Impostor was built");

                if (_compatibilityConfig.AllowHostAuthority)
                    _logger.LogWarning("AllowHostAuthority, which allows game hosts to control more game features, but it uses less well tested code on the client, which causes some bugs");

                if (_compatibilityConfig.AllowVersionMixing)
                    _logger.LogWarning("AllowVersionMixing, which allows players to join games created on different game versions that they may not be 100% compatible with");
            }
        }

        public IEnumerable<ClientBase> Clients => _clients.Values;

        public int NextId()
        {
            var clientId = Interlocked.Increment(ref _idLast);
            if (clientId < 1)
            {
                _idLast = 0;
                clientId = Interlocked.Increment(ref _idLast);
            }
            return clientId;
        }

        public async ValueTask RegisterConnectionAsync(
            IHazelConnection connection,
            string name,
            GameVersion clientVersion,
            Language language,
            QuickChatModes chatMode,
            PlatformSpecificData? platformSpecificData,
            uint nonce = 0)
        {
            var versionCompare = _compatibilityManager.CanConnectToServer(clientVersion);
            if (versionCompare == ICompatibilityManager.VersionCompareResult.ServerTooOld
                && _compatibilityConfig.AllowFutureGameVersions
                && platformSpecificData != null)
            {
                _logger.LogWarning(
                    "Client connected using future version: {clientVersion} ({version}). Unsupported, continue at your own risk.",
                    clientVersion.Value, clientVersion.ToString());
            }
            else if (versionCompare != ICompatibilityManager.VersionCompareResult.Compatible
                     || platformSpecificData == null)
            {
                _logger.LogInformation(
                    "Client connected using unsupported version: {clientVersion} ({version})",
                    clientVersion.Value, clientVersion.ToString());

                using var packet = MessageWriter.Get(MessageType.Reliable);
                var message = versionCompare switch
                {
                    ICompatibilityManager.VersionCompareResult.ClientTooOld => DisconnectMessages.VersionClientTooOld,
                    ICompatibilityManager.VersionCompareResult.ServerTooOld => DisconnectMessages.VersionServerTooOld,
                    ICompatibilityManager.VersionCompareResult.Unknown       => DisconnectMessages.VersionUnsupported,
                    _ => throw new ArgumentOutOfRangeException(),
                };
                await connection.CustomDisconnectAsync(DisconnectReason.Custom, message);
                return;
            }

            if (clientVersion.HasDisableServerAuthorityFlag)
            {
                if (!_compatibilityConfig.AllowHostAuthority)
                {
                    _logger.LogInformation("Player {Name} kicked because they requested host authority.", name);
                    await connection.CustomDisconnectAsync(DisconnectReason.Custom, DisconnectMessages.HostAuthorityUnsupported);
                    return;
                }
                _logger.LogInformation(
                    "Player {Name} connected with server authority disabled, please mention that this mode is in use when asking for support.", name);
            }

            if (name.Length > 10)
            {
                await connection.CustomDisconnectAsync(DisconnectReason.Custom, DisconnectMessages.UsernameLength);
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                await connection.CustomDisconnectAsync(DisconnectReason.Custom, DisconnectMessages.UsernameIllegalCharacters);
                return;
            }

            // ── FriendCode 查找 ──────────────────────────────────────────────
            string? friendCode = null;

            if (_authConfig.EnableNonceAuth)
            {
                // NonceAuth：以 Nonce 为 key 精确查找，一次性消费
                if (nonce != 0)
                {
                    friendCode = _nonceStore.Consume(nonce);
                    if (friendCode != null)
                        _logger.LogInformation(
                            "[NonceAuth] {Name}: nonce 0x{Nonce:X8} → FriendCode={FriendCode}",
                            name, nonce, friendCode);
                    else
                        _logger.LogWarning(
                            "[NonceAuth] {Name}: nonce 0x{Nonce:X8} not found or expired. IP={Ip}",
                            name, nonce, NormalizeIp(connection.EndPoint?.Address));
                }
                else
                {
                    _logger.LogWarning(
                        "[NonceAuth] {Name}: nonce=0, DTLS auth may not have completed. IP={Ip}",
                        name, NormalizeIp(connection.EndPoint?.Address));
                }
            }
            else if (_authConfig.EnableIpAuth)
            {
                // IpAuth：按客户端 IP 查找（NextImpostor 原版行为，含竞争）
                var clientIp = connection.EndPoint?.Address;
                if (clientIp != null)
                {
                    friendCode = _ipCache.Get(clientIp);
                    if (friendCode != null)
                        _logger.LogInformation(
                            "[IpAuth] {Name}: IP={Ip} → FriendCode={FriendCode}",
                            name, NormalizeIp(clientIp), friendCode);
                    else
                        _logger.LogWarning(
                            "[IpAuth] {Name}: no FriendCode cached for IP={Ip}",
                            name, NormalizeIp(clientIp));
                }
            }
            // else: auth disabled → FriendCode stays null

            // ── 创建并注册客户端 ─────────────────────────────────────────────
            var client = _clientFactory.Create(connection, name, clientVersion, language, chatMode, platformSpecificData);
            client.FriendCode = string.IsNullOrEmpty(friendCode) ? null : friendCode;

            var id = NextId();
            client.Id = id;

            _logger.LogTrace(
                "Client connected: Id={Id} Name={Name} FriendCode={FriendCode}",
                id, name, client.FriendCode ?? "(none)");

            _clients.TryAdd(id, client);
            await _eventManager.CallAsync(new ClientConnectedEvent(connection, client));
        }

        public void Remove(IClient client)
        {
            _logger.LogTrace("Client {ClientId} disconnected.", client.Id);
            _clients.TryRemove(client.Id, out _);
        }

        public bool Validate(IClient client)
        {
            return client.Id != 0
                   && _clients.TryGetValue(client.Id, out var registeredClient)
                   && ReferenceEquals(client, registeredClient);
        }

        private static string NormalizeIp(IPAddress? addr)
        {
            if (addr == null) return "(unknown)";
            return addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4().ToString() : addr.ToString();
        }
    }
}
