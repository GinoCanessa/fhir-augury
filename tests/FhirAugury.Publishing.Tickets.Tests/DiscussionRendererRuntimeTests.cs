using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Client;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Publishing.Tickets.Tests;

public sealed class DiscussionRendererRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"discussion-renderer-runtime-{Guid.NewGuid():N}");

    public DiscussionRendererRuntimeTests()
        => Directory.CreateDirectory(_root);

    public void Dispose()
        => TestFileCleanup.SafeDeleteDirectory(_root);

    [Theory]
    [InlineData("#/")]
    [InlineData("#/by-workgroup")]
    [InlineData("#/list")]
    public Task FacetClicks_PreserveOverviewCrosscutAndListRoutes(string route)
        => RunRuntimeAsync(
            """
            const beforeRoute = routePath();
            const beforeTitle = document.title;
            assert.equal(matchingCount(), 3);
            click(facet('wg', 'value:FHIR Infrastructure'));
            assert.equal(routePath(), beforeRoute);
            assert.equal(document.title, beforeTitle);
            assert.equal(heading.textContent, presentation.siteName);
            assert.equal(matchingCount(), 2);
            assert.deepEqual(chipValues('wg'), ['value:FHIR Infrastructure']);
            assert.deepEqual(chipValues('impact'), ['value:Non-substantive']);
            assertDimensionHidden('wg');
            assert.equal(document.activeElement, chip('wg', 'value:FHIR Infrastructure'));
            if (beforeRoute === '') {
              assert.deepEqual(facetCounts('type'), {
                'value:Change Request': 1,
                'value:Technical Correction': 1
              });
              assert.deepEqual(facetCounts('project'), { 'value:FHIR': 2 });
              assert.equal(facetCounts('artifact')['value:Observation'], 2);
            } else if (beforeRoute === 'by-workgroup') {
              assert.ok(byClass(main, 'filter-recovery'));
              assert.equal(byClass(main, 'crosscut-card'), null);
            }
            if (beforeRoute !== 'list') click(byClass(main, 'show-ticket-list'));
            assert.equal(routePath(), 'list');
            assert.deepEqual(ticketKeys(), ['FHIR-1001', 'FHIR-1002']);
            assertDimensionHidden('wg');
            assertDimensionHidden('impact');
            assert.ok(tableHeaders(ticketTable()).includes('Project'));
            for (const row of tableRows(ticketTable())) {
              const links = all(row, node => node.tagName === 'a');
              assert.equal(links.length, 1, 'Only the key is a detail link.');
              assert.equal(links[0].href,
                '#/ticket/' + links[0].textContent + querySuffix());
              assert.ok(all(row, node => hasClass(node, 'ticket-facet-value')).length);
            }
            """,
            route + "?impactKey=value%3ANon-substantive");

    [Theory]
    [InlineData("#/")]
    [InlineData("#/by-workgroup")]
    [InlineData("#/list")]
    public Task ChipRemoval_PreservesOtherFiltersAndRestoresVisibility(string route)
        => RunRuntimeAsync(
            """
            const beforeRoute = routePath();
            assert.equal(matchingCount(), 2);
            removeChip('artifact', 'value:Observation');
            assert.equal(matchingCount(), 3);
            assert.deepEqual(chipValues('wg'), ['value:FHIR Infrastructure', '__unknown__']);
            assert.deepEqual(chipValues('impact'), ['value:Non-substantive']);
            removeChip('wg', 'value:FHIR Infrastructure');
            assert.equal(matchingCount(), 1);
            assert.deepEqual(chipValues('wg'), ['__unknown__']);
            assertDimensionHidden('wg');
            assert.equal(document.activeElement, chip('wg', '__unknown__'));
            removeChip('wg', '__unknown__');
            assert.equal(matchingCount(), 3);
            assertDimensionVisible('wg');
            assertDimensionHidden('impact');
            assert.deepEqual(chipValues('impact'), ['value:Non-substantive']);
            if (beforeRoute !== 'list') {
              assert.deepEqual(facetCounts('wg'), {
                'value:FHIR Infrastructure': 2,
                '__unknown__': 1
              });
            }
            removeChip('impact', 'value:Non-substantive');
            assert.equal(matchingCount(), 4);
            assertDimensionVisible('wg');
            assert.equal(routePath(), beforeRoute);
            assert.equal(all(main, node => hasClass(node, 'chip-remove')).length, 0);
            assert.equal(document.activeElement, byClass(main, 'view-heading'));
            """,
            route + "?wgKey=value%3AFHIR%20Infrastructure&wgKey=__unknown__" +
            "&artifactKey=value%3AObservation&impactKey=value%3ANon-substantive");

    [Fact]
    public async Task SingletonProject_UsesExportedNotFilteredCorpus()
    {
        await RunRuntimeAsync(
            """
            assert.equal(presentation.corpusSummary.exportedProjectCount, 2);
            click(facet('wg', 'value:FHIR Infrastructure'));
            assert.equal(matchingCount(), 2);
            assert.deepEqual(facetCounts('project'), { 'value:FHIR': 2 });
            click(byClass(main, 'show-ticket-list'));
            assert.ok(tableHeaders(ticketTable()).includes('Project'));
            navigate('#/by-project' + querySuffix());
            assert.deepEqual(facetCounts('project'), { 'value:FHIR': 2 });
            click(facet('project', 'value:FHIR'));
            assert.equal(routePath(), 'by-project');
            assertDimensionHidden('project');
            removeChip('project', 'value:FHIR');
            assertDimensionVisible('project');
            assert.equal(matchingCount(), 2);
            assert.deepEqual(chipValues('wg'), ['value:FHIR Infrastructure']);
            """);

        TicketSnapshotFixture singleton = await TicketSnapshotFixture.CreatePreparerAsync(
            _root, schemaVersion: PreparedTicketSnapshotSchemaV3.Version);
        await RunRuntimeAsync(
            """
            assert.equal(presentation.corpusSummary.exportedProjectCount, 1);
            assert.equal(presentation.corpusSummary.ticketCount, 1);
            assertDimensionHidden('project');
            assert.equal(hook.query('SELECT Project FROM tickets', null).rows[0].Project, 'FHIR');
            navigate('#/by-project');
            assert.equal(matchingCount(), 1);
            assertDimensionHidden('project');
            assert.match(byClass(main, 'filter-recovery').textContent, /one project/);
            click(byClass(main, 'show-ticket-list'));
            assertDimensionHidden('project');
            click(facet('type', 'value:Change Request'));
            removeChip('type', 'value:Change Request');
            assertDimensionVisible('type');
            assertDimensionHidden('project');
            """,
            fixture: singleton);
    }

    [Fact]
    public Task GenerationFilters_AreFixedAndCannotExpandExport()
        => RunRuntimeAsync(
            """
            assert.equal(presentation.corpusSummary.ticketCount, 2);
            assert.equal(presentation.corpusSummary.exportedProjectCount, 1);
            const fixed = all(main, node => hasClass(node, 'filter-chip-fixed'));
            assert.equal(fixed.length, 3);
            for (const node of fixed) {
              assert.match(node.textContent, /fixed at publication/);
              assert.equal(all(node, child => child.tagName === 'button').length, 0);
            }
            ['project', 'wg', 'spec'].forEach(assertDimensionHidden);
            click(facet('type', 'value:Technical Correction'));
            assert.equal(matchingCount(), 1);
            removeChip('type', 'value:Technical Correction');
            assert.equal(matchingCount(), 2);
            ['project', 'wg', 'spec'].forEach(assertDimensionHidden);
            assert.equal(querySuffix(), '');
            navigate('#/?projectKey=value%3ACDS');
            assert.equal(matchingCount(), 2);
            assert.equal(hook.query('SELECT COUNT(*) AS Count FROM tickets', null).rows[0].Count, 2);
            removeChip('project', 'value:CDS');
            assertDimensionHidden('project');
            assert.equal(all(main, node => hasClass(node, 'filter-chip-fixed')).length, 3);
            click(byClass(main, 'show-ticket-list'));
            ['project', 'wg', 'spec'].forEach(assertDimensionHidden);
            assert.deepEqual(ticketKeys(), ['FHIR-1001', 'FHIR-1002']);
            """,
            filters: new TicketSiteFilters(
                Specification: "FHIR", Project: "FHIR", WorkGroup: "FHIR Infrastructure"));

    [Fact]
    public Task FilterChanges_PreserveSearchSortAndHistory()
        => RunRuntimeAsync(
            """
            click(sortButton(crosscutTable('type'), 'Type'));
            assert.deepEqual(sortState(crosscutTable('type')), ['Type', 'descending']);
            click(facet('wg', 'value:FHIR Infrastructure'));
            assert.deepEqual(sortState(crosscutTable('type')), ['Type', 'descending']);
            assert.deepEqual(facetOrder('type'), ['value:Technical Correction', 'value:Change Request']);
            removeChip('wg', 'value:FHIR Infrastructure');
            assert.deepEqual(sortState(crosscutTable('type')), ['Type', 'descending']);

            navigate('#/by-type');
            assert.deepEqual(sortState(crosscutTable('type')), ['Type', 'ascending']);
            click(sortButton(crosscutTable('type'), 'Count'));
            click(sortButton(crosscutTable('type'), 'Count'));
            click(facet('type', 'value:Technical Correction'));
            assertDimensionHidden('type');
            removeChip('type', 'value:Technical Correction');
            assert.deepEqual(sortState(crosscutTable('type')), ['Count', 'descending']);
            navigate('#/');
            assert.deepEqual(sortState(crosscutTable('type')), ['Type', 'descending']);

            navigate('#/list');
            const listTitle = document.title;
            click(sortButton(ticketTable(), 'Title'));
            assert.deepEqual(ticketKeys(), ['FHIR-1002', 'CDS-2002', 'CDS-2001', 'FHIR-1001']);
            typeSearch('fixture');
            flushDebounces();
            assert.deepEqual(ticketKeys(), ['FHIR-1002', 'CDS-2002']);
            assert.equal(byClass(main, 'list-row-count').textContent, '2 of 4');
            click(facet('wg', 'value:FHIR Infrastructure'));
            assert.equal(document.title, listTitle);
            assert.equal(searchInput().value, 'fixture');
            assert.deepEqual(ticketKeys(), ['FHIR-1002']);
            assert.deepEqual(sortState(ticketTable()), ['Title', 'ascending']);
            history.back();
            flushRoutes();
            assert.equal(routePath(), 'list');
            assert.deepEqual(chipValues('wg'), []);
            assert.deepEqual(ticketKeys(), ['FHIR-1002', 'CDS-2002']);
            history.forward();
            flushRoutes();
            assert.deepEqual(chipValues('wg'), ['value:FHIR Infrastructure']);
            assert.deepEqual(ticketKeys(), ['FHIR-1002']);

            navigate('#/list');
            typeSearch('');
            flushDebounces();
            click(sortButton(ticketTable(), 'Type'));
            click(sortButton(ticketTable(), 'Type'));
            assert.deepEqual(sortState(ticketTable()), ['Type', 'descending']);
            assert.deepEqual(ticketKeys(), ['CDS-2002', 'FHIR-1002', 'CDS-2001', 'FHIR-1001']);
            click(facet('type', 'value:Change Request'));
            assertDimensionHidden('type');
            assert.deepEqual(sortState(ticketTable()), ['Key', 'ascending']);
            assert.deepEqual(ticketKeys(), ['CDS-2001', 'FHIR-1001']);
            removeChip('type', 'value:Change Request');
            assert.deepEqual(sortState(ticketTable()), ['Type', 'descending']);
            assert.deepEqual(ticketKeys(), ['CDS-2002', 'FHIR-1002', 'CDS-2001', 'FHIR-1001']);

            const oldTable = ticketTable();
            const oldInput = searchInput();
            typeSearch('Alpha');
            const pending = Array.from(timers.values()).find(timer => timer.delay === 150);
            assert.ok(pending, 'The real input handler must schedule a debounce.');
            const cancellations = cancelledDebounces;
            click(facet('wg', 'value:FHIR Infrastructure'));
            assert.ok(cancelledDebounces > cancellations);
            assert.equal(searchInput().value, 'Alpha');
            assert.deepEqual(ticketKeys(), ['FHIR-1002']);
            pending.callback();
            oldInput.value = 'CDS';
            oldInput.dispatchEvent({ type: 'input' });
            flushDebounces();
            assert.equal(tableRows(oldTable).length, 4, 'Detached table must not be updated.');
            assert.equal(searchInput().value, 'Alpha');
            assert.deepEqual(ticketKeys(), ['FHIR-1002']);
            assert.deepEqual(sortState(ticketTable()), ['Type', 'descending']);

            const listHash = location.hash;
            const homeLink = all(breadcrumb, node => node.tagName === 'a')
              .find(node => node.textContent === presentation.siteName);
            click(homeLink);
            assert.equal(routePath(), '');
            assert.deepEqual(chipValues('wg'), ['value:FHIR Infrastructure']);
            history.back();
            flushRoutes();
            assert.equal(location.hash, listHash);
            assert.equal(searchInput().value, 'Alpha');
            assert.deepEqual(ticketKeys(), ['FHIR-1002']);
            history.forward();
            flushRoutes();
            assert.equal(routePath(), '');
            assert.equal(matchingCount(), 2);

            navigate('#/topics');
            const topicTable = () => first(main, node => node.tagName === 'table' &&
              node.getAttribute('aria-label') === 'Discussion topics');
            typeSearch('renderer');
            flushDebounces();
            click(sortButton(topicTable(), 'Topic'));
            assert.equal(tableRows(topicTable()).length, 1);
            navigate('#/topics?wgKey=value%3Amissing');
            assert.equal(searchInput().value, 'renderer');
            assert.equal(tableRows(topicTable()).length, 0);
            assert.deepEqual(sortState(topicTable()), ['Topic', 'ascending']);
            removeChip('wg', 'value:missing');
            assert.equal(searchInput().value, 'renderer');
            assert.equal(tableRows(topicTable()).length, 1);
            click(first(topicTable(), node => node.tagName === 'a'));
            assert.equal(routePath(), 'topic/topic-renderer');
            const groupTable = byClass(main, 'grouped-ticket-table');
            assert.deepEqual(tableRows(groupTable).map(row => row.firstChild.textContent),
              ['FHIR-1001', 'CDS-2001']);
            assert.match(main.textContent, /Discuss together/);
            navigate(listHash);
            assert.equal(searchInput().value, 'Alpha', 'Topic search must not replace ticket-list search.');
            assert.deepEqual(sortState(ticketTable()), ['Type', 'descending']);
            typeSearch('no local search matches');
            flushDebounces();
            assert.equal(tableRows(ticketTable()).length, 0);
            assert.ok(chip('wg', 'value:FHIR Infrastructure'));
            assert.ok(searchInput());
            typeSearch('');
            flushDebounces();
            assert.equal(tableRows(ticketTable()).length, 2);
            """);

    [Fact]
    public Task EmptyExport_KeepsFixedFiltersAndExplainsRecovery()
        => RunRuntimeAsync(
            """
            assert.equal(presentation.corpusSummary.ticketCount, 0);
            assert.equal(presentation.corpusSummary.dateCoverage, 'empty');
            assert.equal(matchingCount(), 0);
            assert.match(byClass(main, 'empty-state').textContent, /generate a new site/);
            assert.equal(all(main, node => hasClass(node, 'filter-chip-fixed')).length, 2);
            assert.equal(all(main, node => hasClass(node, 'chip-remove')).length, 0);
            click(byClass(main, 'show-ticket-list'));
            assert.equal(tableRows(ticketTable()).length, 0);
            assert.ok(searchInput());
            assert.match(byClass(main, 'empty-state').textContent, /generate a new site/);
            assertDimensionHidden('project');
            assertDimensionHidden('spec');
            """,
            filters: new TicketSiteFilters(Specification: "CDS Hooks", Project: "FHIR"));

    [Theory]
    [InlineData("#/")]
    [InlineData("#/by-workgroup")]
    [InlineData("#/list")]
    public Task NoMatch_KeepsRecoveryControls(string route)
        => RunRuntimeAsync(
            """
            const beforeRoute = routePath();
            assert.equal(matchingCount(), 0);
            assert.match(byClass(main, 'empty-state').textContent, /No tickets match/);
            assert.ok(chip('artifact', 'value:missing-value'));
            if (beforeRoute === 'list') {
              assert.ok(searchInput());
              assert.equal(tableRows(ticketTable()).length, 0);
              assert.equal(byClass(main, 'list-row-count').textContent, '0');
            } else {
              assert.ok(byClass(main, 'show-ticket-list'));
              assert.deepEqual(facetCounts('wg'), {});
            }
            removeChip('artifact', 'value:missing-value');
            assert.equal(routePath(), beforeRoute);
            assert.equal(matchingCount(), 4);
            assert.equal(byClass(main, 'empty-state'), null);
            assertDimensionVisible('wg');
            """,
            route + "?artifactKey=value%3Amissing-value");

    [Fact]
    public Task UnknownAndLegacyFacets_MergeDistinctValuesWithoutLosingTheRoute()
        => RunRuntimeAsync(
            """
            assert.equal(facetCounts('artifact')['value:Observation'], 3);
            assert.equal(facetCounts('artifact')['__unknown__'], 1);
            assert.equal(facetCounts('impact')['value:Non-substantive'], 3);
            assert.equal(facetCounts('impact')['value:Compatible, substantive'], 3);
            const observation = facet('artifact', 'value:Observation');
            const patient = facet('artifact', 'value:Patient');
            observation.click();
            patient.click();
            flushRoutes();
            assert.equal(matchingCount(), 3, 'Overlapping multivalued selections count distinct tickets.');
            assert.deepEqual(chipValues('artifact'), ['value:Observation', 'value:Patient']);
            assert.deepEqual(facetCounts('impact'), {
              'value:Compatible, substantive': 2, 'value:Non-substantive': 2
            });
            removeChip('artifact', 'value:Patient');
            assert.equal(matchingCount(), 3);
            removeChip('artifact', 'value:Observation');
            const infrastructure = facet('wg', 'value:FHIR Infrastructure');
            const unknownGroup = facet('wg', '__unknown__');
            infrastructure.click();
            unknownGroup.click();
            infrastructure.click();
            flushRoutes();
            assert.equal(routePath(), '');
            assert.deepEqual(chipValues('wg'), ['value:FHIR Infrastructure', '__unknown__']);
            assert.equal(matchingCount(), 3);
            assert.deepEqual(facetCounts('type'), {
              'value:Change Request': 2, 'value:Technical Correction': 1
            });
            removeChip('wg', 'value:FHIR Infrastructure');
            removeChip('wg', '__unknown__');
            click(facet('artifact', '__unknown__'));
            assert.equal(matchingCount(), 1);
            removeChip('artifact', '__unknown__');
            click(facet('artifact', 'value:(unknown)'));
            assert.equal(matchingCount(), 1);
            removeChip('artifact', 'value:(unknown)');
            click(facet('artifact', 'value:__unknown__'));
            assert.equal(matchingCount(), 1);
            assert.ok(chipValues('artifact')[0].toLowerCase().startsWith('value:'));

            navigate('#/by-artifact/' + encodeURIComponent('__unknown__') +
              '?wg=FHIR%20Infrastructure');
            assert.equal(routePath(), 'list', 'Only a legacy value route redirects to the list.');
            assert.deepEqual(ticketKeys(), ['FHIR-1001']);
            assert.equal(chipValues('artifact')[0].toLowerCase(), 'value:__unknown__');
            navigate('#/by-workgroup/' + encodeURIComponent('FHIR Infrastructure') +
              '?wgKey=__unknown__&impact=Non-substantive');
            assert.equal(routePath(), 'list');
            assert.deepEqual(chipValues('wg'), ['__unknown__', 'value:FHIR Infrastructure']);
            assert.equal(matchingCount(), 3);
            assert.deepEqual(chipValues('impact'), ['value:Non-substantive']);
            """);

    [Fact]
    public Task RenderedAndCopiedProposalLabels_PreserveBodies()
        => RunRuntimeAsync(
            "const expected = " + JsonSerializer.Serialize(TicketSnapshotFixture.DiscussionRuntimeBodies) + ";\n" +
            """
            click(all(ticketTable(), node => node.tagName === 'a')
              .find(node => node.textContent === 'FHIR-1001'));
            const sections = [
              ['Original request', 'RequestPlain'],
              ['Proposed / accepted resolution', 'ResolutionPlain'],
              ['Request Summary', 'RequestSummary'],
              ['Comment Summary', 'CommentSummary'],
              ['Linked Ticket Summary', 'LinkedTicketSummary'],
              ['Related Ticket Summary', 'RelatedTicketSummary'],
              ['Related Zulip Summary', 'RelatedZulipSummary'],
              ['Related GitHub Summary', 'RelatedGitHubSummary'],
              ['Existing Proposed', 'ExistingProposed'],
              ['Proposal A: Accept as requested', 'ProposalA'],
              ['Proposal A: Accept as requested — Justification', 'ProposalAJustification'],
              ['Proposal A: Accept as requested — Impact', 'ProposalAImpact'],
              ['Proposal B: Accept modified', 'ProposalB'],
              ['Proposal B: Accept modified — Justification', 'ProposalBJustification'],
              ['Proposal B: Accept modified — Impact', 'ProposalBImpact'],
              ['Proposal C: Reject', 'ProposalC'],
              ['Proposal C: Reject — Justification', 'ProposalCJustification'],
              ['Recommendation', 'Recommendation'],
              ['Recommendation — Justification', 'RecommendationJustification']
            ];
            const ticket = hook.query("SELECT * FROM tickets WHERE Key = 'FHIR-1001'", null).rows[0];
            for (const [title, field] of sections) {
              assert.equal(ticket[field], expected[field], 'Frozen source body: ' + field);
              const section = authoredSection(title);
              assert.equal(first(section, node => node.tagName === 'pre').textContent,
                expected[field], 'Rendered body: ' + field);
            }
            assert.equal(authoredSection('Proposal C: Reject — Impact', false), null);
            assert.equal(fieldText('Reporter'), 'No public display name available');
            assert.equal(fieldText('Assignee'), 'No public display name available');
            assert.match(byClass(main, 'people-guidance').textContent, /Jira source.*refresh publication metadata/);
            assert.match(fieldText('In-person requesters'), /Trimmed Safe/);
            assert.doesNotMatch(fieldText('Reporter') + fieldText('Assignee'), /unassigned/i);

            click(byClass(header, 'copy-ai'));
            await Promise.resolve();
            assert.ok(copiedMarkdown, 'The actual Copy for AI click must write the clipboard.');
            for (const [title, field] of sections) {
              const marker = '## ' + title + '\n\n';
              const start = copiedMarkdown.indexOf(marker);
              assert.ok(start >= 0, 'Copied heading: ' + title);
              const bodyStart = start + marker.length;
              assert.equal(copiedMarkdown.slice(bodyStart, bodyStart + expected[field].length),
                expected[field], 'Copied body (including whitespace): ' + field);
              assert.equal(copiedMarkdown.slice(bodyStart + expected[field].length,
                bodyStart + expected[field].length + 2), '\n\n');
            }
            assert.ok(!copiedMarkdown.includes('## Proposal C: Reject — Impact'));
            assert.match(copiedMarkdown, /Retained source-backed analysis/);
            assert.match(copiedMarkdown, /5 messages.*unresolved: Zulip lookup failed/);
            """,
            "#/list?wgKey=value%3AFHIR%20Infrastructure");

    [Fact]
    public Task RelatedItems_RenderSafeRepositoryAndZulipAnchors()
        => RunRuntimeAsync(
            """
            const activeSuffix = '?' + new URLSearchParams(querySuffix().slice(1)).toString();
            const related = plain(hook.query(
              "SELECT * FROM related_items WHERE TicketKey = 'FHIR-1001'", null).rows);
            for (const item of related) {
              const rendered = relatedItem(item.Kind, item.ItemKey, item.LinkType);
              const link = first(rendered, node => node.tagName === 'a');
              if (item.ItemKey === 'FHIR-1002' && item.Kind === 'jira') {
                assert.equal(link.href, '#/ticket/FHIR-1002' + activeSuffix);
                assert.equal(link.getAttribute('target'), null);
              } else if (item.Url) {
                assertExternal(link, item.Url);
              } else {
                assert.equal(link, null);
                assert.match(rendered.textContent, /URL unavailable/);
                assert.ok(rendered.textContent.includes(item.Label));
              }
            }
            assertExternal(first(relatedItem('repo', 'HL7/api-incubator-ig'), node => node.tagName === 'a'),
              'https://github.com/HL7/api-incubator-ig');
            assertExternal(first(relatedItem('zulip', '153858681'), node => node.tagName === 'a'),
              'https://chat.fhir.org/#narrow/channel/179166-implementers/topic/Retained.20topic/near/153858681');
            assertExternal(first(relatedItem('zulip', 'legacy-zero'), node => node.tagName === 'a'),
              'https://chat.fhir.org/#narrow/channel/1/topic/Zero%20messages');
            const linkedSources = byClass(authoredSection('Linked Ticket Summary'), 'summary-source-list');
            const internalSource = all(linkedSources, node => node.tagName === 'a')
              .find(node => node.textContent === 'FHIR-1002');
            assert.equal(internalSource.href, '#/ticket/FHIR-1002' + activeSuffix);
            const proseLink = all(authoredSection('Request Summary'), node => node.tagName === 'a')
              .find(node => node.textContent === 'FHIR-1002');
            assert.equal(proseLink.href, '#/ticket/FHIR-1002' + activeSuffix);

            for (const url of [null, '', 'javascript:alert(1)', 'data:text/html,unsafe',
              'mailto:person@example.org', '//example.org', 'ftp://example.org', 'https://',
              'https://example.org/\nunsafe']) {
              assert.equal(sandbox.DiscussionComponents.createExternalAnchor(url, '<literal>'), null);
              const section = sandbox.DiscussionComponents.createSummarySection('Unsafe fixture', 'Authored body', [{
                Label: '<literal>', Url: url, HydrationStatus: 'unresolved',
                HydrationReason: 'Fixture lookup failed'
              }]);
              assert.equal(all(section, node => node.tagName === 'a').length, 0);
              assert.match(section.textContent, /URL (unavailable|omitted)/);
              assert.match(section.textContent, /Fixture lookup failed/);
              assert.match(section.textContent, /<literal>/);
            }
            for (const url of ['http://example.test/a%2Fb?q=a%20b#c',
              'https://example.test/a path with spaces',
              'https://example.test/topic/already.2520encoded?view=compact']) {
              assertExternal(sandbox.DiscussionComponents.createExternalAnchor(url, 'Source'), url);
            }
            click(first(relatedItem('jira', 'FHIR-1002'), node => node.tagName === 'a'));
            assert.equal(routePath(), 'ticket/FHIR-1002');
            assert.deepEqual(chipValues('wg'), ['value:FHIR Infrastructure']);
            """,
            "#/ticket/FHIR-1001?wgKey=value%3AFHIR%20Infrastructure");

    [Fact]
    public Task ZulipSourceListsAndSidebar_ShowTheSameFailureQualification()
        => RunRuntimeAsync(
            """
            const summary = authoredSection('Related Zulip Summary');
            const sources = byClass(summary, 'summary-source-list');
            const rows = sources.childNodes.filter(node => node.tagName === 'li');
            assert.equal(rows.length, 4, 'Join must not include another ticket or another kind with the same key.');
            for (const [key, qualification, excluded] of [
              ['153858681', 'Last-known source-backed link and context retained', 'Unverified link'],
              ['legacy-zero', 'Unverified link and context retained', 'Last-known source-backed']
            ]) {
              const source = rows.find(node => node.getAttribute('data-source-key') === key);
              const sidebar = relatedItem('zulip', key);
              const summaryDiagnostic = byClass(source, 'link-diagnostic').textContent;
              const sidebarDiagnostic = byClass(sidebar, 'link-diagnostic').textContent;
              assert.equal(summaryDiagnostic, sidebarDiagnostic);
              assert.match(summaryDiagnostic, /unresolved: Zulip lookup failed: reference not found/);
              assert.ok(summaryDiagnostic.includes(qualification));
              assert.ok(!summaryDiagnostic.includes(excluded));
              assert.ok(!summaryDiagnostic.includes('zulip-reference-v'));
              assert.ok(!summaryDiagnostic.includes('"backing"'));
              assertExternal(first(source, node => node.tagName === 'a'),
                first(sidebar, node => node.tagName === 'a').href);
            }
            const unavailableSource = rows.find(node => node.getAttribute('data-source-key') === 'bad-reference');
            assert.equal(byClass(unavailableSource, 'link-diagnostic').textContent,
              byClass(relatedItem('zulip', 'bad-reference'), 'link-diagnostic').textContent);
            assert.match(unavailableSource.textContent, /malformed thread id/);
            assert.match(unavailableSource.textContent, /URL unavailable/);
            const resolvedSource = rows.find(node => node.getAttribute('data-source-key') === 'thread-1');
            assert.equal(byClass(resolvedSource, 'link-diagnostic'), null);
            assert.equal(first(summary, node => node.tagName === 'pre').textContent,
              hook.query("SELECT RelatedZulipSummary FROM tickets WHERE Key = 'FHIR-1001'", null).rows[0].RelatedZulipSummary);
            """,
            "#/ticket/FHIR-1001");

    [Fact]
    public async Task ResolvedZulipDetailDiagnostics_RemainVisibleAndCopyable()
    {
        TicketSnapshotFixture fixture = await TicketSnapshotFixture.CreateDiscussionRuntimeAsync(_root);
        await using (SqliteConnection connection = new($"Data Source={fixture.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE prepared_zulip_hydration SET HydrationReason = @reason
                WHERE TicketKey = 'FHIR-1001' AND ZulipThreadId = 'thread-1'
                """;
            command.Parameters.AddWithValue("@reason", ZulipReferenceHydrationReason.Serialize(new()
            {
                Backing = ZulipReferenceBacking.LegacyIndexedContext,
                LatestOutcome = ZulipReferenceLookupOutcome.Resolved,
                Diagnostics = [ZulipReferenceDiagnosticCode.InvalidTimestamp],
            }));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await fixture.RefreshDescriptorHashAsync();
        await RunRuntimeAsync(
            """
            const sidebar = relatedItem('zulip', 'thread-1');
            const sources = byClass(authoredSection('Related Zulip Summary'), 'summary-source-list');
            const source = sources.childNodes.find(node => node.getAttribute('data-source-key') === 'thread-1');
            const sidebarDiagnostic = byClass(sidebar, 'link-diagnostic').textContent;
            assert.equal(sidebarDiagnostic, byClass(source, 'link-diagnostic').textContent);
            assert.match(sidebarDiagnostic, /invalid optional source timestamp/);
            assert.doesNotMatch(sidebarDiagnostic, /unresolved/);
            assertExternal(first(source, node => node.tagName === 'a'),
              first(sidebar, node => node.tagName === 'a').href);
            click(byClass(header, 'copy-ai'));
            await Promise.resolve();
            assert.match(copiedMarkdown, /Zulip diagnostic: invalid optional source timestamp/);
            """,
            "#/ticket/FHIR-1001",
            fixture: fixture);
    }

    [Fact]
    public async Task PeopleAndCoverage_RemainSeparateFromReadiness()
    {
        await RunRuntimeAsync(
            """
            const coverage = byClass(main, 'corpus-coverage');
            const readiness = byClass(main, 'readiness-notice');
            assert.ok(coverage);
            assert.ok(readiness);
            assert.ok(!coverage.contains(readiness) && !readiness.contains(coverage));
            assert.match(coverage.textContent, /4 tickets across 2 projects/);
            assert.match(coverage.textContent, /4 of 4 tickets \(complete coverage\)/);
            assert.match(coverage.textContent, /Reporter 1 of 4; Assignee 0 of 4; in-person requesters 1 of 4/);
            assert.match(coverage.textContent, /retained safe links with unresolved lookups/);
            const frozenCoverage = coverage.textContent;
            const frozenTitle = document.title;
            click(facet('wg', 'value:FHIR Infrastructure'));
            assert.equal(byClass(main, 'corpus-coverage').textContent, frozenCoverage);
            assert.equal(document.title, frozenTitle);
            navigate('#/ticket/FHIR-1002');
            const reason = presentation.readiness.reasons.find(item => item.code ===
              hook.query("SELECT UnavailableReason FROM ticket_people WHERE TicketKey = 'FHIR-1002' AND Role = 'reporter'", null)
                .rows[0].UnavailableReason);
            assert.ok(reason);
            assert.equal(fieldText('Reporter'), reason.message);
            assert.equal(fieldText('Assignee'), reason.message);
            navigate('#/ticket/CDS-2002');
            assert.equal(fieldText('In-person requesters'), 'No public display name available');
            assert.ok(byClass(main, 'people-guidance'));
            """);

        TicketSnapshotFixture legacy = await TicketSnapshotFixture.CreatePreparerAsync(
            _root, schemaVersion: PreparedTicketSnapshotSchemaV1.Version);
        await RunRuntimeAsync(
            """
            const unavailable = hook.query(
              "SELECT UnavailableReason FROM ticket_people WHERE Role = 'reporter'", null).rows[0];
            const reason = presentation.readiness.reasons.find(item => item.code === unavailable.UnavailableReason);
            assert.ok(reason);
            assert.equal(fieldText('Reporter'), reason.message);
            assert.equal(fieldText('Assignee'), reason.message);
            assert.equal(fieldText('In-person requesters'), reason.message);
            assert.doesNotMatch(main.textContent, /Legacy Reporter|Legacy Assignee|Ada Lovelace|Grace Hopper/);
            """,
            "#/ticket/FHIR-1001",
            fixture: legacy);

        TicketSnapshotFixture ready = await TicketSnapshotFixture.CreatePreparerAsync(
            _root,
            schemaVersion: PreparedTicketSnapshotSchemaV3.Version);
        await ready.SetTicketPublicDisplayNamesAsync("FHIR-1001", null, null);
        await ready.AttachValidPublicationRefreshProofAsync(
            new DateTimeOffset(2026, 9, 14, 17, 15, 0, TimeSpan.Zero),
            sourceContentRevision: 9001);
        await RunRuntimeAsync(
            """
            assert.equal(presentation.readiness.isReady, true);
            assert.equal(byClass(main, 'readiness-notice'), null);
            assert.ok(byClass(main, 'corpus-coverage'));
            assert.match(byClass(main, 'people-coverage').textContent, /Reporter 0 of 1/);
            navigate('#/ticket/FHIR-1001');
            assert.equal(fieldText('Reporter'), 'No public display name available');
            assert.ok(byClass(main, 'people-guidance'));
            """,
            fixture: ready);
    }

    [Fact]
    public Task EmittedRuntimeExecutesRealListRouteAndFacetClick()
        => RunRuntimeAsync(
            """
            assert.deepEqual(ticketKeys(), ['FHIR-1001', 'FHIR-1002']);
            click(facet('type', 'value:Change Request'));
            assert.equal(routePath(), 'list');
            assert.deepEqual(ticketKeys(), ['FHIR-1001']);
            assertDimensionHidden('type');
            assertDimensionHidden('wg');
            assertDimensionHidden('impact');
            const prose = new NodeStub('pre');
            sandbox.DiscussionComponents.appendJiraLinkedText(prose, 'See ballot-77 and UP-9, not FHIR-1suffix.');
            const links = all(prose, node => node.tagName === 'a');
            assert.equal(links.length, 2);
            assertExternal(links[0], 'https://jira.hl7.org/browse/BALLOT-77');
            assertExternal(links[1], 'https://jira.hl7.org/browse/UP-9');
            let observed = null;
            const table = sandbox.DiscussionComponents.createSortableTable({
              rows: [{ Count: 2 }, { Count: 10 }],
              initialSort: { key: 'count', direction: 'ascending' },
              onSortChanged: value => { observed = value; },
              columns: [{
                key: 'count', label: 'Count', className: 'count-column',
                value: row => row.Count, compare: 'numeric'
              }]
            });
            assert.deepEqual(plain(table.getSortState()), { key: 'count', direction: 'ascending' });
            sortButton(table.element, 'Count').click();
            assert.deepEqual(plain(observed), { key: 'count', direction: 'descending' });
            assert.deepEqual(plain(table.getSortState()), plain(observed));
            assert.deepEqual(tableRows(table.element).map(row => row.textContent), ['10', '2']);
            const th = first(table.element, node => node.tagName === 'th');
            const td = first(table.element, node => node.tagName === 'td');
            assert.equal(th.scope, 'col');
            assert.ok(hasClass(th, 'count-column') && hasClass(td, 'count-column'));
            """,
            "#/list?wgKey=value%3AFHIR%20Infrastructure&impactKey=value%3ANon-substantive");

    private async Task RunRuntimeAsync(
        string assertions,
        string initialHash = "#/",
        TicketSiteFilters? filters = null,
        TicketSnapshotFixture? fixture = null)
    {
        fixture ??= await TicketSnapshotFixture.CreateDiscussionRuntimeAsync(_root);
        VerifiedAuthoringSnapshotPair pair = await fixture.CreateVerifiedPairAsync("Preparer");
        byte[] sourceDatabase = await File.ReadAllBytesAsync(pair.DatabasePath);
        byte[] sourceDescriptor = await File.ReadAllBytesAsync(pair.DescriptorPath);
        string output = Path.Combine(_root, $"site-{Guid.NewGuid():N}");
        await new TicketSitePublisher().PublishAsync(new TicketSitePublishRequest(
            pair, TicketSiteKind.Discussion, output, "Runtime discussion", filters));

        string runner = Path.Combine(_root, $"renderer-runtime-{Guid.NewGuid():N}.cjs");
        await File.WriteAllTextAsync(runner, RuntimeHarness + "\n" +
            """
            (async function () {
              assert.ok(onReady, 'Runtime initialization must be registered.');
              await onReady();
              assertNoRuntimeError();
              assert.ok(hook, 'The emitted runtime must initialize the real embedded SQL.js database.');
            """ + "\n" + assertions + "\n" +
            """
              assertNoRuntimeError();
              process.stdout.write('passed');
            })().catch(error => {
              console.error(error && error.stack || error);
              process.exitCode = 1;
            }).finally(() => {
              for (const id of timers.keys()) clearTimeout(id);
            });
            """);

        ProcessStartInfo startInfo = new()
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(runner);
        startInfo.ArgumentList.Add(Path.Combine(output, "discussion"));
        startInfo.ArgumentList.Add(initialHash);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Node.js.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        string standardOutput = await outputTask;
        string standardError = await errorTask;
        Assert.True(process.ExitCode == 0,
            $"Discussion renderer runtime probe failed:{Environment.NewLine}{standardError}");
        Assert.Equal("passed", standardOutput);
        Assert.Equal(sourceDatabase, await File.ReadAllBytesAsync(pair.DatabasePath));
        Assert.Equal(sourceDescriptor, await File.ReadAllBytesAsync(pair.DescriptorPath));
    }

    // This event/DOM model executes emitted assets, not browser layout or native focus behavior.
    private const string RuntimeHarness = """
        const fs = require('node:fs');
        const path = require('node:path');
        const vm = require('node:vm');
        const assert = require('node:assert/strict');
        const site = process.argv[2];
        const initialHash = process.argv[3];
        const html = fs.readFileSync(path.join(site, 'index.html'), 'utf8');
        function capture(pattern, label) {
          const match = html.match(pattern);
          assert.ok(match, label + ' was not emitted.');
          return match[1];
        }
        const presentationJson = capture(
          /<script id="site-presentation" type="application\/json">([\s\S]*?)<\/script>/, 'presentation');
        const presentation = JSON.parse(presentationJson);
        const version = capture(/assets\/state\.js\?v=([^"]+)"/, 'asset version');
        const plain = value => JSON.parse(JSON.stringify(value));

        class NodeStub {
          constructor(tag, text) {
            this.tagName = String(tag || '').toLowerCase();
            this.childNodes = [];
            this.parentNode = null;
            this.className = '';
            this.attributes = Object.create(null);
            this.listeners = Object.create(null);
            this.style = {};
            this.value = '';
            this.hidden = false;
            this._text = '';
            if (text != null) this.textContent = text;
          }
          get firstChild() { return this.childNodes[0] || null; }
          get textContent() {
            return this.tagName === '#text' ? this._text :
              this.childNodes.map(child => child.textContent).join('');
          }
          set textContent(value) {
            for (const child of this.childNodes) child.parentNode = null;
            this.childNodes = [];
            this._text = value == null ? '' : String(value);
            if (this.tagName !== '#text' && this._text) {
              this.appendChild(new NodeStub('#text', this._text));
            }
          }
          appendChild(child) {
            assert.ok(child instanceof NodeStub, 'Only DOM nodes may be appended.');
            if (child.parentNode) child.parentNode.removeChild(child);
            child.parentNode = this;
            this.childNodes.push(child);
            return child;
          }
          removeChild(child) {
            const index = this.childNodes.indexOf(child);
            assert.ok(index >= 0, 'Child must belong to its parent.');
            this.childNodes.splice(index, 1);
            if (child.contains(document.activeElement)) document.activeElement = document.body;
            child.parentNode = null;
            return child;
          }
          setAttribute(name, value) {
            this.attributes[name] = String(value);
            if (name === 'class') this.className = String(value);
            if (name === 'hidden') this.hidden = true;
          }
          getAttribute(name) {
            if (name === 'class') return this.className || null;
            return Object.prototype.hasOwnProperty.call(this.attributes, name) ?
              this.attributes[name] : null;
          }
          addEventListener(name, callback) {
            (this.listeners[name] || (this.listeners[name] = [])).push(callback);
          }
          dispatchEvent(event) {
            event.target = this;
            event.defaultPrevented = false;
            event.preventDefault = function () { this.defaultPrevented = true; };
            for (const callback of this.listeners[event.type] || []) callback(event);
            return !event.defaultPrevented;
          }
          click() {
            this.focus();
            if (this.dispatchEvent({ type: 'click' }) &&
                this.tagName === 'a' && this.href.startsWith('#')) location.hash = this.href;
          }
          focus() { document.activeElement = this; }
          contains(node) {
            return Boolean(node && (node === this || this.childNodes.some(child => child.contains(node))));
          }
          querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
          querySelectorAll(selector) {
            const parts = selector.trim().split(/\s+/);
            return all(this, node => {
              if (node === this || !matches(node, parts[parts.length - 1])) return false;
              let ancestor = node.parentNode;
              for (let index = parts.length - 2; index >= 0; index--) {
                while (ancestor && !matches(ancestor, parts[index])) ancestor = ancestor.parentNode;
                if (!ancestor) return false;
                ancestor = ancestor.parentNode;
              }
              return true;
            });
          }
        }
        for (const attribute of ['href', 'target', 'rel', 'id', 'scope', 'type']) {
          Object.defineProperty(NodeStub.prototype, attribute, {
            get() { return this.getAttribute(attribute) || ''; },
            set(value) { this.setAttribute(attribute, value); }
          });
        }
        function hasClass(node, name) {
          return Boolean(node && String(node.className || '').split(/\s+/).includes(name));
        }
        function matches(node, selector) {
          if (selector.startsWith('.')) return hasClass(node, selector.slice(1));
          if (selector.startsWith('#')) return node.id === selector.slice(1);
          return node.tagName === selector;
        }
        function all(root, predicate, result = []) {
          if (!root) return result;
          if (predicate(root)) result.push(root);
          for (const child of root.childNodes) all(child, predicate, result);
          return result;
        }
        function first(root, predicate) { return all(root, predicate)[0] || null; }
        function byClass(root, name) { return first(root, node => hasClass(node, name)); }

        const main = new NodeStub('main');
        main.id = 'app';
        const header = new NodeStub('header');
        const heading = new NodeStub('h1', presentation.siteName);
        const breadcrumb = new NodeStub('nav');
        breadcrumb.id = 'breadcrumb';
        header.appendChild(heading);
        header.appendChild(breadcrumb);
        const body = new NodeStub('body');
        body.appendChild(header);
        body.appendChild(main);
        const presentationNode = new NodeStub('script', presentationJson);
        presentationNode.id = 'site-presentation';
        body.appendChild(presentationNode);
        let onReady = null;
        const document = {
          readyState: 'loading', title: presentation.siteName, body, activeElement: body,
          getElementById: id => first(body, node => node.id === id),
          querySelector: selector => body.querySelector(selector),
          addEventListener: (name, callback) => { if (name === 'DOMContentLoaded') onReady = callback; },
          createElement: tag => new NodeStub(tag),
          createTextNode: text => new NodeStub('#text', String(text))
        };
        const windowListeners = Object.create(null);
        const routeEvents = [];
        const entries = [initialHash];
        let historyIndex = 0;
        let currentHash = initialHash;
        const location = {};
        function changeHash(next, push) {
          if (next === currentHash) return;
          const previous = currentHash;
          if (push) {
            entries.splice(historyIndex + 1);
            entries.push(next);
            historyIndex++;
          }
          currentHash = next;
          routeEvents.push({ type: 'hashchange', oldURL: previous, newURL: next });
        }
        Object.defineProperty(location, 'hash', {
          get: () => currentHash,
          set: value => changeHash(String(value), true)
        });
        const history = {
          back() { if (historyIndex > 0) changeHash(entries[--historyIndex], false); },
          forward() { if (historyIndex < entries.length - 1) changeHash(entries[++historyIndex], false); }
        };
        function assertNoRuntimeError() {
          const errors = all(main, node => hasClass(node, 'error'));
          assert.equal(errors.length, 0, errors.map(node => node.textContent).join('\n'));
        }
        function flushRoutes() {
          let dispatched = 0;
          while (routeEvents.length) {
            assert.ok(++dispatched < 20, 'Route changes must settle.');
            const event = routeEvents.shift();
            for (const callback of windowListeners.hashchange || []) callback(event);
          }
          assertNoRuntimeError();
        }
        function navigate(hash) { location.hash = hash; flushRoutes(); }
        function click(node) { assert.ok(node, 'The requested control must be rendered.'); node.click(); flushRoutes(); }
        function routePath() { return currentHash.replace(/^#\/?/, '').split('?')[0]; }
        function querySuffix() { const index = currentHash.indexOf('?'); return index < 0 ? '' : currentHash.slice(index); }
        function chipValues(dimension) {
          return new URLSearchParams(querySuffix().slice(1)).getAll(dimension + 'Key');
        }
        function facet(dimension, key) {
          const result = first(main, node =>
            (hasClass(node, 'crosscut-row') || hasClass(node, 'ticket-facet-value')) &&
            node.getAttribute('data-dimension') === dimension &&
            node.getAttribute('data-value-key').toLowerCase() === key.toLowerCase());
          assert.ok(result, 'Missing facet control: ' + dimension + '/' + key);
          return result;
        }
        function chip(dimension, key) {
          const result = first(main, node => hasClass(node, 'chip-remove') &&
            node.getAttribute('data-dimension') === dimension &&
            node.getAttribute('data-value-key').toLowerCase() === key.toLowerCase());
          assert.ok(result, 'Missing removable chip: ' + dimension + '/' + key);
          return result;
        }
        function removeChip(dimension, key) { click(chip(dimension, key)); }
        function crosscut(dimension) {
          return first(main, node => hasClass(node, 'crosscut-card') &&
            node.getAttribute('data-dimension') === dimension);
        }
        function crosscutTable(dimension) {
          const table = first(crosscut(dimension), node => node.tagName === 'table');
          assert.ok(table, 'Missing crosscut table: ' + dimension);
          return table;
        }
        function tableRows(table) { return first(table, node => node.tagName === 'tbody').childNodes; }
        function facetOrder(dimension) {
          return tableRows(crosscutTable(dimension)).map(row =>
            first(row, node => hasClass(node, 'crosscut-row')).getAttribute('data-value-key'));
        }
        function facetCounts(dimension) {
          const result = {};
          for (const row of tableRows(crosscutTable(dimension))) {
            result[first(row, node => hasClass(node, 'crosscut-row')).getAttribute('data-value-key')] =
              Number(row.childNodes[1].textContent);
          }
          return result;
        }
        const dimensionLabels = { project: 'Project', wg: 'Workgroup', type: 'Type',
          artifact: 'Artifact', page: 'Page', impact: 'Impact', spec: 'Specification' };
        function assertDimensionHidden(dimension) {
          if (routePath() === 'list') assert.ok(!tableHeaders(ticketTable()).includes(dimensionLabels[dimension]));
          else assert.equal(crosscut(dimension), null);
        }
        function assertDimensionVisible(dimension) {
          if (routePath() === 'list') assert.ok(tableHeaders(ticketTable()).includes(dimensionLabels[dimension]));
          else assert.ok(crosscut(dimension), 'Dimension must return: ' + dimension);
        }
        function ticketTable() {
          const tables = all(main, node => hasClass(node, 'ticket-list-table'));
          assert.equal(tables.length, 1, 'The real list route must render one ticket table.');
          return tables[0];
        }
        function ticketKeys() { return tableRows(ticketTable()).map(row => row.firstChild.textContent); }
        function matchingCount() {
          const count = byClass(main, 'matching-ticket-count');
          if (count) return Number(count.getAttribute('data-count'));
          const title = byClass(main, 'view-heading').textContent.match(/\((\d+)\)$/);
          assert.ok(title, 'List heading must show the matching ticket count.');
          return Number(title[1]);
        }
        function tableHeaders(table) {
          return all(table, node => node.tagName === 'th').map(th => {
            assert.equal(th.scope, 'col');
            return first(th, node => hasClass(node, 'sort-button')).firstChild.textContent;
          });
        }
        function sortButton(table, label) {
          const result = first(table, node => hasClass(node, 'sort-button') &&
            node.firstChild.textContent === label);
          assert.ok(result, 'Missing sort control: ' + label);
          return result;
        }
        function sortState(table) {
          const th = first(table, node => node.tagName === 'th' && node.getAttribute('aria-sort') !== 'none');
          return th ? [first(th, node => hasClass(node, 'sort-button')).firstChild.textContent,
            th.getAttribute('aria-sort')] : null;
        }
        function searchInput() {
          const input = first(main, node => node.tagName === 'input');
          assert.ok(input, 'Search control must remain available.');
          return input;
        }
        function typeSearch(value) { const input = searchInput(); input.value = value; input.dispatchEvent({ type: 'input' }); }
        function authoredSection(title, required = true) {
          const section = first(main, node => node.tagName === 'details' &&
            first(node.firstChild, child => child.tagName === 'h3')?.textContent === title);
          if (required) assert.ok(section, 'Missing authored section: ' + title);
          return section;
        }
        function fieldText(label) {
          const list = first(byClass(main, 'ticket-header'), node => node.tagName === 'dl');
          const index = list.childNodes.findIndex(node => node.tagName === 'dt' && node.textContent === label);
          assert.ok(index >= 0, 'Missing ticket field: ' + label);
          return list.childNodes[index + 1].textContent;
        }
        function relatedItem(kind, key, linkType) {
          const matches = all(byClass(main, 'related-sidebar'), node => node.tagName === 'li' &&
            node.getAttribute('data-kind') === kind && node.getAttribute('data-item-key') === key);
          const result = linkType ? matches.find(node => node.textContent.includes(' (' + linkType + ')')) : matches[0];
          assert.ok(result, 'Missing related item: ' + kind + '/' + key);
          return result;
        }
        function assertExternal(link, url) {
          assert.ok(link, 'A safe URL must be actionable: ' + url);
          assert.equal(link.href, url);
          assert.equal(link.getAttribute('href'), url, 'The supplied URL must not be re-encoded.');
          assert.equal(link.target, '_blank');
          assert.equal(link.rel, 'noopener noreferrer');
        }

        const timers = new Map();
        let cancelledDebounces = 0;
        function schedule(callback, delay) {
          const id = setTimeout(() => { timers.delete(id); callback(); }, delay);
          timers.set(id, { callback, delay });
          return id;
        }
        function cancel(id) {
          if (timers.get(id)?.delay === 150) cancelledDebounces++;
          timers.delete(id);
          clearTimeout(id);
        }
        function flushDebounces() {
          for (const [id, timer] of Array.from(timers)) {
            if (timer.delay !== 150) continue;
            clearTimeout(id);
            timers.delete(id);
            timer.callback();
          }
        }
        let hook = null;
        let copiedMarkdown = null;
        const sandbox = {
          console, WebAssembly, Uint8Array, ArrayBuffer, TextDecoder, TextEncoder,
          URL, URLSearchParams, TypeError, Blob, Response, DecompressionStream, atob, btoa,
          Node: NodeStub, document, location, history,
          setTimeout: schedule, clearTimeout: cancel,
          navigator: { clipboard: { writeText: async value => { copiedMarkdown = value; } } },
          addEventListener: (name, callback) => {
            (windowListeners[name] || (windowListeners[name] = [])).push(callback);
          },
          __DISCUSSION_TEST_HOOK__: value => { hook = value; }
        };
        sandbox.window = sandbox;
        sandbox.globalThis = sandbox;
        vm.createContext(sandbox);
        for (const inline of html.matchAll(/<script>([\s\S]*?)<\/script>/g)) {
          vm.runInContext(inline[1], sandbox, { filename: 'index.html inline script' });
        }
        assert.equal(sandbox.__ASSET_VERSION__, version);
        assert.ok(sandbox.__DB__ && sandbox.__SQL_WASM__, 'Database and WASM must be embedded for file hosting.');

        // State must also run with no document, database, or facet catalog.
        const stateSource = fs.readFileSync(path.join(site, 'assets', 'state.js'), 'utf8');
        const pure = { window: {}, URLSearchParams };
        vm.runInNewContext(stateSource, pure, { filename: 'state.js' });
        const state = pure.window.DiscussionState;
        const original = state.create('by-impact', { wg: ['value:Alpha'] }, { project: ['value:FHIR'] }, 2);
        const selected = state.select(state.select(original, 'wg', 'value:Beta'), 'wg', 'VALUE:alpha');
        assert.deepEqual(plain(selected.filters.wg), ['value:Alpha', 'value:Beta']);
        assert.deepEqual(plain(original.filters.wg), ['value:Alpha']);
        assert.ok(Object.isFrozen(original) && Object.isFrozen(original.filters.wg));
        const removed = state.remove(selected, 'wg', 'value:Alpha');
        assert.deepEqual(plain(removed.filters.wg), ['value:Beta']);
        assert.equal(state.isDimensionVisible(removed, 'wg'), false);
        assert.equal(state.isDimensionVisible(state.remove(removed, 'wg', 'value:Beta'), 'wg'), true);
        assert.deepEqual(plain(state.remove(original, 'project', 'value:FHIR').fixedFilters), { project: ['value:FHIR'] });
        assert.equal(state.isDimensionVisible(original, 'project'), false);
        assert.equal(state.isDimensionVisible(state.create('', {}, {}, 1), 'project'), false);
        assert.equal(state.isDimensionVisible(state.create('', { wg: ['value:Alpha'] }, {}, 2), 'project'), true);
        assert.equal(state.toHash(state.navigate(selected, 'list'), ['project', 'wg']),
          '#/list?wgKey=value%3AAlpha&wgKey=value%3ABeta');

        for (const file of ['sql-wasm.js', 'state.js', 'components.js', 'app.js']) {
          assert.ok(html.includes('assets/' + file + '?v=' + version), 'Versioned dependency: ' + file);
          vm.runInContext(fs.readFileSync(path.join(site, 'assets', file), 'utf8'), sandbox, { filename: file });
        }
        """;
}
