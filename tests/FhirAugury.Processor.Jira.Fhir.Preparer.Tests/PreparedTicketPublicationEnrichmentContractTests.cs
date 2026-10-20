using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketPublicationEnrichmentContractTests
{
    [Fact]
    public void Fingerprint_BindsRecipeBaselineProtectedGraphAndGrouping()
    {
        PreparedTicketPublicationEnrichmentInput input = CreateInput();
        string fingerprint = PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(input);
        PreparedTicketPublicationEnrichmentSource source = input.Source;
        PreparedTicketPublicationEnrichmentInput[] changed =
        [
            input with { Source = source with { RunId = "another-source-run" } },
            input with { Source = source with { SnapshotId = "another-snapshot" } },
            input with { Source = source with { SnapshotSha256 = Hash("other bytes") } },
            input with { Source = source with { AuthoringEpoch = source.AuthoringEpoch + 1 } },
            input with { Source = source with { Sequence = source.Sequence + 1 } },
            input with { Source = source with { SchemaVersion = 2 } },
            input with { Source = source with { SizeBytes = source.SizeBytes + 1 } },
            input with { SourceCorpusFingerprint = Hash("another original corpus") },
            WithCorpus(input, [input.Corpus[0] with { ReceiptId = "new-receipt" }, .. input.Corpus.Skip(1)]),
            WithRows(input, [input.ProtectedRows[0] with { Fingerprint = Hash("changed authored value") }, .. input.ProtectedRows.Skip(1)]),
            WithGrouping(input, [input.Grouping[0] with { OutputFingerprint = Hash("changed grouping text") }, .. input.Grouping.Skip(1)]),
            WithGrouping(input, [input.Grouping[0] with { ProtectedRowsFingerprint = Hash("changed grouping ID") }, .. input.Grouping.Skip(1)]),
            WithGrouping(input, [input.Grouping[0] with { CorpusFingerprint = Hash("changed partition membership") }, .. input.Grouping.Skip(1)]),
            input with { AdditionalTicketKeys = ["FHIR-1", "FHIR-3"] },
            input with { ZulipReferences = [input.ZulipReferences[0] with { AssociationId = "replaced-association" }, .. input.ZulipReferences.Skip(1)] },
        ];
        Assert.All(changed, candidate => Assert.NotEqual(
            fingerprint, PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(candidate)));
        Assert.Throws<NotSupportedException>(() =>
            PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(input with { RecipeVersion = 2 }));
        Assert.Throws<NotSupportedException>(() =>
            PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(input with { RecipeName = "other" }));
        Assert.Equal("publication-enrichment", input.RecipeName);
        Assert.Equal("publication-enrichment-v1", PreparedTicketPublicationEnrichmentContract.StageName);
        Assert.NotEqual(
            PreparerDatabase.PublicationMetadataStageName,
            PreparedTicketPublicationEnrichmentContract.StageName);
    }

    [Fact]
    public void Fingerprint_PreservesNullEmptyAndRevisionText()
    {
        PreparedTicketPublicationProtectedRow nullRow = Row("null", null);
        PreparedTicketPublicationProtectedRow emptyRow = Row("text", "");
        PreparedTicketPublicationProtectedRow[] rows =
        [
            nullRow, emptyRow, Row("text", " "), Row("text", "<null>"),
            Row("text", "1"), Row("integer", "1"), Row("text", " A\r\nB "),
            Row("text", "A\nB"), Row("text", "a\u001fb\nc"),
        ];
        Assert.Equal(rows.Length, rows.Select(row =>
            PreparedTicketPublicationEnrichmentContract.FingerprintRow(row).Fingerprint)
            .Distinct(StringComparer.Ordinal).Count());
        PreparedTicketPublicationEnrichmentInput input = CreateInput();
        PreparedTicketPublicationEnrichmentInput offset = WithCorpus(input,
            [input.Corpus[0] with { ExpectedSourceRevision = "2025-07-17T16:12:12.0000000-05:00" }, .. input.Corpus.Skip(1)]);
        PreparedTicketPublicationEnrichmentInput utc = WithCorpus(input,
            [input.Corpus[0] with { ExpectedSourceRevision = "2025-07-17T21:12:12.0000000+00:00" }, .. input.Corpus.Skip(1)]);
        PreparedTicketPublicationEnrichmentInput trimmedPrecision = WithCorpus(input,
            [input.Corpus[0] with { ExpectedSourceRevision = "2025-07-17T21:12:12Z" }, .. input.Corpus.Skip(1)]);
        Assert.NotEqual(
            PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(offset),
            PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(utc));
        Assert.NotEqual(
            PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(utc),
            PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(trimmedPrecision));
        string json = PreparedTicketPublicationEnrichmentContract.SerializeInputJson(offset);
        Assert.Equal(offset.Corpus[0].ExpectedSourceRevision,
            PreparedTicketPublicationEnrichmentContract.ParseInput(json).Corpus[0].ExpectedSourceRevision);
    }

    [Fact]
    public void Fingerprint_IsIndependentOfQueryEnumerationOrder()
    {
        PreparedTicketPublicationEnrichmentInput input = CreateInput();
        PreparedTicketPublicationEnrichmentInput reversed = input with
        {
            Corpus = input.Corpus.Reverse().ToArray(),
            ProtectedRows = input.ProtectedRows.Reverse().ToArray(),
            Grouping = input.Grouping.Reverse().ToArray(),
            ZulipReferences = input.ZulipReferences.Reverse().ToArray(),
            AdditionalTicketKeys = input.AdditionalTicketKeys.Reverse().ToArray(),
        };
        Assert.Equal(
            PreparedTicketPublicationEnrichmentContract.SerializeInput(input),
            PreparedTicketPublicationEnrichmentContract.SerializeInput(reversed));
        PreparedTicketPublicationProtectedRow row = Row("text", "exact value");
        Assert.Equal(
            PreparedTicketPublicationEnrichmentContract.SerializeRow(row),
            PreparedTicketPublicationEnrichmentContract.SerializeRow(row with { Values = row.Values.Reverse().ToArray() }));
        string json = PreparedTicketPublicationEnrichmentContract.SerializeInputJson(input);
        Assert.Equal(json, PreparedTicketPublicationEnrichmentContract.SerializeInputJson(
            PreparedTicketPublicationEnrichmentContract.ParseInput(json)));
        Assert.Equal(["fhir-2", "FHIR-3"],
            PreparedTicketPublicationEnrichmentContract.ParseInput(json).AdditionalTicketKeys);
        Assert.DoesNotContain("authored body", json, StringComparison.Ordinal);
    }

    [Fact]
    public void InputRefusesUnknownNonNullMetadataAndInconsistentFrozenFingerprints()
    {
        PreparedTicketPublicationEnrichmentInput input = CreateInput();
        string json = PreparedTicketPublicationEnrichmentContract.SerializeInputJson(input);
        AuthoringMaintenanceRunRequest envelope = new(
            1, input.RecipeName, 1, new(input.Source.SnapshotId, 1, 3, 2),
            json.Replace("\"recipeVersion\":1", "\"recipeVersion\":2", StringComparison.Ordinal));
        AuthoringMaintenanceRunRequest parsed = Assert.IsType<AuthoringMaintenanceRunRequest>(
            AuthoringMaintenanceRunRequest.Parse(envelope.Serialize()));
        parsed.EnsureRecipe(PreparedTicketPublicationEnrichmentContract.RecipeName, 1);
        Assert.NotNull(parsed.CorpusComparison);
        Assert.Throws<NotSupportedException>(() =>
            PreparedTicketPublicationEnrichmentContract.ParseInput(parsed.RecipeInputJson));
        Assert.Throws<ArgumentException>(() =>
            PreparedTicketPublicationEnrichmentContract.SerializeInput(input with { ProtectedContentFingerprint = Hash("other") }));
        Assert.Throws<ArgumentException>(() =>
            PreparedTicketPublicationEnrichmentContract.SerializeInput(input with { CorpusFingerprint = Hash("other") }));
        Assert.Throws<ArgumentException>(() =>
            PreparedTicketPublicationEnrichmentContract.SerializeInput(input with { RetainedGroupingFingerprint = Hash("other") }));
        Assert.Throws<ArgumentException>(() =>
            PreparedTicketPublicationEnrichmentContract.SerializeInput(input with { AdditionalTicketKeys = ["fhir-2", "fhir-2"] }));
        Assert.Throws<ArgumentException>(() =>
            PreparedTicketPublicationEnrichmentContract.SerializeInput(input with { ProtectedRows = [input.ProtectedRows[0], input.ProtectedRows[0]] }));
        Assert.Throws<JsonException>(() =>
            PreparedTicketPublicationEnrichmentContract.ParseInput(json.Replace(
                "\"recipeVersion\":1", "\"recipeVersion\":1,\"recipeVersion\":1", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => PreparedTicketPublicationEnrichmentContract.ParseInput("{}"));
        Assert.Throws<JsonException>(() => PreparedTicketPublicationEnrichmentContract.ParseInput("null"));
        Assert.Null(AuthoringMaintenanceRunRequest.Parse(null));
    }

    [Fact]
    public void LegacyFingerprint_RemainsUnchanged()
    {
        PreparedTicketPublicationCorpusItem[] corpus =
        [
            new("FHIR-2", "receipt-2", "item-2", "run-2", "fhir", "revision-2"),
            new("FHIR-1", "receipt-1", "item-1", "run-1", "fhir", "revision-1"),
        ];
        const string corpusJson = """{"contractVersion":1,"corpus":[{"ticketKey":"FHIR-1","receiptId":"receipt-1","runItemId":"item-1","contributingRunId":"run-1","itemKind":"fhir","expectedSourceRevision":"revision-1"},{"ticketKey":"FHIR-2","receiptId":"receipt-2","runItemId":"item-2","contributingRunId":"run-2","itemKind":"fhir","expectedSourceRevision":"revision-2"}]}""";
        string corpusHash = Hash(corpusJson);
        Assert.Equal(Encoding.UTF8.GetBytes(corpusJson), PreparedTicketPublicationContract.SerializeCorpus(corpus));
        Assert.Equal(corpusHash, PreparedTicketPublicationContract.ComputeCorpusFingerprint(corpus));
        string inputJson = $$"""{"contractVersion":1,"purpose":"publication-refresh","sourceRunId":"source-run","corpusFingerprint":"{{corpusHash}}"}""";
        Assert.Equal(Encoding.UTF8.GetBytes(inputJson),
            PreparedTicketPublicationContract.SerializePublicationRefreshInput("source-run", corpus));
        Assert.Equal(Hash(inputJson),
            PreparedTicketPublicationContract.ComputePublicationRefreshInputFingerprint("source-run", corpus));

        PreparedTicketGroupingPayload grouping = new()
        {
            WorkGroupClean = "FHIRInfrastructure", WorkGroupDisplay = "FHIR Infrastructure",
            Specification = "FHIR", Type = "Change Request",
            Topics = [new()
            {
                ShortDescription = "Topic", LongerDescription = "", RenderOrderHint = null,
                RemainingTicketKeys = ["FHIR-2", "FHIR-1"],
            }],
        };
        const string groupingJson = """{"contractVersion":1,"partitions":[{"workGroupClean":"FHIRInfrastructure","workGroupDisplay":"FHIR Infrastructure","specification":"FHIR","type":"Change Request","topics":[{"shortDescription":"Topic","longerDescription":"","renderOrderHint":null,"linkedTicketGroups":[],"remainingTicketKeys":[{"ticketKey":"FHIR-2","order":0},{"ticketKey":"FHIR-1","order":1}]}]}]}""";
        Assert.Equal(Encoding.UTF8.GetBytes(groupingJson), PreparedTicketPublicationContract.SerializeGrouping([grouping]));
        Assert.Equal(Hash(groupingJson), PreparedTicketPublicationContract.ComputeGroupingPartitionFingerprint(grouping));
        Assert.Equal(1, PreparedTicketPublicationContract.CurrentVersion);
        Assert.Equal("publication-metadata", PreparerDatabase.PublicationMetadataStageName);
        Assert.Equal(3, PreparedTicketSnapshotSchemaV3.Version);
    }

    private static PreparedTicketPublicationEnrichmentInput CreateInput()
    {
        PreparedTicketPublicationCorpusItem[] corpus =
        [
            new("FHIR-1", "receipt-1", "item-1", "run-1", "fhir", "revision-1"),
            new("fhir-2", "receipt-2", "item-2", "run-2", "fhir", "revision-2"),
            new("FHIR-3", "receipt-3", "item-3", "run-3", "fhir", "revision-3"),
        ];
        PreparedTicketPublicationProtectedRowFingerprint[] rows =
        [
            PreparedTicketPublicationEnrichmentContract.FingerprintRow(Row("text", "authored body")),
            PreparedTicketPublicationEnrichmentContract.FingerprintRow(Row("text", "another body") with { Key = "row-2" }),
        ];
        PreparedTicketPublicationProtectedGroupingFingerprint[] grouping =
        [
            new("partition-a", Hash("members-a"), Hash("semantic-a"), Hash("ids-and-order-a")),
            new("partition-b", Hash("members-b"), Hash("semantic-b"), Hash("ids-and-order-b")),
        ];
        return new(
            1, new("source-run", "source-snapshot", Hash("snapshot bytes"), 1, 7, 3, 4096, 1),
            PreparedTicketPublicationContract.ComputeCorpusFingerprint(corpus.Take(1)),
            corpus, PreparedTicketPublicationContract.ComputeCorpusFingerprint(corpus),
            rows, PreparedTicketPublicationEnrichmentContract.ComputeProtectedContentFingerprint(rows),
            grouping, PreparedTicketPublicationEnrichmentContract.ComputeRetainedGroupingFingerprint(grouping),
            [
                new("association-1", "FHIR-1", "stream::topic", "hydration-1", 1),
                new("association-2", "fhir-2", "stream::topic", null, null),
            ],
            ["fhir-2", "FHIR-3"]);
    }

    private static PreparedTicketPublicationProtectedRow Row(string storageClass, string? value)
        => new("prepared_tickets", "FHIR-1", "row-1",
            [new("Id", "text", "semantic-id"), new("ProposalA", storageClass, value)]);

    private static PreparedTicketPublicationEnrichmentInput WithCorpus(
        PreparedTicketPublicationEnrichmentInput input,
        PreparedTicketPublicationCorpusItem[] corpus)
        => input with { Corpus = corpus, CorpusFingerprint = PreparedTicketPublicationContract.ComputeCorpusFingerprint(corpus) };

    private static PreparedTicketPublicationEnrichmentInput WithRows(
        PreparedTicketPublicationEnrichmentInput input,
        PreparedTicketPublicationProtectedRowFingerprint[] rows)
        => input with
        {
            ProtectedRows = rows,
            ProtectedContentFingerprint = PreparedTicketPublicationEnrichmentContract.ComputeProtectedContentFingerprint(rows),
        };

    private static PreparedTicketPublicationEnrichmentInput WithGrouping(
        PreparedTicketPublicationEnrichmentInput input,
        PreparedTicketPublicationProtectedGroupingFingerprint[] grouping)
        => input with
        {
            Grouping = grouping,
            RetainedGroupingFingerprint = PreparedTicketPublicationEnrichmentContract.ComputeRetainedGroupingFingerprint(grouping),
        };

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
