# Fixtures

Saved verbatim from subtitlecat.com on 2026-07-28. They exist so the regex parsers are
tested against the site's real markup rather than against markup written from memory,
and so a future layout change shows up as a failing test with a readable diff.

| File | Source |
| --- | --- |
| `search-results.html` | `https://www.subtitlecat.com/index.php?search=Dune+Part+Two+2024+1080p` |
| `detail-page.html` | `https://www.subtitlecat.com/subs/789/Dune.Part.Two.2024.REPACK.1080p.10bit.WEBRip.6CH.x265.HEVC-PSA_eng_sdh.html` |
| `detail-page-with-dutch.html` | `https://www.subtitlecat.com/subs/736/Dune.Part.Two.2024.1080p.BluRay.x264.AAC5.1-%5BYTS.MX%5D.html` |

Two of them are deliberately different cases. `detail-page.html` has 16 materialised
languages and **no** Dutch, so it covers the ordinary miss. `detail-page-with-dutch.html`
has 31 and does include Dutch, and its Dutch download sits under `/subs/757/` while the
page itself is `/subs/736/`, which is the case that proves the download URL cannot be
derived from the page id.

To refresh one, re-fetch the URL above with a browser User-Agent and commit the result.
Expect the assertions on exact counts to need updating; that is the point of them.
