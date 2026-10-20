using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using FhirAugury.Source.Jira.Api;

namespace FhirAugury.Source.Jira.Ingestion;

internal enum JiraPeopleFieldState { Missing, Absent, Present, Malformed, IdentityConflict, Conflict }
internal enum JiraPeopleFormat { Xml, Json }

// These types never cross the HTTP boundary. In particular, a migrated user row
// and the legacy issue role strings cannot be constructed into observations.
internal sealed record JiraPersonObservation(
    JiraPeopleFieldState State,
    string? Identity = null,
    string? ExplicitName = null);

internal sealed record JiraRequesterObservation(
    JiraPeopleFieldState State,
    IReadOnlyList<JiraPersonObservation> People);

internal sealed record JiraPeopleObservation(
    string? Key,
    string? UpdatedAt,
    JiraPersonObservation Reporter,
    JiraPersonObservation Assignee,
    JiraRequesterObservation Requesters,
    string Digest = "");

internal sealed record JiraRawPeopleObservation(string Body, JiraPeopleFormat Format, string? Origin);

internal sealed record JiraPeopleEvidence(
    string SourceFingerprint,
    bool IsUpstream,
    IReadOnlyDictionary<string, IReadOnlyList<JiraPeopleObservation>> Observations,
    IReadOnlyDictionary<string, string> Failures);

internal sealed record JiraPeopleRevisionObservation(JiraPeopleObservation? Observation, string? Failure);

/// <summary>
/// Reads explicit raw observations, without ingestion defaults or identity
/// guessing. Origin is supplied only by JiraSource's authenticated acquisition
/// or a matching source-written acquisition receipt.
/// </summary>
internal static partial class JiraPublicPeopleObservationReader
{
    internal const int CurrentVersion = 1;

    [GeneratedRegex(@"\A[A-Z][A-Z0-9_]*(?:-[A-Z0-9_]+)*-[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex TicketKeyPattern();

    [GeneratedRegex(@"(?:[zZ]|[+-]\d{2}:?\d{2}|GMT|UTC)$", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitOffsetPattern();

    internal static bool IsTicketKey(string? key) =>
        key is { Length: > 2 and <= 128 } && TicketKeyPattern().IsMatch(key);

    internal static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static bool TryRevision(string? value, out DateTimeOffset revision)
    {
        revision = default;
        return !string.IsNullOrWhiteSpace(value)
            && ExplicitOffsetPattern().IsMatch(value.Trim())
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out revision)
            && revision != DateTimeOffset.MinValue;
    }

    internal static JiraPeopleEvidence Read(
        IEnumerable<JiraRawPeopleObservation> responses,
        IReadOnlyList<string> keys,
        string sourceFingerprint,
        bool upstream)
    {
        HashSet<string> selected = new(keys, StringComparer.Ordinal);
        Dictionary<string, List<JiraPeopleObservation>> observations = new(StringComparer.Ordinal);
        Dictionary<string, string> failures = new(StringComparer.Ordinal);
        bool unknownOrigin = false;
        foreach (JiraRawPeopleObservation response in responses)
        {
            if (!string.Equals(response.Origin, sourceFingerprint, StringComparison.Ordinal)
                || string.IsNullOrEmpty(response.Origin))
            {
                unknownOrigin = true;
                continue;
            }

            try
            {
                IReadOnlyList<JiraPeopleObservation> parsed = response.Format == JiraPeopleFormat.Xml
                    ? JiraXmlParser.ReadPublicPeopleObservations(response.Body)
                    : JiraFieldMapper.ReadPublicPeopleObservations(response.Body);
                string digest = Digest($"{CurrentVersion}\n{response.Origin}\n{response.Format}\n{response.Body}");
                foreach (JiraPeopleObservation observation in parsed)
                {
                    if (observation.Key is null || !selected.Contains(observation.Key))
                        continue;
                    if (!observations.TryGetValue(observation.Key, out List<JiraPeopleObservation>? values))
                        observations[observation.Key] = values = [];
                    values.Add(observation with { Digest = digest });
                }
            }
            catch (Exception ex) when (ex is JsonException or XmlException or InvalidDataException)
            {
                // Never include the parser exception: it can quote raw evidence.
                foreach (string key in keys)
                    failures[key] = JiraPublicPeopleCodes.MalformedValue;
            }
        }

        foreach (string key in keys.Where(key => !observations.ContainsKey(key)))
        {
            failures.TryAdd(key, unknownOrigin
                ? JiraPublicPeopleCodes.UnknownCacheOrigin
                : JiraPublicPeopleCodes.MissingObservation);
        }
        return new(sourceFingerprint, upstream,
            observations.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<JiraPeopleObservation>)pair.Value.ToArray(), StringComparer.Ordinal),
            failures);
    }

    internal static JiraPeopleRevisionObservation ReadForRevision(
        JiraPeopleEvidence evidence, string key, string storedRevision)
    {
        if (evidence.Failures.TryGetValue(key, out string? failure))
            return new(null, failure);
        if (!TryRevision(storedRevision, out DateTimeOffset revision))
            return new(null, JiraPublicPeopleCodes.MissingRevision);
        if (!evidence.Observations.TryGetValue(key, out IReadOnlyList<JiraPeopleObservation>? values))
            return new(null, JiraPublicPeopleCodes.MissingObservation);

        List<JiraPeopleObservation> matching = [];
        bool missingRevision = false;
        foreach (JiraPeopleObservation value in values)
        {
            if (!TryRevision(value.UpdatedAt, out DateTimeOffset observed))
            {
                missingRevision = true;
                continue;
            }
            if (observed == revision)
                matching.Add(value);
            else if (evidence.IsUpstream)
                return new(null, JiraPublicPeopleCodes.RevisionMismatch);
        }
        if (missingRevision)
            return new(null, JiraPublicPeopleCodes.MissingRevision);
        if (matching.Count == 0)
            return new(null, JiraPublicPeopleCodes.RevisionMismatch);

        JiraPeopleObservation merged = matching[0];
        foreach (JiraPeopleObservation value in matching.Skip(1))
        {
            merged = merged with
            {
                Reporter = MergePerson(merged.Reporter, value.Reporter),
                Assignee = MergePerson(merged.Assignee, value.Assignee),
                Requesters = MergeRequesters(merged.Requesters, value.Requesters),
            };
        }
        // Every raw digest participates, including equivalent offset encodings.
        merged = merged with
        {
            Digest = Digest(string.Join("\n", matching.Select(value => value.Digest)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))),
        };
        return new(merged, null);
    }

    private static JiraPersonObservation MergePerson(JiraPersonObservation left, JiraPersonObservation right)
    {
        if (left.State == JiraPeopleFieldState.Missing) return right;
        if (right.State == JiraPeopleFieldState.Missing) return left;
        if (left == right) return left;
        if (left.State == JiraPeopleFieldState.Present && right.State == JiraPeopleFieldState.Present
            && left.Identity is not null && left.Identity == right.Identity
            && (left.ExplicitName is null || right.ExplicitName is null || left.ExplicitName == right.ExplicitName))
        {
            return left with { ExplicitName = left.ExplicitName ?? right.ExplicitName };
        }
        return new(JiraPeopleFieldState.Conflict);
    }

    private static JiraRequesterObservation MergeRequesters(JiraRequesterObservation left, JiraRequesterObservation right)
    {
        if (left.State == JiraPeopleFieldState.Missing) return right;
        if (right.State == JiraPeopleFieldState.Missing) return left;
        if (left.State == right.State && left.People.SequenceEqual(right.People)) return left;
        if (left.State != JiraPeopleFieldState.Present || right.State != JiraPeopleFieldState.Present
            || left.People.Count != right.People.Count)
            return new(JiraPeopleFieldState.Conflict, []);

        JiraPersonObservation[] a = left.People.OrderBy(value => value.Identity, StringComparer.Ordinal).ToArray();
        JiraPersonObservation[] b = right.People.OrderBy(value => value.Identity, StringComparer.Ordinal).ToArray();
        JiraPersonObservation[] merged = a.Zip(b, MergePerson).ToArray();
        return merged.Any(value => value.State == JiraPeopleFieldState.Conflict)
            ? new(JiraPeopleFieldState.Conflict, [])
            : new(JiraPeopleFieldState.Present, merged);
    }

    internal static JiraPersonObservation Person(string? identity, string? explicitName)
    {
        if (identity is not null &&
            (string.IsNullOrWhiteSpace(identity) || identity != identity.Trim() || identity.Any(char.IsControl)))
            return new(JiraPeopleFieldState.Malformed);
        return new(JiraPeopleFieldState.Present, identity, explicitName);
    }

    internal static string? FieldReason(JiraPeopleFieldState state) => state switch
    {
        JiraPeopleFieldState.Missing => JiraPublicPeopleCodes.MissingField,
        JiraPeopleFieldState.Absent => JiraPublicPeopleCodes.RoleAbsent,
        JiraPeopleFieldState.Malformed => JiraPublicPeopleCodes.MalformedValue,
        JiraPeopleFieldState.IdentityConflict => JiraPublicPeopleCodes.IdentityConflict,
        JiraPeopleFieldState.Conflict => JiraPublicPeopleCodes.ConflictingObservations,
        _ => null,
    };
}
