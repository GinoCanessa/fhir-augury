using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Zulip.Database.Records;

/// <summary>Links a Zulip thread (StreamName + Topic) to a Jira ticket key, aggregated from message-level references.</summary>
[LdgSQLiteTable("zulip_thread_tickets")]
[LdgSQLiteIndex(nameof(JiraKey))]
[LdgSQLiteIndex(nameof(StreamName), nameof(Topic))]
[LdgSQLiteIndex(nameof(JiraKey), nameof(StreamName), nameof(Topic))]
public partial record class ZulipThreadTicketRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary>Stream name (denormalized for query convenience).</summary>
    public required string StreamName { get; set; }

    /// <summary>Topic name (thread identifier within stream).</summary>
    public required string Topic { get; set; }

    /// <summary>The normalized Jira key.</summary>
    public required string JiraKey { get; set; }

    /// <summary>Number of messages in this thread that reference this ticket.</summary>
    public required int ReferenceCount { get; set; }

    /// <summary>Earliest message timestamp that references this ticket in this thread.</summary>
    public required DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>Latest message timestamp that references this ticket in this thread.</summary>
    public required DateTimeOffset LastSeenAt { get; set; }
}
