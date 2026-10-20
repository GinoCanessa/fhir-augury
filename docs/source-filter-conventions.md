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
- Confluence `Spaces = null` discovers every non-archived global space on the instance; `Spaces = []` ingests no spaces.
- GitHub repository lists and file-content list options follow the convention. Defaulted repository and ignore-pattern lists use defaults only when the config key is absent or null.

Operator configs that currently use `[]` on defaulted ingestion lists to mean "use defaults" should remove the key or set it to `null`.

## Jira specification exactness

Jira specification values are passed unchanged to exact, parameterized `IN`
predicates. Ordinary spaces (`U+0020`) and non-breaking spaces (`U+00A0`) are
distinct inputs; specification filters do not normalize one into the other.
This differs from the native contains-LIKE label-text selection described
below. For the shipped Planner restriction and correctly nested local
overrides, see [Planner Jira filter repair](configuration.md#planner-jira-filter-repair).

## Jira label-text selection

For the FHIR Preparer and Planner, `Processing.Jira.LabelsToInclude` and
`Processing.Jira.LabelsToExclude` correspond to the selection API fields
`LabelText.Includes` and `LabelText.Excludes`. They have no defaults. Each
list is independently inactive when absent, `null`, empty, or all blank.
Null, empty, and whitespace-only entries are ignored; all other values
are preserved, including surrounding whitespace.

These criteria match raw nullable `Labels` text, not the existing exact
`Labels` filter. Each value is parameterized as `%<value>%` for native
SQLite contains-LIKE matching. `%` and `_` remain wildcards, native case
behavior applies, and values are not escaped, normalized, or tokenized.

| Active label-text groups | Stored `NULL` labels | Non-null label text |
|-|-|-|
| Neither | Pass | Pass |
| Includes only | Fail | Match at least one inclusion |
| Excludes only | Pass | Match none of the exclusions |
| Both | Fail | Match at least one inclusion and no exclusion |

A null **list** means inactive, not stored `NULL`. Non-null empty text is
distinct: `LabelText.Excludes = ["%"]` accepts stored `NULL` but rejects
every non-null value, including `""`. Any exclusion match on non-null
text overrides inclusion.

Each active group is ANDed with inherited filters and any `Keys`
restriction, including for null labels. For example, with exclusion
patterns bound as `%<value>%`:

```sql
WHERE Status IN (@status) AND Key IN (@key)
  AND (Labels IS NULL OR
       (Labels NOT LIKE @exclude0 AND Labels NOT LIKE @exclude1))
```

Existing exact-value filters and their list conventions are unchanged.
