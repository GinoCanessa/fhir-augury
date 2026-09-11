using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;

public static class PreparedTicketSnapshotSchemaV2
{
    public const int Version = AuthoringSnapshotSchemaV2.Version;
    public const string ProcessorKind = PreparedTicketSnapshotSchemaV1.ProcessorKind;

    public static AuthoringSnapshotSchemaCatalog Catalog { get; } = new(
        Version,
        CreateTables(),
        [
            .. PreparedTicketSnapshotSchemaV1.CountedTables,
            "prepared_ticket_in_person_requesters",
        ]);

    public static IReadOnlyList<AuthoringSnapshotTableSchema> Tables =>
        Catalog.Tables;

    public static IReadOnlyList<string> CountedTables =>
        Catalog.CountedTables;

    private static IEnumerable<AuthoringSnapshotTableSchema> CreateTables()
    {
        foreach (AuthoringSnapshotTableSchema table in
                 AuthoringSnapshotSchemaV2.CoreTables)
        {
            yield return table;
        }

        HashSet<string> v1CoreTables = AuthoringSnapshotSchemaV1.CoreTables
            .Select(table => table.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (AuthoringSnapshotTableSchema table in
                 PreparedTicketSnapshotSchemaV1.Tables)
        {
            if (v1CoreTables.Contains(table.Name))
            {
                continue;
            }

            yield return table.Name switch
            {
                "prepared_ticket_hydration" => new(
                    table.Name,
                    [
                        "RowId", "Id", "TicketKey", "Priority", "Resolution",
                        "ResolutionDescriptionPlain", "Specification",
                        "RaisedInVersion", "SelectedBallot", "ChangeCategory",
                        "Impact", "Labels", "CommentCount", "DescriptionPlain",
                        "DescriptionHtml", "ResolutionDescriptionHtml",
                        "Reporter", "Assignee", "CreatedAt",
                        "RelatedArtifactsRaw", "RelatedPagesRaw",
                        "SourceProject", "SourceLastSuccessfulRefreshAt",
                        "SourceContentRevision", "HydratedAt",
                        "HydrationStatus", "HydrationReason",
                    ]),
                "prepared_jira_hydration" => new(
                    table.Name,
                    [
                        "RowId", "Id", "TicketKey", "JiraKey", "Title",
                        "Status", "Type", "Priority", "Resolution",
                        "ResolutionDescriptionPlain", "WorkGroup",
                        "WorkGroupClean", "Specification", "UpdatedAt", "Url",
                        "DescriptionHtml", "ResolutionDescriptionHtml",
                        "Reporter", "Assignee", "CreatedAt",
                        "RelatedArtifactsRaw", "RelatedPagesRaw", "HydratedAt",
                        "HydrationStatus", "HydrationReason",
                    ]),
                _ => table,
            };
        }

        yield return new AuthoringSnapshotTableSchema(
            "prepared_ticket_in_person_requesters",
            ["RowId", "TicketKey", "DisplayName"]);
    }
}

public static class PreparedTicketSnapshotSchemaResolver
{
    public static IReadOnlyList<int> SupportedVersions { get; } =
        Array.AsReadOnly(
        [
            PreparedTicketSnapshotSchemaV1.Version,
            PreparedTicketSnapshotSchemaV2.Version,
            PreparedTicketSnapshotSchemaV3.Version,
        ]);

    public static AuthoringSnapshotSchemaCatalog Resolve(int schemaVersion)
        => schemaVersion switch
        {
            PreparedTicketSnapshotSchemaV1.Version =>
                PreparedTicketSnapshotSchemaV1.Catalog,
            PreparedTicketSnapshotSchemaV2.Version =>
                PreparedTicketSnapshotSchemaV2.Catalog,
            PreparedTicketSnapshotSchemaV3.Version =>
                PreparedTicketSnapshotSchemaV3.Catalog,
            _ => throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                $"Unsupported prepared-ticket snapshot schema version. Supported versions: {string.Join(", ", SupportedVersions)}."),
        };

    public static bool TryResolve(
        int schemaVersion,
        out AuthoringSnapshotSchemaCatalog? catalog)
    {
        catalog = schemaVersion switch
        {
            PreparedTicketSnapshotSchemaV1.Version =>
                PreparedTicketSnapshotSchemaV1.Catalog,
            PreparedTicketSnapshotSchemaV2.Version =>
                PreparedTicketSnapshotSchemaV2.Catalog,
            PreparedTicketSnapshotSchemaV3.Version =>
                PreparedTicketSnapshotSchemaV3.Catalog,
            _ => null,
        };
        return catalog is not null;
    }
}
