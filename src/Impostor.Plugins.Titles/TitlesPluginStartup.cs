using Impostor.Api.Events;
using Impostor.Api.Plugins;
using Impostor.Plugins.Titles.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Impostor.Plugins.Titles;

public sealed class TitlesPluginStartup : IPluginStartup
{
    public void ConfigureHost(IHostBuilder host) { }

    public void ConfigureServices(IServiceCollection services)
    {
        // TitleStore is registered by Impostor.Server (built-in)
        services.AddSingleton<IEventListener, TitleEventListener>();
    }
}
