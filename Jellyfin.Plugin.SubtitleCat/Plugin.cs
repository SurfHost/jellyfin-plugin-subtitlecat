using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SubtitleCat.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SubtitleCat;

/// <summary>
/// SubtitleCat plugin. Adds subtitlecat.com as a subtitle provider, matched on the
/// release name Jellyfin already knows from the media filename.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "SubtitleCat";

    /// <inheritdoc />
    public override string Description =>
        "Downloads subtitles from subtitlecat.com, matched on the release name of the media file.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("6d16c1a0-4868-4ddb-8d48-6fd5eb0ecac6");

    /// <summary>
    /// Gets the effective configuration, falling back to defaults when the plugin
    /// instance is not available. Search runs on Jellyfin's schedule, so it can be
    /// reached during startup or teardown when <see cref="Instance"/> is not set.
    /// </summary>
    public static PluginConfiguration Config => Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "SubtitleCat",
            EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html"
        }
    };
}
