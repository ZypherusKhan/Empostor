using System.Threading.Tasks;
using Impostor.Api.Plugins;
using Microsoft.Extensions.Logging;

namespace Impostor.Plugins.FriendCodeValidator
{
    // MatchDuck offered the Friendcode safenames.txt lol
    [ImpostorPlugin("duck.hayashiume.friendcodevalidator", "Friend Code Validator", "MatchDuck & HayashiUme", "1.0.0")]
    public sealed class FriendCodeValidatorPlugin : PluginBase
    {
        private readonly ILogger<FriendCodeValidatorPlugin> _logger;

        public FriendCodeValidatorPlugin(ILogger<FriendCodeValidatorPlugin> logger)
            => _logger = logger;

        public override ValueTask EnableAsync()
        {
            _logger.LogInformation("[FriendCodeValidator] Enabled.");
            return default;
        }

        public override ValueTask DisableAsync() => default;
    }
}
