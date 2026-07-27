using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleCat.Models;
using Jellyfin.Plugin.SubtitleCat.Scraping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleCat.Api;

/// <summary>The result of a test search run from the configuration page.</summary>
public sealed class TestSearchResponse
{
    /// <summary>Gets or sets the query that was run.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Gets or sets how many result rows were parsed.</summary>
    public int RowsParsed { get; set; }

    /// <summary>Gets or sets how long the search took, in milliseconds.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>Gets or sets the first few release names found, as a sanity check.</summary>
    public IReadOnlyList<string> SampleNames { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Admin endpoints backing the configuration page.
/// </summary>
[ApiController]
[Route("SubtitleCat")]
public class SubtitleCatController : ControllerBase
{
    private readonly SubtitleCatClient _client;

    /// <summary>Initializes a new instance of the <see cref="SubtitleCatController"/> class.</summary>
    public SubtitleCatController(SubtitleCatClient client)
    {
        _client = client;
    }

    /// <summary>Gets the current scrape health.</summary>
    [HttpGet("Admin/Health")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public ActionResult<ScrapeHealthReport> Health()
    {
        SetNoStoreHeaders();
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        return Ok(_client.GetHealth());
    }

    /// <summary>
    /// Runs a search and reports what came back, so a suspected parser problem can be
    /// confirmed without hunting for a film to test against.
    /// </summary>
    /// <param name="query">The text to search subtitlecat for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [HttpPost("Admin/TestSearch")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public async Task<ActionResult<TestSearchResponse>> TestSearch(
        [FromQuery] string? query,
        CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("Enter something to search for.");
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var rows = await _client.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        started.Stop();

        return Ok(new TestSearchResponse
        {
            Query = query,
            RowsParsed = rows.Count,
            ElapsedMs = started.ElapsedMilliseconds,
            SampleNames = rows.Take(5).Select(r => r.Name).ToList()
        });
    }

    /// <summary>Empties the page cache, so the next search fetches fresh pages.</summary>
    [HttpPost("Admin/ClearCache")]
    [Authorize(AuthenticationSchemes = "CustomAuthentication")]
    public ActionResult ClearCache()
    {
        SetNoStoreHeaders();
        if (!User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        _client.ClearCache();
        return NoContent();
    }

    private void SetNoStoreHeaders()
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
    }
}
