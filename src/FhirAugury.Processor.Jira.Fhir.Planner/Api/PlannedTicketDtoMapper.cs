using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Api;

internal static class PlannedTicketDtoMapper
{
    public static PlannedTicketSummaryDto ToDto(PlannedTicketSummary s)
        => new(s.Key, s.Resolution, s.ResolutionSummary, s.FeatureProposal, s.DesignRationale, s.SavedAt);

    public static PlannedTicketDetailDto ToDto(PlannedTicketDetail d)
        => new(
            ToDto(d.Ticket),
            d.Repos.Select(r => new PlannedTicketRepoDto(r.RepoKey, r.RepoRevision, r.Justification)).ToArray(),
            d.RepoChanges.Select(c => new PlannedTicketRepoChangeDto(
                c.Id, c.TicketRepoId, c.RepoKey, c.ChangeSequence, c.FilePath, c.ChangeTitle, c.ChangeDescription,
                c.SourceLineStart, c.SourceLineEnd, c.ReplacementLines, c.Reason)).ToArray(),
            d.RepoImpacts.Select(i => new PlannedTicketRepoImpactDto(i.TicketRepoId, i.RepoKey, i.TicketRepoChangeId, i.AffectedFilePath, i.HowAffected)).ToArray(),
            d.ChangeValidations.Select(v => new PlannedTicketChangeValidationDto(v.TicketRepoId, v.RepoKey, v.ValidationSequence, v.Action)).ToArray(),
            d.TestingConsiderations.Select(t => new PlannedTicketTestingConsiderationDto(t.TicketRepoId, t.RepoKey, t.ConsiderationSequence, t.Consideration)).ToArray(),
            d.OpenQuestions.Select(q => new PlannedTicketOpenQuestionDto(q.TicketRepoId, q.RepoKey, q.QuestionSequence, q.Question)).ToArray());

    public static PlannedJiraHydrationDisplayDto ToDto(PlannedJiraHydrationRow r)
        => new(r.IssueKey, r.JiraKey, r.Title, r.Status, r.Type, r.Priority, r.Resolution, r.ResolutionDescriptionPlain,
            r.WorkGroup, r.WorkGroupClean, r.Specification, r.UpdatedAt, r.Url, r.HydratedAt, r.HydrationStatus, r.HydrationReason);

    public static PlannedTicketTopicGroupingResponse ToDto(PlannedTicketTopicsForCategory c)
        => new(c.WorkGroupClean, c.WorkGroupDisplay, c.Specification, c.Type, c.SavedAt,
            c.Topics.Select(t => new PlannedTicketTopicDto(
                t.Id, t.ShortDescription, t.LongerDescription, t.RenderOrderHint, t.SpannedRepos,
                t.LinkedTicketGroups.Select(g => new PlannedTicketTopicGroupDto(
                    g.FirstTicketKey, g.Rationale,
                    g.Members.Select(m => new PlannedTicketTopicGroupMemberDto(m.TicketKey, m.Order)).ToArray())).ToArray(),
                t.RemainingTicketKeys)).ToArray());

    public static PlannedTicketQueryFilter ToFilter(this PlannedTicketQueryRequest req)
        => new(req.Repo, req.AffectedFilePath, req.RelatedJiraKey, Math.Clamp(req.Limit, 1, 500), Math.Max(0, req.Offset));

    public static PlannedTicketTopicGroupingPayload ToPayload(this PlannedTicketTopicGroupingRequest req)
        => new()
        {
            WorkGroupClean = req.WorkGroupClean,
            WorkGroupDisplay = req.WorkGroupDisplay,
            Specification = req.Specification,
            Type = req.Type,
            Topics = req.Topics.Select(t => new PlannedTicketTopicPayload
            {
                ShortDescription = t.ShortDescription,
                LongerDescription = t.LongerDescription,
                RenderOrderHint = t.RenderOrderHint,
                SpannedRepos = t.SpannedRepos,
                LinkedTicketGroups = t.LinkedTicketGroups.Select(g => new PlannedTicketTopicGroupPayload
                {
                    FirstTicketKey = g.FirstTicketKey,
                    Rationale = g.Rationale,
                    Members = g.Members.Select(m => new PlannedTicketTopicGroupMemberPayload
                    {
                        TicketKey = m.TicketKey,
                        Order = m.Order,
                    }).ToList(),
                }).ToList(),
                RemainingTicketKeys = t.RemainingTicketKeys,
            }).ToList(),
        };
}
