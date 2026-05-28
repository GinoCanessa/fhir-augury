using FhirAugury.Server.Terminology.Configuration;
using FhirAugury.Server.Terminology.Models;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Options;

namespace FhirAugury.Server.Terminology.Matching;

/// <summary>
/// Converts a parsed FHIR <see cref="CodeSystem"/> or
/// <see cref="ValueSet"/> into a <see cref="NormalizedSubmission"/>
/// that the matchers consume. Enforces the
/// <c>MaxSubmissionConcepts</c> cap.
/// </summary>
public sealed class SubmissionNormalizer
{
    private readonly int _conceptCap;

    public SubmissionNormalizer(IOptions<TerminologyServiceOptions> options)
    {
        _conceptCap = options.Value.MaxSubmissionConcepts;
    }

    /// <summary>For test wiring only.</summary>
    internal SubmissionNormalizer(int conceptCap)
    {
        _conceptCap = conceptCap;
    }

    public NormalizedSubmission Normalize(CodeSystem cs, string fhirVersion)
    {
        ArgumentNullException.ThrowIfNull(cs);

        string url = cs.Url ?? string.Empty;
        NormalizedSubmission sub = new()
        {
            Kind = "CodeSystem",
            FhirVersion = fhirVersion,
            CanonicalUrl = string.IsNullOrWhiteSpace(url) ? null : url,
            CanonicalUrlNormalized = string.IsNullOrWhiteSpace(url)
                ? null
                : TerminologyTextNormalizer.NormalizeCanonicalUrl(url),
            Version = cs.Version,
            Title = cs.Title,
            Name = cs.Name,
            Description = cs.Description,
            Purpose = cs.Purpose,
        };

        foreach (CodeSystem.ConceptDefinitionComponent c in cs.Concept ?? [])
        {
            FlattenCs(c, url, sub.Concepts);
            EnforceCap(sub.Concepts.Count);
        }

        return sub;
    }

    public NormalizedSubmission Normalize(ValueSet vs, string fhirVersion)
    {
        ArgumentNullException.ThrowIfNull(vs);

        string url = vs.Url ?? string.Empty;
        NormalizedSubmission sub = new()
        {
            Kind = "ValueSet",
            FhirVersion = fhirVersion,
            CanonicalUrl = string.IsNullOrWhiteSpace(url) ? null : url,
            CanonicalUrlNormalized = string.IsNullOrWhiteSpace(url)
                ? null
                : TerminologyTextNormalizer.NormalizeCanonicalUrl(url),
            Version = vs.Version,
            Title = vs.Title,
            Name = vs.Name,
            Description = vs.Description,
            Purpose = vs.Purpose,
        };

        if (vs.Compose is not null)
        {
            foreach (ValueSet.ConceptSetComponent inc in vs.Compose.Include ?? [])
            {
                string system = inc.System ?? string.Empty;
                foreach (ValueSet.ConceptReferenceComponent c in inc.Concept ?? [])
                {
                    sub.Concepts.Add(new NormalizedConcept(
                        SystemUrl: system,
                        Code: c.Code ?? string.Empty,
                        Display: c.Display,
                        DisplayNormalized: TerminologyTextNormalizer.NormalizeDisplay(c.Display ?? string.Empty)));
                    EnforceCap(sub.Concepts.Count);
                }
            }
        }

        return sub;
    }

    private void FlattenCs(
        CodeSystem.ConceptDefinitionComponent c,
        string system,
        List<NormalizedConcept> sink)
    {
        sink.Add(new NormalizedConcept(
            SystemUrl: system,
            Code: c.Code ?? string.Empty,
            Display: c.Display,
            DisplayNormalized: TerminologyTextNormalizer.NormalizeDisplay(c.Display ?? string.Empty)));

        foreach (CodeSystem.ConceptDefinitionComponent child in c.Concept ?? [])
        {
            FlattenCs(child, system, sink);
        }
    }

    private void EnforceCap(int currentCount)
    {
        if (currentCount > _conceptCap)
        {
            throw new SubmissionTooLargeException(_conceptCap, currentCount);
        }
    }
}
