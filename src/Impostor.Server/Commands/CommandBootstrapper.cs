using System.Threading;
using System.Threading.Tasks;
using Impostor.Server.Commands.Commands;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Impostor.Server.Commands;

internal sealed class CommandBootstrapper : IHostedService
{
    private readonly ILogger<CommandBootstrapper> _logger;
    private readonly CommandService _commands;

    public CommandBootstrapper(
        ILogger<CommandBootstrapper> logger,
        CommandService commands)
    {
        _logger = logger;
        _commands = commands;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _commands.Register(new HelpCommand(_commands));
        _commands.Register(new SetColorCommand());
        _commands.Register(new NoteCommand());
        _logger.LogInformation(
            "[Commands] Registered {Count} built-in command(s): {Names}",
            _commands.All.Count,
            string.Join(", ", System.Linq.Enumerable.Select(_commands.All, c => "/" + c.Name)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
