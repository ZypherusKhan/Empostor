using System.Threading.Tasks;
namespace Impostor.Server.Commands;
public interface ICommand
{
    string Name { get; }
    string[] Aliases => System.Array.Empty<string>();
    string Description { get; }
    string Usage { get; }
    ValueTask<bool> ExecuteAsync(CommandContext ctx);
}
