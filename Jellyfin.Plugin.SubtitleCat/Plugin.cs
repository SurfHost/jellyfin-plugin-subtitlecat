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

    /// <summary>
    /// Reports the version the way meta.json and the plugin catalog write it
    /// (MAJOR.MINOR.PATCH). Jellyfin takes the version it lists for a running plugin
    /// from the assembly, which always has four parts (0.3.2.0), but it looks the
    /// plugin's image up by version against meta.json's 0.3.2. Those two never match,
    /// so the logo vanished from the plugin pages after every restart.
    /// </summary>
    /// <returns>The plugin info with a three-part version.</returns>
    public override PluginInfo GetPluginInfo()
    {
        var info = base.GetPluginInfo();
        info.Version = ManifestVersion(info.Version);
        return info;
    }

    /// <summary>Drops a zero fourth part from a version: 0.3.2.0 becomes 0.3.2.</summary>
    /// <param name="version">The assembly version.</param>
    /// <returns>The version as meta.json writes it.</returns>
    public static Version ManifestVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Revision == 0 ? new Version(version.Major, version.Minor, version.Build) : version;
    }

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
