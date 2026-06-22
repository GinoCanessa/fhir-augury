// Applying sub-site SPA — Phase 6 implementation.
// Views:
//   #/        landing crosscut (count + by-workgroup, by-spec, by-repo, by-spanned-repos)
//   #/list    flat ticket list
//   #/ticket/<key>   per-ticket detail
//   #/topics  topic list (with SpannedRepos)
//   #/topic/<id>     per-topic detail grouped by repo
(async function () {
  const app = document.getElementById('app');
  const chipsEl = document.getElementById('chips');
  if (!app) return;

  let db;
  const SITE_NAME = 'Applying';
  let STATIC_TITLE = '';

  function truncate(str, max) {
    if (!str) return str;
    return str.length > max ? str.slice(0, max - 1) + '…' : str;
  }

  function setDocTitle(subject) {
    document.title = subject ? subject + ' — ' + SITE_NAME : STATIC_TITLE;
  }
  try {
    const SQL = await initSqlJs({ locateFile: f => 'assets/' + f });
    const b64 = window.__DB__ || '';
    if (!b64) { app.textContent = 'No database inlined.'; return; }
    const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
    db = new SQL.Database(bytes);
  } catch (e) {
    app.textContent = 'Failed to load planner DB: ' + (e.message || e);
    return;
  }

  // Render the active filter chips (from the generator-time __FILTERS__ blob).
  const filters = window.__FILTERS__ || {};
  if (chipsEl) {
    Object.entries(filters).forEach(([k, v]) => {
      const chip = document.createElement('span');
      chip.className = 'chip';
      chip.textContent = k + ': ' + v;
      chipsEl.appendChild(chip);
    });
  }

  function escape(s) {
    return (s == null ? '' : String(s)).replace(/[&<>"']/g,
      c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  // Render authored Markdown prose to sanitized HTML for innerHTML.
  // marked parses Markdown (raw-HTML passthrough enabled); DOMPurify is the
  // single sanitization layer. Ticket content is untrusted. If either global is
  // missing, degrade to today's escaped-paragraph behavior rather than throwing.
  function md(s) {
    if (s == null || s === '') return '';
    if (typeof marked === 'undefined' || typeof DOMPurify === 'undefined') {
      return '<p>' + escape(s) + '</p>';
    }
    return DOMPurify.sanitize(marked.parse(String(s)));
  }

  // Sanitize authored HTML (jira request/resolution) for innerHTML. DOMPurify
  // is the single sanitization layer; ticket content is untrusted. Degrades to
  // an escaped paragraph if DOMPurify is missing. NOTE: callers pass HTML here;
  // plain-text fallbacks must go through escape()+<pre>, never this helper.
  function sanitizeHtml(s) {
    if (s == null || s === '') return '';
    if (typeof DOMPurify === 'undefined') return '<p>' + escape(s) + '</p>';
    return DOMPurify.sanitize(String(s));
  }

  // Minimal DOM helpers (mirror the discussion sub-site). Scoped to the new
  // sortable/searchable list + breadcrumb code, which needs event wiring; the
  // other renderX functions remain string builders.
  function el(tag, attrs, child) {
    const node = document.createElement(tag);
    if (attrs) {
      for (const k in attrs) {
        if (k === 'class') node.className = attrs[k];
        else node.setAttribute(k, attrs[k]);
      }
    }
    if (child != null) {
      if (typeof child === 'string') node.textContent = child;
      else if (child instanceof Node) node.appendChild(child);
    }
    return node;
  }

  function clearChildren(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  function setBreadcrumb(tail) {
    const bc = document.getElementById('breadcrumb');
    if (!bc) return;
    clearChildren(bc);
    const hasTail = Array.isArray(tail) && tail.length > 0;
    const parts = [
      { label: 'Chooser', href: '../index.html' },
      { label: 'Applying', href: hasTail ? '#/' : null },
    ];
    if (hasTail) {
      for (let i = 0; i < tail.length; i++) parts.push(tail[i]);
    }
    for (let i = 0; i < parts.length; i++) {
      if (i > 0) bc.appendChild(document.createTextNode(' › '));
      const p = parts[i];
      if (p.href) bc.appendChild(el('a', { href: p.href }, p.label));
      else bc.appendChild(document.createTextNode(p.label));
    }
  }

  function query(sql, params) {
    const stmt = db.prepare(sql);
    if (params) stmt.bind(params);
    const rows = [];
    while (stmt.step()) rows.push(stmt.getAsObject());
    stmt.free();
    return rows;
  }

  function ticketTitle(key) {
    const rows = query(
      'SELECT COALESCE(Title, "(unknown)") AS Title FROM planned_jira_hydration WHERE IssueKey = $k AND JiraKey = $k LIMIT 1',
      { '$k': key });
    return rows.length ? rows[0].Title : '(unknown)';
  }

  function topicsExist() {
    const rows = query('SELECT count(*) AS c FROM planned_ticket_topics');
    return rows[0]?.c > 0;
  }

  function renderLanding() {
    const total = query('SELECT count(*) AS c FROM planned_tickets')[0]?.c ?? 0;
    const byRepo = query(
      'SELECT RepoKey, count(DISTINCT IssueKey) AS Tickets ' +
      'FROM planned_ticket_repos GROUP BY RepoKey ORDER BY Tickets DESC, RepoKey');
    const bySpannedRepos = query(
      'SELECT ttr.RepoKey, count(DISTINCT tt.RowId) AS Topics ' +
      'FROM planned_ticket_topic_repos ttr ' +
      'JOIN planned_ticket_topics tt ON tt.RowId = ttr.TopicRowId ' +
      'GROUP BY ttr.RepoKey ORDER BY Topics DESC, ttr.RepoKey');
    const showTopics = topicsExist();

    let html = '';
    html += '<section><p><strong>' + total + '</strong> planned tickets.</p>';
    html += '<p><a href="#/list">Show Ticket List →</a></p>';
    if (showTopics) {
      html += '<p><a href="#/topics">Show Topic List →</a></p>';
    } else {
      html += '<p class="muted">Show Topic List → <span class="tooltip" title="No planner topics have been written yet.">(unavailable)</span></p>';
    }
    html += '</section>';

    if (byRepo.length) {
      html += '<section><h2>Tickets by repo</h2><table><thead><tr><th>Repo</th><th>Tickets</th></tr></thead><tbody>';
      byRepo.forEach(r => {
        html += '<tr><td>' + escape(r.RepoKey) + '</td><td>' + r.Tickets + '</td></tr>';
      });
      html += '</tbody></table></section>';
    }

    if (bySpannedRepos.length) {
      html += '<section><h2>Topics by spanned repo</h2><table><thead><tr><th>Repo</th><th>Topics</th></tr></thead><tbody>';
      bySpannedRepos.forEach(r => {
        html += '<tr><td>' + escape(r.RepoKey) + '</td><td>' + r.Topics + '</td></tr>';
      });
      html += '</tbody></table></section>';
    }
    app.innerHTML = html;
  }

  function renderList() {
    const rows = query(
      'SELECT pt.Key AS Key, ' +
      '       COALESCE(jh.Title, "(unknown)") AS Title, ' +
      '       COALESCE(jh.WorkGroup, "(unknown)") AS WorkGroup, ' +
      '       COALESCE(jh.Specification, "(unknown)") AS Specification, ' +
      '       (SELECT GROUP_CONCAT(RepoKey) FROM (SELECT RepoKey FROM planned_ticket_repos r WHERE r.IssueKey = pt.Key ORDER BY RepoKey)) AS Repos, ' +
      '       (SELECT count(*) FROM planned_ticket_repo_changes c WHERE c.IssueKey = pt.Key) AS Changes ' +
      'FROM planned_tickets pt ' +
      'LEFT JOIN planned_jira_hydration jh ON jh.IssueKey = pt.Key AND jh.JiraKey = pt.Key ' +
      'ORDER BY pt.Key');

    clearChildren(app);
    setDocTitle('Planned tickets');
    app.appendChild(el('h2', null, 'Planned tickets (' + rows.length + ')'));

    const filterRow = el('div', { class: 'filter-row' });
    const input = el('input', {
      type: 'text',
      placeholder: 'Filter by key, title, workgroup, spec, or repos…',
      autocomplete: 'off',
    });
    filterRow.appendChild(input);
    app.appendChild(filterRow);

    const countWrap = el('p', { class: 'muted' });
    const countSpan = el('span', null, String(rows.length));
    countWrap.appendChild(countSpan);
    countWrap.appendChild(document.createTextNode(' rows'));
    app.appendChild(countWrap);

    const columns = [
      { label: 'Key',       field: 'Key',           cmp: 'key', link: true },
      { label: 'Title',     field: 'Title',         cmp: 'ci' },
      { label: 'Workgroup', field: 'WorkGroup',     cmp: 'ci' },
      { label: 'Spec',      field: 'Specification', cmp: 'ci' },
      { label: 'Repos',     field: 'Repos',         cmp: 'ci' },
      { label: 'Changes',   field: 'Changes',       cmp: 'num' },
    ];

    let sortCol = 'Key';
    let sortDir = 'asc';

    const table = el('table');
    const thead = el('thead');
    const headRow = el('tr');
    const headerCells = [];
    for (let ci = 0; ci < columns.length; ci++) {
      const col = columns[ci];
      const th = el('th', { class: 'sortable', role: 'button', tabindex: '0', 'aria-sort': 'none' }, col.label);
      const onActivate = (function (label) {
        return function () {
          if (sortCol === label) {
            sortDir = (sortDir === 'asc') ? 'desc' : 'asc';
          } else {
            sortCol = label;
            sortDir = 'asc';
          }
          renderRows(input.value);
          updateHeaderAffordances();
        };
      })(col.label);
      th.addEventListener('click', onActivate);
      th.addEventListener('keydown', function (ev) {
        if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); onActivate(); }
      });
      headerCells.push({ th: th, label: col.label });
      headRow.appendChild(th);
    }
    thead.appendChild(headRow);
    table.appendChild(thead);

    function updateHeaderAffordances() {
      for (let i = 0; i < headerCells.length; i++) {
        const cell = headerCells[i];
        const active = (cell.label === sortCol);
        const glyph = active ? (sortDir === 'asc' ? ' \u25b2' : ' \u25bc') : '';
        cell.th.textContent = cell.label + glyph;
        cell.th.setAttribute('aria-sort', active ? (sortDir === 'asc' ? 'ascending' : 'descending') : 'none');
      }
    }

    function compareFor(cmpType) {
      if (cmpType === 'key') {
        return function (a, b) { return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' }); };
      }
      if (cmpType === 'num') {
        return function (a, b) { return (parseFloat(a) || 0) - (parseFloat(b) || 0); };
      }
      return function (a, b) { return String(a).localeCompare(String(b), undefined, { sensitivity: 'base' }); };
    }

    const tbody = el('tbody');
    const renderRows = function (needle) {
      clearChildren(tbody);
      const n = (needle || '').toLowerCase();
      const filtered = [];
      for (let i = 0; i < rows.length; i++) {
        const r = rows[i];
        if (n.length > 0) {
          // Search the actual row fields (exclude Changes), case-insensitive.
          const hay = String(r.Key || '') + '\n' + String(r.Title || '') + '\n' +
            String(r.WorkGroup || '') + '\n' + String(r.Specification || '') + '\n' + String(r.Repos || '');
          if (hay.toLowerCase().indexOf(n) < 0) continue;
        }
        filtered.push(r);
      }

      let activeCol = null;
      for (let i = 0; i < columns.length; i++) {
        if (columns[i].label === sortCol) { activeCol = columns[i]; break; }
      }
      if (activeCol) {
        const cmp = compareFor(activeCol.cmp);
        const dirMul = (sortDir === 'desc') ? -1 : 1;
        const field = activeCol.field;
        filtered.sort(function (a, b) { return cmp(a[field], b[field]) * dirMul; });
      }

      for (let i = 0; i < filtered.length; i++) {
        const r = filtered[i];
        const tr = el('tr');
        const keyCell = el('td');
        keyCell.appendChild(el('a', { href: '#/ticket/' + encodeURIComponent(String(r.Key)) }, String(r.Key)));
        tr.appendChild(keyCell);
        tr.appendChild(el('td', null, String(r.Title || '')));
        tr.appendChild(el('td', null, String(r.WorkGroup || '')));
        tr.appendChild(el('td', null, String(r.Specification || '')));
        tr.appendChild(el('td', null, String(r.Repos || '')));
        tr.appendChild(el('td', null, String(r.Changes || 0)));
        tbody.appendChild(tr);
      }
      countSpan.textContent = (needle && needle.length > 0) ? (filtered.length + ' of ' + rows.length) : String(rows.length);
    };

    table.appendChild(tbody);
    app.appendChild(table);

    let debounce = 0;
    input.addEventListener('input', function () {
      if (debounce) window.clearTimeout(debounce);
      debounce = window.setTimeout(function () { renderRows(input.value); }, 150);
    });
    renderRows('');
    updateHeaderAffordances();
  }

  function renderTicket(key) {
    const summary = query(
      'SELECT pt.Key AS Key, pt.ResolutionSummary, pt.FeatureProposal, pt.DesignRationale, ' +
      '       jh.Title AS Title, jh.WorkGroup AS WorkGroup, jh.Status AS Status, jh.Type AS Type, ' +
      '       jh.Url AS Url, ' +
      '       COALESCE(th.Specification, jh.Specification) AS Specification, ' +
      '       COALESCE(th.Priority, jh.Priority) AS Priority, ' +
      '       COALESCE(th.Resolution, jh.Resolution) AS Resolution, ' +
      '       th.DescriptionPlain AS DescriptionPlain, ' +
      '       COALESCE(th.ResolutionDescriptionPlain, jh.ResolutionDescriptionPlain) AS ResolutionDescriptionPlain ' +
      'FROM planned_tickets pt ' +
      'LEFT JOIN planned_jira_hydration jh ON jh.IssueKey = pt.Key AND jh.JiraKey = pt.Key ' +
      'LEFT JOIN planned_ticket_hydration th ON th.IssueKey = pt.Key ' +
      'WHERE pt.Key = $k',
      { '$k': key })[0];
    if (!summary) { app.innerHTML = '<p>Ticket not found.</p>'; return; }
    const repos = query(
      'SELECT RepoKey, RepoRevision, Justification FROM planned_ticket_repos WHERE IssueKey = $k ORDER BY RepoKey',
      { '$k': key });
    const changes = query(
      'SELECT TicketRepoId, RepoKey, ChangeSequence, FilePath, ChangeTitle, ChangeDescription, ReplacementLines ' +
      'FROM planned_ticket_repo_changes WHERE IssueKey = $k ORDER BY ChangeSequence',
      { '$k': key });
    const impacts = query(
      'SELECT RepoKey, AffectedFilePath, HowAffected FROM planned_ticket_repo_impacts WHERE IssueKey = $k',
      { '$k': key });
    const validations = query(
      'SELECT RepoKey, ValidationSequence, Action FROM planned_ticket_change_validations WHERE IssueKey = $k ORDER BY ValidationSequence',
      { '$k': key });
    const tests = query(
      'SELECT RepoKey, ConsiderationSequence, Consideration FROM planned_ticket_testing_considerations WHERE IssueKey = $k ORDER BY ConsiderationSequence',
      { '$k': key });
    const questions = query(
      'SELECT RepoKey, QuestionSequence, Question FROM planned_ticket_open_questions WHERE IssueKey = $k ORDER BY QuestionSequence',
      { '$k': key });

    // HTML request / resolution (planned_ticket_jira_content, backfilled at
    // emit time from jira.db). Older inlined DBs lack the table, so query
    // defensively.
    let content = [];
    try {
      content = query(
        'SELECT DescriptionHtml, ResolutionDescriptionHtml FROM planned_ticket_jira_content WHERE TicketKey = $k',
        { '$k': key });
    } catch (e) { content = []; }
    const c = content[0] || {};

    function section(label, bodyHtml, open) {
      return '<details class="accordion"' + (open ? ' open' : '') + '>' +
        '<summary><h3>' + escape(label) + '</h3></summary>' +
        '<div class="accordion-body">' + bodyHtml + '</div></details>';
    }

    const title = summary.Title || ticketTitle(summary.Key);
    setDocTitle(title ? String(summary.Key) + ': ' + truncate(String(title), 60) : String(summary.Key));
    let html = '<h2>' + escape(summary.Key) + ' — ' + escape(title) + '</h2>';

    // Ticket summary key/value table (first section). Key links to Jira.
    const fb = v => (v == null || v === '') ? '(unknown)' : escape(v);
    let dl = '<dl class="ticket-summary">';
    dl += '<dt>Key</dt><dd><a href="https://jira.hl7.org/browse/' + encodeURIComponent(String(summary.Key)) +
      '" target="_blank" rel="noopener noreferrer">' + escape(summary.Key) + '</a></dd>';
    dl += '<dt>Title</dt><dd>' + fb(summary.Title) + '</dd>';
    dl += '<dt>Workgroup</dt><dd>' + fb(summary.WorkGroup) + '</dd>';
    dl += '<dt>Status</dt><dd>' + fb(summary.Status) + '</dd>';
    dl += '<dt>Type</dt><dd>' + fb(summary.Type) + '</dd>';
    dl += '<dt>Specification</dt><dd>' + fb(summary.Specification) + '</dd>';
    dl += '<dt>Priority</dt><dd>' + fb(summary.Priority) + '</dd>';
    dl += '<dt>Resolution</dt><dd>' + fb(summary.Resolution) + '</dd>';
    if (summary.Url) {
      dl += '<dt>Url</dt><dd><a href="' + escape(summary.Url) + '" target="_blank" rel="noopener noreferrer">' + escape(summary.Url) + '</a></dd>';
    } else {
      dl += '<dt>Url</dt><dd>—</dd>';
    }
    dl += '</dl>';
    html += section('Ticket summary', dl, true);

    // Original request: authored HTML, else plain-text (escaped <pre>).
    const descHtml = c.DescriptionHtml;
    if (descHtml != null && descHtml !== '') {
      html += section('Original request', '<div class="md">' + sanitizeHtml(descHtml) + '</div>', false);
    } else if (summary.DescriptionPlain != null && summary.DescriptionPlain !== '') {
      html += section('Original request', '<pre>' + escape(summary.DescriptionPlain) + '</pre>', false);
    }

    // Proposed / accepted resolution: authored HTML, else plain-text.
    const resHtml = c.ResolutionDescriptionHtml;
    if (resHtml != null && resHtml !== '') {
      html += section('Proposed / accepted resolution', '<div class="md">' + sanitizeHtml(resHtml) + '</div>', false);
    } else if (summary.ResolutionDescriptionPlain != null && summary.ResolutionDescriptionPlain !== '') {
      html += section('Proposed / accepted resolution', '<pre>' + escape(summary.ResolutionDescriptionPlain) + '</pre>', false);
    }

    html += section('Resolution summary', '<div class="md">' + md(summary.ResolutionSummary) + '</div>', true);
    html += section('Feature proposal', '<div class="md">' + md(summary.FeatureProposal) + '</div>', false);
    html += section('Design rationale', '<div class="md">' + md(summary.DesignRationale) + '</div>', false);

    repos.forEach(repo => {
      html += '<details class="accordion repo-section"><summary><h3>Repo: ' + escape(repo.RepoKey) + '</h3></summary><div class="accordion-body">';
      if (repo.RepoRevision) html += '<p class="muted">Revision: ' + escape(repo.RepoRevision) + '</p>';
      if (repo.Justification) html += '<div class="md">' + md(repo.Justification) + '</div>';

      const repoChanges = changes.filter(c => c.RepoKey === repo.RepoKey);
      if (repoChanges.length) {
        html += '<h4>Changes</h4><ul>';
        repoChanges.forEach(c => {
          html += '<li><strong>' + escape(c.ChangeTitle) + '</strong> — <code>' + escape(c.FilePath) + '</code>' +
            '<div class="md">' + md(c.ChangeDescription) + '</div>';
          try {
            const lines = JSON.parse(c.ReplacementLines || '[]');
            if (lines.length) {
              html += '<pre>' + lines.map(l => escape(l)).join('\n') + '</pre>';
            }
          } catch {}
          html += '</li>';
        });
        html += '</ul>';
      }

      const repoImpacts = impacts.filter(i => i.RepoKey === repo.RepoKey);
      if (repoImpacts.length) {
        html += '<h4>Impacts</h4><ul>';
        repoImpacts.forEach(i => html += '<li><code>' + escape(i.AffectedFilePath) + '</code> — <div class="md">' + md(i.HowAffected) + '</div></li>');
        html += '</ul>';
      }

      const repoValidations = validations.filter(v => v.RepoKey === repo.RepoKey);
      if (repoValidations.length) {
        html += '<h4>Change validations</h4><ul>';
        repoValidations.forEach(v => html += '<li><div class="md">' + md(v.Action) + '</div></li>');
        html += '</ul>';
      }

      const repoTests = tests.filter(t => t.RepoKey === repo.RepoKey);
      if (repoTests.length) {
        html += '<h4>Testing considerations</h4><ul>';
        repoTests.forEach(t => html += '<li><div class="md">' + md(t.Consideration) + '</div></li>');
        html += '</ul>';
      }

      const repoQuestions = questions.filter(q => q.RepoKey === repo.RepoKey);
      if (repoQuestions.length) {
        html += '<h4>Open questions</h4><ul>';
        repoQuestions.forEach(q => html += '<li><div class="md">' + md(q.Question) + '</div></li>');
        html += '</ul>';
      }
      html += '</div></details>';
    });

    setCopyExport(function () {
      return serializeTicketMarkdown(summary, repos, changes, impacts, validations, tests, questions);
    });
    app.innerHTML = html;
  }

  function renderTopicList() {
    setDocTitle('Topics');
    const rows = query(
      'SELECT tt.Id, tt.ShortDescription, tt.WorkGroupDisplay, tt.Specification, tt.Type, ' +
      '       (SELECT GROUP_CONCAT(RepoKey) FROM planned_ticket_topic_repos r WHERE r.TopicRowId = tt.RowId) AS Repos, ' +
      '       (SELECT count(*) FROM planned_ticket_topic_members m WHERE m.TopicRowId = tt.RowId) AS Tickets ' +
      'FROM planned_ticket_topics tt ORDER BY COALESCE(tt.RenderOrderHint, 1000000), tt.ShortDescription');
    let html = '<h2>Topics</h2>';
    if (!rows.length) {
      html += '<p>No topics have been written yet.</p>';
    } else {
      html += '<table><thead><tr><th>Topic</th><th>Workgroup</th><th>Spec</th><th>Type</th><th>Spanned repos</th><th>Tickets</th></tr></thead><tbody>';
      rows.forEach(r => {
        html += '<tr>' +
          '<td><a href="#/topic/' + encodeURIComponent(r.Id) + '">' + escape(r.ShortDescription) + '</a></td>' +
          '<td>' + escape(r.WorkGroupDisplay) + '</td>' +
          '<td>' + escape(r.Specification) + '</td>' +
          '<td>' + escape(r.Type) + '</td>' +
          '<td>' + escape(r.Repos || '') + '</td>' +
          '<td>' + (r.Tickets || 0) + '</td>' +
          '</tr>';
      });
      html += '</tbody></table>';
    }
    app.innerHTML = html;
  }

  function renderTopicDetail(id) {
    const topicRows = query(
      'SELECT RowId, ShortDescription, LongerDescription FROM planned_ticket_topics WHERE Id = $id',
      { '$id': id });
    if (!topicRows.length) { app.innerHTML = '<p>Topic not found.</p>'; return; }
    const topic = topicRows[0];
    setDocTitle(String(topic.ShortDescription || id));
    const repos = query(
      'SELECT RepoKey FROM planned_ticket_topic_repos WHERE TopicRowId = $r ORDER BY OrderInTopic',
      { '$r': topic.RowId });
    const members = query(
      'SELECT TicketKey, TopicGroupRowId FROM planned_ticket_topic_members WHERE TopicRowId = $r ORDER BY OrderInContainer',
      { '$r': topic.RowId });

    let html = '<h2>' + escape(topic.ShortDescription) + '</h2>';
    html += '<div class="md">' + md(topic.LongerDescription) + '</div>';
    html += '<h3>Spanned repos</h3><ul>';
    repos.forEach(r => html += '<li>' + escape(r.RepoKey) + '</li>');
    html += '</ul>';
    html += '<h3>Member tickets</h3><ul>';
    members.forEach(m => {
      html += '<li><a href="#/ticket/' + encodeURIComponent(m.TicketKey) + '">' + escape(m.TicketKey) + '</a>' +
        (m.TopicGroupRowId ? ' <span class="muted">(grouped)</span>' : '') + '</li>';
    });
    html += '</ul>';
    app.innerHTML = html;
  }

  function route() {
    clearCopyExport();
    setDocTitle(null);
    const hash = window.location.hash.replace(/^#/, '') || '/';
    if (hash === '/' || hash === '') { setBreadcrumb([]); renderLanding(); return; }
    if (hash === '/list') { setBreadcrumb([{ label: 'List', href: null }]); renderList(); return; }
    if (hash === '/topics') { setBreadcrumb([{ label: 'Topics', href: null }]); renderTopicList(); return; }
    if (hash.startsWith('/ticket/')) {
      const key = decodeURIComponent(hash.slice('/ticket/'.length));
      setBreadcrumb([{ label: 'List', href: '#/list' }, { label: key, href: null }]);
      renderTicket(key);
      return;
    }
    if (hash.startsWith('/topic/')) {
      const id = decodeURIComponent(hash.slice('/topic/'.length));
      setBreadcrumb([{ label: 'Topics', href: '#/topics' }, { label: id, href: null }]);
      renderTopicDetail(id);
      return;
    }
    setBreadcrumb([]);
    app.innerHTML = '<p>Unknown route.</p>';
  }

  // ---- Copy for AI (shared convention; identical across tools) -------------
  // A top-right "Copy for AI" button on detail/leaf views. It serializes the
  // current view from the in-memory rows to markdown (CurrentExport) and writes
  // it to the clipboard, with an execCommand fallback for file:// where the
  // async Clipboard API is unavailable. Detail views opt in via setCopyExport();
  // the router hides the button again via clearCopyExport() on every route.

  var CurrentExport = null;

  function installCopyButton() {
    if (document.querySelector('.copy-ai')) return;
    var header = document.querySelector('header');
    if (!header) return;
    var btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'copy-ai';
    btn.hidden = true;
    btn.textContent = '📋 Copy for AI';
    var status = document.createElement('span');
    status.className = 'copy-ai-status';
    status.setAttribute('role', 'status');
    btn.addEventListener('click', function () {
      try {
        var md = CurrentExport && CurrentExport();
        if (md) copyForAi(md);
        else setCopyStatus('Nothing to copy');
      } catch (e) {
        setCopyStatus('Copy failed');
      }
    });
    header.appendChild(btn);
    header.appendChild(status);
  }

  function setCopyStatus(msg) {
    var status = document.querySelector('.copy-ai-status');
    if (status) status.textContent = msg;
  }

  function setCopyExport(fn) {
    CurrentExport = fn;
    var btn = document.querySelector('.copy-ai');
    if (btn) btn.hidden = false;
    setCopyStatus('');
  }

  function clearCopyExport() {
    CurrentExport = null;
    var btn = document.querySelector('.copy-ai');
    if (btn) btn.hidden = true;
    setCopyStatus('');
  }

  function copyForAi(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(
        function () { setCopyStatus('Copied!'); },
        function () { setCopyStatus(copyViaTextarea(text) ? 'Copied!' : 'Copy failed'); }
      );
    } else {
      setCopyStatus(copyViaTextarea(text) ? 'Copied!' : 'Copy failed');
    }
  }

  function copyViaTextarea(text) {
    var ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.left = '-9999px';
    ta.style.top = '0';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.focus();
    ta.select();
    if (ta.setSelectionRange) ta.setSelectionRange(0, text.length);
    var copied = false;
    try { copied = document.execCommand('copy'); } catch (e) { copied = false; }
    document.body.removeChild(ta);
    return copied;
  }

  function mdEscapeCell(s) {
    if (s == null) return '';
    return String(s).replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');
  }

  function mdTable(headers, rows) {
    var out = '| ' + headers.map(mdEscapeCell).join(' | ') + ' |\n';
    out += '| ' + headers.map(function () { return '---'; }).join(' | ') + ' |\n';
    for (var i = 0; i < rows.length; i++) {
      out += '| ' + rows[i].map(mdEscapeCell).join(' | ') + ' |\n';
    }
    return out;
  }
  // ---- end Copy for AI shared convention ----------------------------------

  // ---- Copy for AI serializer (applying / planner specific) ---------------

  function serializeTicketMarkdown(row, repos, changes, impacts, validations, tests, questions) {
    var out = '# ' + String(row.Key || '') + ' — ' + String(row.Title || row.Key || '') + '\n\n';

    out += mdTable(['Field', 'Value'], [
      ['Key', String(row.Key || '')],
      ['Title', row.Title == null ? '' : String(row.Title)],
      ['Workgroup', row.WorkGroup == null ? '' : String(row.WorkGroup)],
      ['Status', row.Status == null ? '' : String(row.Status)],
      ['Type', row.Type == null ? '' : String(row.Type)],
      ['Specification', row.Specification == null ? '' : String(row.Specification)],
      ['Priority', row.Priority == null ? '' : String(row.Priority)],
      ['Resolution', row.Resolution == null ? '' : String(row.Resolution)],
      ['Url', row.Url == null ? '' : String(row.Url)]
    ]) + '\n';

    // Description blocks — prefer the *Plain text (escaped HTML is the view's
    // fallback; the export uses the plain variant directly).
    out += mdProseSection('Original request', row.DescriptionPlain);
    out += mdProseSection('Proposed / accepted resolution', row.ResolutionDescriptionPlain);

    // Authored markdown fields pass through verbatim (D2).
    out += mdProseSection('Resolution summary', row.ResolutionSummary);
    out += mdProseSection('Feature proposal', row.FeatureProposal);
    out += mdProseSection('Design rationale', row.DesignRationale);

    for (var ri = 0; ri < repos.length; ri++) {
      var repo = repos[ri];
      var rk = repo.RepoKey;
      out += '## Repo: ' + String(rk || '') + '\n\n';
      if (repo.RepoRevision) out += '_Revision: ' + String(repo.RepoRevision) + '_\n\n';
      if (repo.Justification != null && String(repo.Justification).trim() !== '') {
        out += String(repo.Justification).trim() + '\n\n';
      }

      var repoChanges = changes.filter(function (c) { return c.RepoKey === rk; });
      if (repoChanges.length > 0) {
        out += '### Changes (' + repoChanges.length + ')\n\n';
        out += mdTable(['File', 'Title', 'Description'],
          repoChanges.map(function (c) {
            return [String(c.FilePath || ''), String(c.ChangeTitle || ''), String(c.ChangeDescription || '')];
          })) + '\n';
      }

      var repoImpacts = impacts.filter(function (i) { return i.RepoKey === rk; });
      if (repoImpacts.length > 0) {
        out += '### Impacts (' + repoImpacts.length + ')\n\n';
        out += mdTable(['Affected file', 'How affected'],
          repoImpacts.map(function (i) { return [String(i.AffectedFilePath || ''), String(i.HowAffected || '')]; })) + '\n';
      }

      var repoValidations = validations.filter(function (v) { return v.RepoKey === rk; });
      if (repoValidations.length > 0) {
        out += '### Change validations (' + repoValidations.length + ')\n\n';
        out += mdTable(['Action'], repoValidations.map(function (v) { return [String(v.Action || '')]; })) + '\n';
      }

      var repoTests = tests.filter(function (t) { return t.RepoKey === rk; });
      if (repoTests.length > 0) {
        out += '### Testing considerations (' + repoTests.length + ')\n\n';
        out += mdTable(['Consideration'], repoTests.map(function (t) { return [String(t.Consideration || '')]; })) + '\n';
      }

      var repoQuestions = questions.filter(function (q) { return q.RepoKey === rk; });
      if (repoQuestions.length > 0) {
        out += '### Open questions (' + repoQuestions.length + ')\n\n';
        out += mdTable(['Question'], repoQuestions.map(function (q) { return [String(q.Question || '')]; })) + '\n';
      }
    }

    return out;
  }

  function mdProseSection(title, value) {
    if (value == null || String(value).trim() === '') return '';
    return '## ' + title + '\n\n' + String(value).trim() + '\n\n';
  }

  installCopyButton();
  STATIC_TITLE = document.title;
  window.addEventListener('hashchange', route);
  route();
})();
