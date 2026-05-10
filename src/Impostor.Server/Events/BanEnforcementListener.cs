using System.Threading.Tasks;
using Impostor.Api.Events;
using Impostor.Api.Events.Client;
using Impostor.Api.Innersloth;
using Impostor.Server.Service.Admin;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.Events.Client
{
    internal sealed class BanEnforcementListener : IEventListener
    {
        private readonly ILogger<BanEnforcementListener> _logger;
        private readonly BanStore _bans;

        public BanEnforcementListener(ILogger<BanEnforcementListener> logger, BanStore bans)
        {
            _logger = logger;
            _bans   = bans;
        }

        [EventListener]
        public async ValueTask OnClientConnected(IClientConnectedEvent e)
        {
            var client = e.Client;
            var ip     = client.Connection?.EndPoint?.Address;

            if (ip != null && _bans.IsIpBanned(ip))
            {
                _logger.LogWarning("[Ban] Rejected banned IP {Ip} ({Name})", ip, client.Name);
                await client.DisconnectAsync(DisconnectReason.Banned, "You are banned from this server.");
                return;
            }

            if (_bans.IsFriendCodeBanned(client.FriendCode))
            {
                _logger.LogWarning("[Ban] Rejected banned FriendCode {FC} ({Name})", client.FriendCode, client.Name);
                await client.DisconnectAsync(DisconnectReason.Banned, "You are banned from this server.");
            }
        }
    }
}
