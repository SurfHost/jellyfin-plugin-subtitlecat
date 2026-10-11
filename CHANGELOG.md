# Changelog

The text Jellyfin shows in its plugin catalog is taken from this file: on release, the section for the version being
released is flattened into one paragraph and written to `manifest.json`. Edit this file first when preparing a release.
Releases before 0.3.0 predate this file; their notes are on the GitHub releases page.

## 0.3.2

- Fixed: the plugin's logo disappeared from Jellyfin's plugin pages after a restart. The running plugin reported its version with four parts (0.3.1.0) while Jellyfin looks the image up by the three-part version from meta.json (0.3.1).

## 0.3.1

- A logo for the plugin, shown in Jellyfin's plugin catalog and on the installed plugins page.

Repository changes, no effect on the plugin itself:

- The release zip now carries `logo.png` next to the dll and `meta.json`. `logo.svg` is the editable source
  of the logo.

## 0.3.0

For Jellyfin 12.2. Jellyfin 12 runs on .NET 10 and no longer loads plugins built for 10.11, so this release is rebuilt
for it. Servers still on Jellyfin 10.11 should stay on 0.2.0, which remains in the catalog for them.

- Built for Jellyfin 12.2 and .NET 10 (`targetAbi 12.2.0.0`). Nothing else changes: the subtitle provider, matching,
  translation backends and settings behave exactly as in 0.2.0, and existing settings carry over.

Repository changes, no effect on the plugin itself:

- The release workflow reads the target framework and `targetAbi` from `meta.json` instead of hard-coding them, and
  fills the catalog changelog from this file instead of "See the GitHub release notes."
- Builds use the .NET 10 SDK, pinned through a new `global.json`.
