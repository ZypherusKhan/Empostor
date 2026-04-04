using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Impostor.Api.Config;
using Impostor.Api.Events.Managers;
using Impostor.Api.Net.Messages.C2S;
using Next.Hazel;
using Next.Hazel.Dtls;
using Next.Hazel.Udp;
using Impostor.Server.Events.Client;
using Impostor.Server.Net.Auth;
using Impostor.Server.Net.Hazel;
using Impostor.Server.Net.Manager;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;

namespace Impostor.Server.Net
{
    internal class Matchmaker
    {
        private readonly IEventManager _eventManager;
        private readonly ClientManager _clientManager;
        private readonly ObjectPool<MessageReader> _readerPool;
        private readonly ILogger<HazelConnection> _connectionLogger;
        private readonly ILogger<Matchmaker> _logger;
        private readonly AuthConfig _authConfig;
        private readonly DtlsCertificateService _certService;

        private readonly IpFriendCodeCache _ipCache;
        private readonly FriendCodeNonceStore _nonceStore;

        private UdpConnectionListener? _gameListener;
        private DtlsConnectionListener? _dtlsAuthListener;

        public Matchmaker(
            IEventManager eventManager,
            ClientManager clientManager,
            ObjectPool<MessageReader> readerPool,
            ILogger<HazelConnection> connectionLogger,
            ILogger<Matchmaker> logger,
            IOptions<AuthConfig> authConfig,
            DtlsCertificateService certService,
            IpFriendCodeCache ipCache,
            FriendCodeNonceStore nonceStore)
        {
            _eventManager = eventManager;
            _clientManager = clientManager;
            _readerPool = readerPool;
            _connectionLogger = connectionLogger;
            _logger = logger;
            _authConfig = authConfig.Value;
            _certService = certService;
            _ipCache = ipCache;
            _nonceStore = nonceStore;
        }

        public async ValueTask StartAsync(IPEndPoint ipEndPoint)
        {
            var mode = ipEndPoint.AddressFamily switch
            {
                AddressFamily.InterNetwork   => IPMode.IPv4,
                AddressFamily.InterNetworkV6 => IPMode.IPv6,
                _ => throw new InvalidOperationException(),
            };

            _gameListener = new UdpConnectionListener(ipEndPoint, _readerPool, mode)
            {
                NewConnection = OnNewConnection,
            };
            await _gameListener.StartAsync();
            _logger.LogInformation("[Matchmaker] UDP game listener started on {EndPoint}", ipEndPoint);

            if (_authConfig.IsAuthEnabled)
            {
                var authEndPoint = new IPEndPoint(ipEndPoint.Address, ipEndPoint.Port + 2);
                try
                {
                    var cert = _certService.GetOrCreateCertificate();
                    _dtlsAuthListener = new DtlsConnectionListener(authEndPoint, _readerPool, mode)
                    {
                        NewConnection = OnDtlsAuthConnection,
                    };
                    _dtlsAuthListener.SetCertificate(cert);
                    await _dtlsAuthListener.StartAsync();

                    var modeLabel = _authConfig.EnableNonceAuth ? "NonceAuth" : "IpAuth";
                    _logger.LogInformation(
                        "[Matchmaker] DTLS auth listener started on {EndPoint} (mode={Mode})",
                        authEndPoint, modeLabel);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[Matchmaker] Failed to start DTLS auth listener on {EndPoint}",
                        authEndPoint);
                    _dtlsAuthListener = null;
                }
            }
            else
            {
                _logger.LogInformation(
                    "[Matchmaker] Auth disabled — FriendCode will not be set for any client.");
            }
        }

        public async ValueTask StopAsync()
        {
            if (_gameListener != null)
                await _gameListener.DisposeAsync();

            if (_dtlsAuthListener != null)
                await _dtlsAuthListener.DisposeAsync();
        }

        private async ValueTask OnDtlsAuthConnection(NewConnectionEventArgs e)
        {
            var clientIp = NormalizeIp(e.Connection.EndPoint.Address);
            var rawIp = e.Connection.EndPoint.Address;

            try
            {
                AuthHandshakeC2S.Deserialize(
                    e.HandshakeData,
                    out _,
                    out _,
                    out var friendCode);

                if (string.IsNullOrEmpty(friendCode))
                {
                    _logger.LogWarning("[Auth] DTLS from {Ip}: empty FriendCode", clientIp);
                }

                uint nonce = 0;

                if (_authConfig.EnableNonceAuth)
                {
                    nonce = _nonceStore.Issue(friendCode ?? string.Empty, clientIp);
                    _logger.LogInformation(
                        "[NonceAuth] DTLS from {Ip}: FriendCode={FriendCode}, issued nonce 0x{Nonce:X8}",
                        clientIp, friendCode, nonce);
                }
                else
                {
                    _ipCache.Set(rawIp, friendCode ?? string.Empty);
                    _logger.LogInformation(
                        "[IpAuth] DTLS from {Ip}: FriendCode={FriendCode} (stored by IP, overwrite)",
                        clientIp, friendCode);

                    nonce = (uint)(DateTime.UtcNow.Ticks & 0xFFFF_FFFF);
                    if (nonce == 0) nonce = 1;
                }

                using var writer = MessageWriter.Get(MessageType.Reliable);
                writer.StartMessage(1);
                writer.Write(nonce);
                writer.EndMessage();
                await e.Connection.SendAsync(writer);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Auth] Error in DTLS auth from {Ip}", clientIp);
            }
        }

        private async ValueTask OnNewConnection(NewConnectionEventArgs e)
        {
            HandshakeC2S.Deserialize(
                e.HandshakeData,
                out var clientVersion,
                out var name,
                out var language,
                out var chatMode,
                out var platformSpecificData,
                out var nonce);

            var connection = new HazelConnection(e.Connection, _connectionLogger);
            await _eventManager.CallAsync(new ClientConnectionEvent(connection, e.HandshakeData));
            await _clientManager.RegisterConnectionAsync(
                connection, name, clientVersion, language, chatMode, platformSpecificData, nonce);
        }

        private static string NormalizeIp(IPAddress addr)
            => addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4().ToString() : addr.ToString();
    }
}
