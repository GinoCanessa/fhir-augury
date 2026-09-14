using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FhirAugury.Processing.Client;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;

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

    [Fact]
    public async Task EmittedRuntimeExecutesRealListRouteAndFacetClick()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                includeSecondTicket: true,
                schemaVersion: PreparedTicketSnapshotSchemaV3.Version,
                useMultipleRuns: true,
                includeRendererEvidence: true,
                includeUntrustedPeople: true);
        VerifiedAuthoringSnapshotPair pair =
            await fixture.CreateVerifiedPairAsync("Preparer");
        string output = Path.Combine(_root, "site");
        await new TicketSitePublisher().PublishAsync(
            new TicketSitePublishRequest(
                pair,
                TicketSiteKind.Discussion,
                output,
                "Runtime discussion"));

        string site = Path.Combine(output, "discussion");
        string runner = Path.Combine(_root, "renderer-runtime.cjs");
        await File.WriteAllTextAsync(
            runner,
            """
            const fs = require('node:fs');
            const vm = require('node:vm');

            const site = process.argv[2];
            const html = fs.readFileSync(site + '/index.html', 'utf8');
            function capture(pattern, label) {
              const match = html.match(pattern);
              if (!match) throw new Error(label + ' was not emitted.');
              return match[1];
            }

            const database = capture(/window\.__DB__='([^']+)'/, 'database');
            const wasm = capture(/window\.__SQL_WASM__='([^']+)'/, 'WASM');
            const presentation = capture(
              /<script id="site-presentation" type="application\/json">([\s\S]*?)<\/script>/,
              'presentation');

            class NodeStub {
              constructor(tag, text) {
                this.tagName = String(tag || '').toLowerCase();
                this.childNodes = [];
                this.firstChild = null;
                this.className = '';
                this.attributes = {};
                this.listeners = {};
                this.parentNode = null;
                this.style = {};
                this._textContent = text == null ? '' : String(text);
              }
              get textContent() {
                if (this.childNodes.length > 0) {
                  return this.childNodes.map(child => child.textContent).join('');
                }
                return this._textContent;
              }
              set textContent(value) {
                for (const child of this.childNodes) child.parentNode = null;
                this.childNodes = [];
                this.firstChild = null;
                this._textContent = value == null ? '' : String(value);
              }
              appendChild(child) {
                if (this.tagName !== '#text') this._textContent = '';
                child.parentNode = this;
                this.childNodes.push(child);
                this.firstChild = this.childNodes[0] || null;
                return child;
              }
              removeChild(child) {
                const index = this.childNodes.indexOf(child);
                if (index >= 0) this.childNodes.splice(index, 1);
                child.parentNode = null;
                this.firstChild = this.childNodes[0] || null;
                return child;
              }
              setAttribute(name, value) {
                const text = String(value);
                this.attributes[name] = text;
                if (name === 'id') this.id = text;
                if (name === 'class') this.className = text;
                if (name === 'href') this.href = text;
                if (name === 'role') this.role = text;
              }
              getAttribute(name) {
                return Object.prototype.hasOwnProperty.call(this.attributes, name)
                  ? this.attributes[name]
                  : null;
              }
              addEventListener(name, callback) {
                this.listeners[name] = callback;
              }
              click() {
                if (this.listeners.click) this.listeners.click({ target: this });
              }
            }

            function hasClass(node, className) {
              return String(node.className || '').split(/\s+/).includes(className);
            }
            function findAll(root, predicate, matches) {
              const result = matches || [];
              if (predicate(root)) result.push(root);
              for (const child of root.childNodes || []) {
                findAll(child, predicate, result);
              }
              return result;
            }
            function findFirst(root, predicate) {
              const matches = findAll(root, predicate);
              return matches.length ? matches[0] : null;
            }
            function tableHeaders(table) {
              const header = findFirst(
                table,
                node => node.tagName === 'thead');
              if (!header || !header.firstChild) {
                throw new Error('Ticket table header was not rendered.');
              }
              return header.firstChild.childNodes.map(cell => {
                if (cell.scope !== 'col') {
                  throw new Error('Ticket table header did not retain column scope.');
                }
                const button = findFirst(
                  cell,
                  node => node.tagName === 'button' && hasClass(node, 'sort-button'));
                if (!button || !button.firstChild) {
                  throw new Error('Ticket table header was not sortable.');
                }
                return button.firstChild.textContent;
              });
            }
            function ticketTable(main) {
              const tables = findAll(
                main,
                node => node.tagName === 'table' &&
                  hasClass(node, 'ticket-list-table'));
              if (tables.length !== 1) {
                throw new Error('The real list route did not render one ticket table.');
              }
              return tables[0];
            }
            function assertTicketLinkRoles(table) {
              const body = findFirst(table, node => node.tagName === 'tbody');
              if (!body || body.childNodes.length !== 1) {
                throw new Error('The filtered list did not render one ticket row.');
              }
              const links = findAll(body, node => node.tagName === 'a');
              if (links.length !== 1 ||
                  links[0].textContent !== 'FHIR-1001' ||
                  !String(links[0].getAttribute('href') || '')
                    .startsWith('#/ticket/FHIR-1001')) {
                throw new Error('Only the ticket key may be a detail link.');
              }
              const facetButtons = findAll(
                body,
                node => node.tagName === 'button' &&
                  hasClass(node, 'ticket-facet-value'));
              if (facetButtons.length === 0) {
                throw new Error('List facet values were not rendered as buttons.');
              }
              return {
                ticketKey: links[0].textContent,
                facetButtons
              };
            }

            const main = new NodeStub('main');
            main.id = 'app';
            const header = new NodeStub('header');
            const heading = new NodeStub('h1');
            header.appendChild(heading);
            const breadcrumb = new NodeStub('nav');
            breadcrumb.id = 'breadcrumb';
            const body = new NodeStub('body');
            body.appendChild(header);
            body.appendChild(breadcrumb);
            body.appendChild(main);
            const presentationNode = new NodeStub('script', presentation);
            presentationNode.id = 'site-presentation';
            let onReady = null;
            const document = {
              readyState: 'loading',
              title: '',
              body,
              getElementById: function (id) {
                if (id === 'site-presentation') return presentationNode;
                if (id === 'app') return main;
                if (id === 'breadcrumb') return breadcrumb;
                return null;
              },
              querySelector: function (selector) {
                if (selector === 'header h1') return heading;
                if (selector === 'header') return header;
                if (selector.startsWith('.')) {
                  const className = selector.slice(1);
                  return findFirst(body, node => hasClass(node, className));
                }
                return null;
              },
              addEventListener: function (name, callback) {
                if (name === 'DOMContentLoaded') onReady = callback;
              },
              createElement: function (tag) { return new NodeStub(tag); },
              createTextNode: function (text) {
                return new NodeStub('#text', String(text));
              }
            };
            const windowListeners = Object.create(null);
            let currentHash =
              '#/list?wgKey=value%3AFHIR%20Infrastructure' +
              '&impactKey=value%3ANon-substantive';
            const location = {};
            Object.defineProperty(location, 'hash', {
              enumerable: true,
              get: function () { return currentHash; },
              set: function (value) {
                const next = String(value);
                if (next === currentHash) return;
                currentHash = next;
                for (const callback of windowListeners.hashchange || []) {
                  callback();
                }
              }
            });
            let hook = null;
            const sandbox = {
              console,
              WebAssembly,
              Uint8Array,
              ArrayBuffer,
              TextDecoder,
              TextEncoder,
              URLSearchParams,
              Blob,
              Response,
              DecompressionStream,
              atob,
              btoa,
              Node: NodeStub,
              document,
              setTimeout,
              clearTimeout,
              location,
              addEventListener: function (name, callback) {
                if (!windowListeners[name]) windowListeners[name] = [];
                windowListeners[name].push(callback);
              },
              __DB__: database,
              __DBGZ__: 1,
              __SQL_WASM__: wasm,
              __ASSET_VERSION__: 'runtime-test',
              __DISCUSSION_TEST_HOOK__: function (value) { hook = value; }
            };
            sandbox.window = sandbox;
            sandbox.globalThis = sandbox;
            vm.createContext(sandbox);
            vm.runInContext(
              fs.readFileSync(site + '/assets/sql-wasm.js', 'utf8'),
              sandbox,
              { filename: 'sql-wasm.js' });
            vm.runInContext(
              fs.readFileSync(site + '/assets/components.js', 'utf8'),
              sandbox,
              { filename: 'components.js' });
            vm.runInContext(
              fs.readFileSync(site + '/assets/app.js', 'utf8'),
              sandbox,
              { filename: 'app.js' });

            (async function () {
              if (!onReady) throw new Error('Runtime initialization was not registered.');
              await onReady();
              if (!hook) throw new Error('Runtime test hook was not exposed.');

              const initialTable = ticketTable(main);
              const initialHeaders = tableHeaders(initialTable);
              if (initialHeaders.includes('Workgroup') ||
                  initialHeaders.includes('Impact') ||
                  !initialHeaders.includes('Project') ||
                  !initialHeaders.includes('Type')) {
                throw new Error(
                  'The real list route did not suppress selected dimensions.');
              }
              const initialRoles = assertTicketLinkRoles(initialTable);
              const typeButton = initialRoles.facetButtons.find(
                button => button.textContent === 'Change Request');
              if (!typeButton) {
                throw new Error('The rendered Type facet button was not found.');
              }

              typeButton.click();
              const navigation = location.hash;
              const expectedNavigation =
                '#/list?wgKey=value%3AFHIR+Infrastructure' +
                '&typeKey=value%3AChange+Request' +
                '&impactKey=value%3ANon-substantive';
              if (navigation !== expectedNavigation) {
                throw new Error(
                  'Rendered facet click did not compose the list hash: ' +
                  navigation);
              }

              const finalTable = ticketTable(main);
              const finalHeaders = tableHeaders(finalTable);
              if (finalHeaders.includes('Workgroup') ||
                  finalHeaders.includes('Impact') ||
                  finalHeaders.includes('Type') ||
                  !finalHeaders.includes('Project')) {
                throw new Error(
                  'Clicked list dimension was not suppressed after rerouting.');
              }
              const finalRoles = assertTicketLinkRoles(finalTable);

              location.hash = '#/by-impact';
              const impactTables = findAll(
                main,
                node => node.tagName === 'table' &&
                  hasClass(node, 'crosscut-table'));
              if (impactTables.length !== 1) {
                throw new Error('The real impact route did not render its table.');
              }
              const impactLabels = findAll(
                impactTables[0],
                node => node.tagName === 'button' &&
                  hasClass(node, 'crosscut-row'))
                .map(button => button.textContent);
              if (!impactLabels.includes('Non-substantive') ||
                  !impactLabels.includes('Compatible, substantive')) {
                throw new Error('Impact crosscut did not use projected facet rows.');
              }

              const reporter = hook.query(
                "SELECT DisplayName, Availability, UnavailableReason " +
                "FROM ticket_people WHERE TicketKey = 'FHIR-1001' " +
                "AND Role = 'reporter'",
                null).rows[0];
              if (hook.renderPersonAvailability(reporter) !== 'Not provided') {
                throw new Error('Legitimately absent reporter was not distinguished.');
              }

              const prose = new NodeStub('pre');
              sandbox.DiscussionComponents.appendJiraLinkedText(
                prose,
                'See ballot-77 and UP-9.');
              const links = prose.childNodes.filter(
                node => node.tagName === 'a');
              if (links.length !== 2 ||
                  links[0].href !==
                    'https://jira.hl7.org/browse/BALLOT-77' ||
                  links[1].href !==
                    'https://jira.hl7.org/browse/UP-9') {
                throw new Error('Generic Jira prose links were not canonical.');
              }

              const countTable =
                sandbox.DiscussionComponents.createSortableTable({
                  rows: [{ Count: 2 }],
                  columns: [{
                    key: 'count',
                    label: 'Count',
                    className: 'count-column',
                    value: row => row.Count
                  }]
                }).element;
              const countHeader =
                countTable.childNodes[0].childNodes[0].childNodes[0];
              const countCell =
                countTable.childNodes[1].childNodes[0].childNodes[0];
              if (!countHeader.className.includes('count-column') ||
                  countCell.className !== 'count-column') {
                throw new Error('Count semantic class did not reach header and cell.');
              }

              process.stdout.write(JSON.stringify({
                impactLabels,
                initialHeaders,
                finalHeaders,
                navigation,
                ticketKey: finalRoles.ticketKey
              }));
            })().catch(error => {
              console.error(error && error.stack || error);
              process.exitCode = 1;
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
        startInfo.ArgumentList.Add(site);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Node.js.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        string standardOutput = await outputTask;
        string standardError = await errorTask;

        Assert.True(
            process.ExitCode == 0,
            $"Discussion renderer runtime probe failed:{Environment.NewLine}" +
            standardError);
        using JsonDocument result = JsonDocument.Parse(standardOutput);
        Assert.Equal(
            "FHIR-1001",
            result.RootElement.GetProperty("ticketKey").GetString());
        Assert.Contains(
            "Type",
            result.RootElement.GetProperty("initialHeaders")
                .EnumerateArray()
                .Select(value => value.GetString()));
        Assert.DoesNotContain(
            "Type",
            result.RootElement.GetProperty("finalHeaders")
                .EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Equal(
            "#/list?wgKey=value%3AFHIR+Infrastructure" +
            "&typeKey=value%3AChange+Request" +
            "&impactKey=value%3ANon-substantive",
            result.RootElement.GetProperty("navigation").GetString());
    }
}
