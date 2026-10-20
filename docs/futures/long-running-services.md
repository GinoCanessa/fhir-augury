# Long-running Augury services

This is a forward-looking, non-binding roadmap for continuously operated
deployments, not an approved runtime design or delivery commitment.
**Existing context** describes current foundations; the operating stories
and horizons describe **candidate capabilities**; implementation choices
remain **deferred decisions**. Maintainers, operators, cache producers, and
UI users can use it to discuss what would become useful and what evidence
would justify separately scoped proposals.

## Existing context

Augury already has independent HTTP source services and an Orchestrator
that aggregates across sources
([Architecture (v2)](../../README.md#architecture-v2)).
[Source Service Architecture](../technical/architecture.md#source-service-architecture)
identifies four ingesting sources: `Source.Jira`, `Source.Zulip`,
`Source.Confluence`, and `Source.GitHub`, each with its own SQLite store and
file-system cache. Their
[ingestion pipeline](../technical/architecture.md#ingestion-pipeline-per-source-service)
supports scheduled and on-demand ingestion. `Source.Fhir` serves read-only
FHIR specification reference data; these stories give it no ingestion
obligation.

Jira specifically distinguishes its successful upstream-refresh watermark
from its local content revision. Only error-free upstream full or
incremental ingestion advances that watermark; cache rebuilds do not.
This source-specific provenance behavior is not a general runtime guarantee.

The trusted-local Dev UI's
[operations workspace](../technical/architecture.md#operations-workspace-and-local-artifacts)
already distinguishes successful observations from retained stale values
and their original success times during subsequent loading or failure.
Those observations go through the Orchestrator. They do not establish
continuous ticket-view refresh, upstream freshness, or suitability for an
exposed production UI.

Future views preserve
[service-owned stores](../technical/architecture.md#sqlite-per-service-not-shared)
and the existing
[HTTP client boundary](../technical/architecture.md#mcp-cli-and-dev-ui-as-http-clients):
UI/CLI/MCP → Orchestrator → owning source's HTTP API. The Orchestrator
remains the only cross-source aggregator; clients neither open live source
databases nor call sources directly.

## Operating stories

Content received, content applied, upstream currency, and displayed
information answer different questions. They are not proposed fields or
processing states. Both intake paths need validation/provenance and
progress/failure visibility; update identity and interruption connect them
to continued reads and views. Access control and backup/restore enter with
the first update, not as optional later hardening. These stories describe
producer intent and source-owned application without assigning endpoints,
queues, workers, or new Orchestrator responsibilities.

### Manifest-requested bucket retrieval

For cache producers and update-requesting automation,
**manifest-requested cache retrieval** would let a producer submit a
manifest requesting retrieval of available cache updates from a bucket
without resupplying the content. Callers and operators would gain
understandable progress and outcomes across retrieval, validation,
application, and failure, including what usable data resulted.

A retrieved object is not necessarily acceptable or applied content.
Missing or inaccessible objects, an unauthorized producer, incompatible
cache content, or uncertain provenance could undermine the outcome.
Externally prepared content may also be older or less complete than the
upstream system; successful application alone cannot establish upstream
currency. Readers might still be using older data while an update is being
considered or applied.

Evaluation needs representative cache and provenance examples, producer
authority and access boundaries, compatibility criteria, and an account of
what must be protected for backup and restoration. It must connect update
identity to direct uploads: the same content might arrive by either route
or overlap another update. Provider, transfer protocol, and manifest
semantics remain unselected. The interruption story considers failed
retrieval or application alongside continued access to the last usable data.

### Direct update upload

For producers without a bucket workflow,
**direct update upload with comparable outcome visibility** would let
them supply content directly and understand transfer, validation,
application, and failure without arranging remote retrieval. It is a
first-class intake candidate, not a fallback whose visibility or
protections can be omitted.

Receiving bytes does not establish their origin, integrity, compatibility,
or application to the intended source. A partial transfer or unacceptable
content could leave the producer unsure what happened. The same update
could also arrive through bucket retrieval, or overlap a newer change;
neither arrival order nor a lost response settles update identity. An
upload acknowledgment must not become a claim that tickets are current or
that an open view now displays them.

Follow-up needs representative producer workflows and content to evaluate
acceptable formats, validation, transfer limits, partial-transfer handling,
and who may supply updates for which source. No upload API or numeric limit
is specified. Both intakes need decisions about duplicate and overlapping
content, evidence available to operators, and protection of last usable
data for continued reads and backup/restore. The interruption story applies
equally; direct delivery does not remove recovery ambiguity.

### A UI left open as tickets change

Ticket reviewers and UI maintainers would benefit from
**service-backed views that remain useful as tickets arrive or change**.
The candidate experience helps users understand fresh or stale data,
updating views, unavailable data, and unknown freshness rather than
interpreting every successful read as proof of current tickets.

Observation time says when a read happened, not how current its contents
are. A recently read view might show data predating a bucket request or
upload; content already applied by an owner might not yet be displayed.
An update in progress says little about the currency of the visible
tickets. A mixed-source view can combine different freshness evidence or
unknowns, so one successful source read cannot justify an all-current
impression. Retaining a useful older view during an update or failed read
could help users continue working, but hiding its age or uncertainty would
mislead them.

Evaluation needs operator and user descriptions of acceptable stale or
unavailable views, mixed-source examples, and evidence that explanations
refer to the displayed data. Access policy must cover both data and
operational details shown to the intended audience. Restored older data
belongs in these examples from the start. No polling/push choice, refresh
interval, or UI stack is selected.

### Interrupted operation across handover or restart

Operators taking over a deployment would benefit from
**understandable update continuity**: accounting for outstanding, failed,
or ambiguous work beyond the original caller's session, while relating
known outcomes to the last usable data and what readers can still use.

A bucket request or upload can outlive its response. A lost reply leaves
application uncertain; a repeated request may duplicate content or
overlap another update. Interruption during retrieval differs from
interruption during application, and restarting a service does not itself
explain either outcome. The operator needs to understand these differences
without assuming that resubmission is harmless or that continued reads
necessarily see a complete change.

Backup/restore may recover older data than a previous caller or open UI
observed. A newly available service or recent successful read would not
make that restored content newer. Evaluation must consider the relationship
between restored data, provenance, outstanding work, and mixed-source
views, as well as who may inspect updates or authorize recovery actions.

Separately authorized failure/recovery work could examine lost responses,
duplicates, overlap, interrupted retrieval/application, restart, and older
restores for both intakes. Operators' descriptions of acceptable continued,
stale, or unavailable reads would guide the choices. This proposes no
automatic retry, reconciliation, consistency, or recovery promise;
processor durability does not establish recovery for these cache updates.

## Provisional roadmap

The horizons order confidence gained, not implementation phases or
releases. Neither intake has precedence. Every capability remains a
candidate, and advancement depends on evidence and separately scoped
decisions. Basic access protection and backup/recovery considerations are
dependencies from Near onward, not benefits reserved for Later.

| Horizon | Named candidate capability | Motivating story | Dependencies | Rationale | Advancement evidence or decisions |
|---------|----------------------------|------------------|--------------|-----------|-----------------------------------|
| Near — understand one change | Manifest-requested retrieval and direct upload with basic progress/failure and outcome visibility; honest UI freshness. | Manifest-requested bucket retrieval; Direct update upload; A UI left open as tickets change. | Cache boundaries, producer trust/provenance, update identity, meanings of usable/current data, initial access and backup scope. | Understand what one change made usable without confusing receipt, application, upstream currency, and display; evaluate both intakes equally. | Representative cache/provenance examples; validation and access decisions; operator descriptions of acceptable stale or unavailable views. |
| Next — remain understandable through repetition or failure | Update continuity through repeated/overlapping changes and restart recovery; continued reads and views useful over time. | Interrupted operation across handover or restart, connecting both intakes and the open UI. | Earlier outcome and freshness distinctions; decisions about ambiguous outcomes, consistency, access, retained evidence, and recovery. | One successful transfer says little about another change, an interruption, or restoration of older data. | Findings from separately authorized failure/recovery work covering duplicate or overlapping updates, lost replies, restart, and restore; decisions on acceptable continued, stale, or unavailable reads. |
| Later — reduce routine supervision | Optional changed-ticket cues in an open view and exception-focused summaries of outstanding or failed updates. | A UI left open as tickets change; Interrupted operation across handover or restart. | Evidence from repeated updates/recovery, demonstrated operator needs, and established freshness and access decisions. | Reduce routine checking only where cues and summaries help users act without hiding uncertainty; these are not committed product requirements. | Operator examples of missed relevant changes or overlooked work; evidence that proposed cues and summaries would remain useful and honest through repetition and recovery. |

## Deferred decisions and follow-up

Later, separately scoped proposals must settle relevant choices with
evidence: content formats and manifest semantics; provider and transfer;
scheduling, concurrency, and retry; freshness and consistency; access
policy and audience; hosting; retention; backup/restore scope and recovery
objectives; capacity; and UI refresh behavior. Listing a choice neither
selects a mechanism nor approves implementation.

Evidence named here is suggested input for separately authorized
follow-up, not a report of completed validation. This document calls for
no live data collection or runtime experiments as part of its preparation.

## Non-goals

This roadmap implements no intake endpoint, background work, UI refresh,
authentication, or recovery behavior. It changes no application,
configuration, deployment, service state, or data. It selects no new API,
provider, manifest/transport contract, hosting topology, database
replacement, or UI product. It is not a migration plan, production
runbook, or release commitment, and does not expand unrelated source
integrations or processor features.
