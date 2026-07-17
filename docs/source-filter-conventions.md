# Source Filter and Selection List Conventions

Source services use the **null-as-default, empty-as-explicit-all** convention for list-shaped string filters in API contracts and list-shaped ingestion selection options.

- `null` or an absent JSON/config key means use the field's documented default behavior.
- `[]` means the caller/operator explicitly supplied no restriction for query filters, or an explicit empty override for ingestion selection lists.
- Non-empty lists restrict or select exactly the listed values.
- Include-style and exclude-style filters follow the same three-state rule.
- Query filters do not define a match-nothing sentinel; callers that want no results should not call the endpoint.

For API query filters with no per-field default, `null` and `[]` both add no SQL/query predicate. For ingestion options with defaults, `null` preserves the default and `[]` opts out of that default.

## Source notes

- Jira query and local-processing filters follow the convention. `Jira.Projects = null` falls back to `DefaultProject`; `Jira.Projects = []` disables project ingestion.
- Zulip `StreamNames` and `SenderNames` query filters follow the convention. Numeric stream/sender ID filters are unchanged.
- Confluence `Spaces = null` uses `FHIR`, `FHIRI`, and `SOA`; `Spaces = []` ingests no spaces.
- GitHub repository lists and file-content list options follow the convention. Defaulted repository and ignore-pattern lists use defaults only when the config key is absent or null.

Operator configs that currently use `[]` on defaulted ingestion lists to mean "use defaults" should remove the key or set it to `null`.
