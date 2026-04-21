using FhirAugury.Source.Zulip.Api;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Controllers;
using FhirAugury.Source.Zulip.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Tests;

/// <summary>
/// Phase B B1: Zulip exposes <c>items/{id}/comments</c> and
/// <c>items/{id}/links</c> as empty-array stubs so cross-source consumers
/// can call the same shape as Jira even though Zulip has no first-class
/// comment/link concept.
/// </summary>
public class ItemsControllerStubTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ZulipDatabase _db;
    private readonly ItemsController _controller;

    public ItemsControllerStubTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zulip_items_stub_{Guid.NewGuid()}.db");
        _db = new ZulipDatabase(_dbPath, NullLogger<ZulipDatabase>.Instance);
        _db.Initialize();

        IOptions<ZulipServiceOptions> opts = Options.Create(new ZulipServiceOptions());
        _controller = new ItemsController(_db, opts);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void GetComments_ReturnsOkWithEmptyArray()
    {
        IActionResult result = _controller.GetComments("12345");

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        ZulipCommentEntry[] body = Assert.IsType<ZulipCommentEntry[]>(ok.Value);
        Assert.Empty(body);
    }

    [Fact]
    public void GetLinks_ReturnsOkWithEmptyArray()
    {
        IActionResult result = _controller.GetLinks("67890");

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        ZulipItemLinkEntry[] body = Assert.IsType<ZulipItemLinkEntry[]>(ok.Value);
        Assert.Empty(body);
    }

    [Fact]
    public void GetComments_NonNumericId_StillReturnsEmptyArray()
    {
        // Stub does not parse the id; shape parity with Jira (string keys).
        IActionResult result = _controller.GetComments("not-a-number");

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        ZulipCommentEntry[] body = Assert.IsType<ZulipCommentEntry[]>(ok.Value);
        Assert.Empty(body);
    }
}
