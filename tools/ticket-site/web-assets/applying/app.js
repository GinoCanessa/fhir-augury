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
      '       (SELECT GROUP_CONCAT(DISTINCT RepoKey) FROM planned_ticket_repos r WHERE r.IssueKey = pt.Key) AS Repos, ' +
      '       (SELECT count(*) FROM planned_ticket_repo_changes c WHERE c.IssueKey = pt.Key) AS Changes ' +
      'FROM planned_tickets pt ' +
      'LEFT JOIN planned_jira_hydration jh ON jh.IssueKey = pt.Key AND jh.JiraKey = pt.Key ' +
      'ORDER BY pt.Key');
    let html = '<h2>Planned tickets</h2>';
    html += '<table><thead><tr><th>Key</th><th>Title</th><th>Workgroup</th><th>Spec</th><th>Repos</th><th>Changes</th></tr></thead><tbody>';
    rows.forEach(r => {
      html += '<tr>' +
        '<td><a href="#/ticket/' + encodeURIComponent(r.Key) + '">' + escape(r.Key) + '</a></td>' +
        '<td>' + escape(r.Title) + '</td>' +
        '<td>' + escape(r.WorkGroup) + '</td>' +
        '<td>' + escape(r.Specification) + '</td>' +
        '<td>' + escape(r.Repos || '') + '</td>' +
        '<td>' + (r.Changes || 0) + '</td>' +
        '</tr>';
    });
    html += '</tbody></table>';
    app.innerHTML = html;
  }

  function renderTicket(key) {
    const summary = query(
      'SELECT Key, ResolutionSummary, FeatureProposal, DesignRationale FROM planned_tickets WHERE Key = $k',
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

    let html = '<h2>' + escape(summary.Key) + ' — ' + escape(ticketTitle(summary.Key)) + '</h2>';
    function section(label, bodyHtml, open) {
      return '<details class="accordion"' + (open ? ' open' : '') + '>' +
        '<summary><h3>' + escape(label) + '</h3></summary>' +
        '<div class="accordion-body">' + bodyHtml + '</div></details>';
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

    app.innerHTML = html;
  }

  function renderTopicList() {
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
    const hash = window.location.hash.replace(/^#/, '') || '/';
    if (hash === '/' || hash === '') { renderLanding(); return; }
    if (hash === '/list') { renderList(); return; }
    if (hash === '/topics') { renderTopicList(); return; }
    if (hash.startsWith('/ticket/')) { renderTicket(decodeURIComponent(hash.slice('/ticket/'.length))); return; }
    if (hash.startsWith('/topic/')) { renderTopicDetail(decodeURIComponent(hash.slice('/topic/'.length))); return; }
    app.innerHTML = '<p>Unknown route.</p>';
  }

  window.addEventListener('hashchange', route);
  route();
})();
