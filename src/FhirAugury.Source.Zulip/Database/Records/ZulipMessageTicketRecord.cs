using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Zulip.Database.Records;

/// <summary>Links a Zulip message to a Jira ticket key.</summary>
[LdgSQLiteTable("zulip_message_tickets")]
[LdgSQLiteIndex(nameof(JiraKey))]
[LdgSQLiteIndex(nameof(ZulipMessageId))]
[LdgSQLiteIndex(nameof(JiraKey), nameof(ZulipMessageId))]
public partial record class ZulipMessageTicketRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary>The Zulip API message ID (matches ZulipMessageRecord.ZulipMessageId).</summary>
    public required int ZulipMessageId { get; set; }

    /// <summary>The normalized Jira key (e.g., "FHIR-43499", "JF-1234", "GF-5678").</summary>
    public required string JiraKey { get; set; }

    /// <summary>Text surrounding the reference for context display.</summary>
    public required string? Context { get; set; }
}
