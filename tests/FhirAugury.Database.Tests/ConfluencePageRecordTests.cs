using FhirAugury.Database.Records;

namespace FhirAugury.Database.Tests;

public class ConfluencePageRecordTests
{
    [Fact]
    public void InsertAndSelectSingle_Roundtrip()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        var page = TestHelper.CreateSamplePage("12345", "Test Page");
        ConfluencePageRecord.Insert(conn, page);

        var loaded = ConfluencePageRecord.SelectSingle(conn, ConfluenceId: "12345");
        Assert.NotNull(loaded);
        Assert.Equal("Test Page", loaded.Title);
        Assert.Equal("FHIR", loaded.SpaceKey);
    }

    [Fact]
    public void Update_ModifiesRecord()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        var page = TestHelper.CreateSamplePage("12345", "Original Title");
        ConfluencePageRecord.Insert(conn, page);

        page.Title = "Updated Title";
        page.VersionNumber = 2;
        ConfluencePageRecord.Update(conn, page);

        var loaded = ConfluencePageRecord.SelectSingle(conn, ConfluenceId: "12345");
        Assert.NotNull(loaded);
        Assert.Equal("Updated Title", loaded.Title);
        Assert.Equal(2, loaded.VersionNumber);
    }

    [Fact]
    public void SelectList_FiltersBySpaceKey()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        ConfluencePageRecord.Insert(conn, TestHelper.CreateSamplePage("1", "FHIR Page", spaceKey: "FHIR"));
        ConfluencePageRecord.Insert(conn, TestHelper.CreateSamplePage("2", "FHIRI Page", spaceKey: "FHIRI"));

        var fhirPages = ConfluencePageRecord.SelectList(conn, SpaceKey: "FHIR");
        Assert.Single(fhirPages);
        Assert.Equal("FHIR Page", fhirPages[0].Title);
    }

    [Fact]
    public void InsertIgnoreDuplicates_DoesNotThrow()
    {
        using var conn = TestHelper.CreateInMemoryDb();
        var page = TestHelper.CreateSamplePage("12345", "Test Page");
        ConfluencePageRecord.Insert(conn, page);

        var duplicate = TestHelper.CreateSamplePage("12345", "Duplicate");
        ConfluencePageRecord.Insert(conn, duplicate, ignoreDuplicates: true);

        var count = ConfluencePageRecord.SelectCount(conn);
        Assert.Equal(1, count);
    }
}
