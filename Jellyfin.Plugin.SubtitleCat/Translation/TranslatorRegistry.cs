using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubtitleCat.Translation;

/// <summary>
/// Resolves the translator the configuration currently asks for.
/// </summary>
/// <remarks>
/// All backends are registered and the choice is made per call, rather than registering
/// only the selected one. Dependency injection is wired once at startup while the setting
/// can change at any moment, so binding the choice at registration would mean a restart
/// after every change.
/// </remarks>
public sealed class TranslatorRegistry
{
    private readonly IReadOnlyList<ISubtitleTranslator> _translators;

    /// <summary>Initializes a new instance of the <see cref="TranslatorRegistry"/> class.</summary>
    public TranslatorRegistry(IEnumerable<ISubtitleTranslator> translators)
    {
        _translators = translators.ToList();
    }

    /// <summary>
    /// Gets the configured translator, or null when translation is off or the chosen
    /// backend is missing something it needs.
    /// </summary>
    public ISubtitleTranslator? Active
    {
        get
        {
            var kind = Plugin.Config.TranslationBackend;
            if (kind == TranslatorKind.None)
            {
                return null;
            }

            var translator = _translators.FirstOrDefault(t => t.Kind == kind);
            return translator?.IsConfigured == true ? translator : null;
        }
    }

    /// <summary>Gets a value indicating whether a usable translator is configured.</summary>
    public bool IsAvailable => Active is not null;

    /// <summary>Gets the active translator's display name, for result labels.</summary>
    public string ActiveName => Active?.Name ?? "translation";

    /// <summary>
    /// Explains why translation is unavailable, for the log and the status panel.
    /// </summary>
    public string? DescribeUnavailable()
    {
        var kind = Plugin.Config.TranslationBackend;
        if (kind == TranslatorKind.None)
        {
            return "Translation is turned off.";
        }

        var translator = _translators.FirstOrDefault(t => t.Kind == kind);
        if (translator is null)
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "No translator is registered for {0}.",
                kind);
        }

        return translator.IsConfigured
            ? null
            : string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} is selected but not fully configured. Check its address or API key.",
                translator.Name);
    }
}
