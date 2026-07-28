using System;
using System.Net;
using System.Net.Http;
using Jellyfin.Plugin.SubtitleCat.Models;
using Jellyfin.Plugin.SubtitleCat.Providers;
using Jellyfin.Plugin.SubtitleCat.Scraping;
using Jellyfin.Plugin.SubtitleCat.Translation;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SubtitleCat;

/// <summary>
/// Registers the provider and the services behind it.
/// </summary>
/// <remarks>
/// This file is what decides whether the plugin exists at all. Jellyfin's subtitle
/// manager takes its providers from dependency injection, so without the
/// <see cref="ISubtitleProvider"/> registration below the plugin installs, enables, and
/// simply never appears in any subtitle search, with nothing logged to say why.
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        _ = applicationHost;

        serviceCollection.AddSingleton<ScrapeHealth>();
        serviceCollection.AddSingleton<SubtitleCatClient>();

        // Every backend is registered and the configured one is picked per call, so
        // changing the setting takes effect without a restart.
        serviceCollection.AddSingleton<ISubtitleTranslator, LibreTranslateTranslator>();
        serviceCollection.AddSingleton<ISubtitleTranslator, GoogleTranslateTranslator>();
        serviceCollection.AddSingleton<ISubtitleTranslator, DeepLTranslator>();
        serviceCollection.AddSingleton<TranslatorRegistry>();
        serviceCollection.AddSingleton<SubtitleTranslationService>();

        // The registration Jellyfin actually consumes. Everything above is only
        // reachable through it.
        serviceCollection.AddSingleton<ISubtitleProvider, SubtitleCatDownloader>();

        serviceCollection
            .AddHttpClient(SubtitleCatClient.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // The site serves gzip and the pages are a few hundred kilobytes each.
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            });

        foreach (var name in new[]
        {
            LibreTranslateTranslator.ClientName,
            GoogleTranslateTranslator.ClientName,
            DeepLTranslator.ClientName
        })
        {
            serviceCollection
                .AddHttpClient(name)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2)
                });
        }
    }
}
