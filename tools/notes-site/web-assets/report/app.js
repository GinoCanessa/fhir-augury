// notes-site report SPA — vanilla JS, sql.js in the browser, hash router.
// SECURITY: all DB-derived values are rendered via textContent / createElement
// only — never innerHTML — EXCEPT the two sanitizer-gated helpers htmlBlock()
// (authored ballot-note HTML) and mdBlock() (authored Markdown), both of which
// pass through DOMPurify. No network access; the notes DB is inlined as base64.

(function () {
  'use strict';

  var UNKNOWN_WG = '(unknown)';

  /** @type {any} */
  var db = null;

  var App = {
    init: async function () {
      var main = document.getElementById('app');
      renderProvenance();
      try {
        // initSqlJs is global, set by sql-wasm.js.
        // eslint-disable-next-line no-undef
        var SQL = await initSqlJs({ locateFile: function (f) { return 'assets/' + f; } });
        var blob = (typeof window.__DB__ === 'string') ? window.__DB__ : '';
        if (!blob) {
          throw new Error('window.__DB__ missing — emitter did not inline the database.');
        }
        var bin = atob(blob);
        var bytes = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        db = new SQL.Database(bytes);
      } catch (err) {
        renderError(main, 'Failed to load database: ' + err.message);
        return;
      }

      window.addEventListener('hashchange', App.route);
      App.route();
    },

    route: function () {
      var main = document.getElementById('app');
      clearChildren(main);
      var hash = window.location.hash || '#/';
      var stripped = hash.replace(/^#\/?/, '');
      var parts = stripped.split('/').filter(function (p) { return p.length > 0; });

      try {
        if (parts.length === 0) {
          setBreadcrumb([]);
          Views.landing(main);
        } else if (parts[0] === 'note' && parts[1]) {
          var noteId = decodeURIComponent(parts.slice(1).join('/'));
          Views.note(main, noteId);
        } else {
          setBreadcrumb([{ label: 'Not found', href: null }]);
          renderError(main, 'Unknown route: ' + hash);
        }
      } catch (err) {
        renderError(main, 'Error rendering view: ' + err.message);
      }
    }
  };

  // ---- views --------------------------------------------------------------

  var Views = {
    landing: function (main) {
      var rows = query(
        'SELECT NoteId, Name, Type, WorkGroup, WorkGroupCode, RepoOwner, RepoName, ' +
        'CommitsInWindow, TicketsAttributed, NeedsNote ' +
        'FROM notes', null).rows;

      main.appendChild(el('h2', null, 'Ballot notes (' + rows.length + ')'));

      if (rows.length === 0) {
        main.appendChild(el('p', { class: 'muted' }, 'No notes in this database yet. Run "notes-site write" to add some.'));
        return;
      }

      // Toolbar: free-text search + group-by-workgroup toggle + live count.
      var toolbar = el('div', { class: 'toolbar' });
      var search = el('input', {
        class: 'search-box', type: 'search',
        placeholder: 'Filter by name, type, workgroup, repo…',
        'aria-label': 'Filter notes', autocomplete: 'off'
      });
      var groupLabel = el('label');
      var groupToggle = el('input', { type: 'checkbox' });
      groupLabel.appendChild(groupToggle);
      groupLabel.appendChild(document.createTextNode(' Group by workgroup'));
      var countSpan = el('span', { class: 'count' }, String(rows.length) + ' notes');
      toolbar.appendChild(search);
      toolbar.appendChild(groupLabel);
      toolbar.appendChild(countSpan);
      main.appendChild(toolbar);

      var results = el('div');
      main.appendChild(results);

      var columns = [
        { label: 'Name', get: function (r) { return String(r.Name || ''); }, cmp: 'name', cell: nameCell },
        { label: 'Type', get: function (r) { return String(r.Type || ''); }, cmp: 'ci' },
        { label: 'Workgroup', get: function (r) { return String(r.WorkGroup || ''); }, cmp: 'ci' },
        { label: 'Repo', get: function (r) { return String(r.RepoOwner || '') + '/' + String(r.RepoName || ''); }, cmp: 'ci' },
        { label: 'Commits', get: function (r) { return Number(r.CommitsInWindow || 0); }, cmp: 'num', num: true },
        { label: 'Tickets', get: function (r) { return Number(r.TicketsAttributed || 0); }, cmp: 'num', num: true },
        { label: 'Needs note', get: function (r) { return String(r.NeedsNote || ''); }, cmp: 'ci', cell: needsNoteCell }
      ];

      var sortCol = 'Name';
      var sortDir = 'asc';

      function applySort(list) {
        var active = null;
        for (var i = 0; i < columns.length; i++) {
          if (columns[i].label === sortCol) { active = columns[i]; break; }
        }
        if (!active) return list;
        var cmp = compareFor(active.cmp);
        var dirMul = (sortDir === 'desc') ? -1 : 1;
        var get = active.get;
        return list.slice().sort(function (a, b) { return cmp(get(a), get(b)) * dirMul; });
      }

      function filterRows(needle) {
        var n = needle.toLowerCase();
        if (n.length === 0) return rows;
        return rows.filter(function (r) {
          var hay = [r.Name, r.Type, r.WorkGroup, r.RepoOwner + '/' + r.RepoName, r.NeedsNote]
            .join('\n').toLowerCase();
          return hay.indexOf(n) >= 0;
        });
      }

      function onHeaderActivate(label) {
        if (sortCol === label) {
          sortDir = (sortDir === 'asc') ? 'desc' : 'asc';
        } else {
          sortCol = label;
          sortDir = 'asc';
        }
        render();
      }

      function buildTable(rowSet) {
        var table = el('table');
        var thead = el('thead');
        var headRow = el('tr');
        for (var ci = 0; ci < columns.length; ci++) {
          var col = columns[ci];
          var active = (col.label === sortCol);
          var glyph = active ? (sortDir === 'asc' ? ' \u25b2' : ' \u25bc') : '';
          var th = el('th', {
            class: col.num ? 'sortable num' : 'sortable',
            role: 'button', tabindex: '0',
            'aria-sort': active ? (sortDir === 'asc' ? 'ascending' : 'descending') : 'none'
          }, col.label + glyph);
          (function (label) {
            th.addEventListener('click', function () { onHeaderActivate(label); });
            th.addEventListener('keydown', function (ev) {
              if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); onHeaderActivate(label); }
            });
          })(col.label);
          headRow.appendChild(th);
        }
        thead.appendChild(headRow);
        table.appendChild(thead);

        var tbody = el('tbody');
        var sorted = applySort(rowSet);
        for (var i = 0; i < sorted.length; i++) {
          var r = sorted[i];
          var tr = el('tr');
          for (var cj = 0; cj < columns.length; cj++) {
            var c = columns[cj];
            if (c.cell) {
              var td = el('td', c.num ? { class: 'num' } : null);
              td.appendChild(c.cell(r));
              tr.appendChild(td);
            } else {
              tr.appendChild(el('td', c.num ? { class: 'num' } : null, String(c.get(r))));
            }
          }
          tbody.appendChild(tr);
        }
        table.appendChild(tbody);
        return table;
      }

      function render() {
        clearChildren(results);
        var filtered = filterRows(search.value);
        countSpan.textContent = (search.value.length > 0)
          ? (filtered.length + ' of ' + rows.length + ' notes')
          : (rows.length + ' notes');

        if (groupToggle.checked) {
          var groups = groupByWorkGroup(filtered);
          for (var g = 0; g < groups.length; g++) {
            var grp = groups[g];
            var section = el('div', { class: 'wg-group' });
            section.appendChild(el('h2', null, grp.name + ' (' + grp.rows.length + ')'));
            section.appendChild(buildTable(grp.rows));
            results.appendChild(section);
          }
          if (groups.length === 0) {
            results.appendChild(el('p', { class: 'muted' }, 'No matching notes.'));
          }
        } else {
          if (filtered.length === 0) {
            results.appendChild(el('p', { class: 'muted' }, 'No matching notes.'));
          } else {
            results.appendChild(buildTable(filtered));
          }
        }
      }

      var debounce = 0;
      search.addEventListener('input', function () {
        if (debounce) window.clearTimeout(debounce);
        debounce = window.setTimeout(render, 120);
      });
      groupToggle.addEventListener('change', render);
      render();
    },

    note: function (main, noteId) {
      var res = query('SELECT * FROM notes WHERE NoteId = $id', { $id: noteId });
      if (res.rows.length === 0) {
        setBreadcrumb([{ label: 'Not found', href: null }]);
        main.appendChild(el('p', { class: 'error' }, 'No note with id ' + noteId + '.'));
        main.appendChild(el('p', null, el('a', { href: '#/' }, '← Back to all notes')));
        return;
      }
      var n = res.rows[0];
      setBreadcrumb([{ label: String(n.Name || noteId), href: null }]);

      main.appendChild(el('h2', null, String(n.Name) + ' — ' + String(n.Type)));

      // Header definition list.
      var header = el('section', { class: 'detail-header' });
      var dl = el('dl');
      function kv(k, v) {
        dl.appendChild(el('dt', null, k));
        var dd = el('dd');
        if (v instanceof Node) dd.appendChild(v);
        else dd.appendChild(document.createTextNode(v == null || v === '' ? '—' : String(v)));
        dl.appendChild(dd);
      }
      var repo = String(n.RepoOwner || '') + '/' + String(n.RepoName || '');
      kv('Repository', el('a', {
        href: 'https://github.com/' + encodeURIComponent(String(n.RepoOwner)) + '/' + encodeURIComponent(String(n.RepoName)),
        target: '_blank', rel: 'noopener noreferrer'
      }, repo));
      if (n.RepoCategory) kv('Category', n.RepoCategory);
      kv('Type', n.Type);
      kv('Workgroup', n.WorkGroup);
      kv('Window', windowNode(n));
      kv('Commits in window', n.CommitsInWindow);
      kv('Tickets attributed', n.TicketsAttributed);
      kv('Needs note', needsNoteBadge(String(n.NeedsNote || 'unknown')));
      kv('Generated', n.GeneratedAt);
      header.appendChild(dl);
      main.appendChild(header);

      // Proposed ballot note (authored HTML).
      main.appendChild(el('h3', null, 'Proposed ballot note'));
      if (n.ProposedBallotNoteHtml && String(n.ProposedBallotNoteHtml).trim().length > 0) {
        var proposed = htmlBlock(String(n.ProposedBallotNoteHtml));
        proposed.className = 'ballot-note md';
        main.appendChild(proposed);
      } else {
        main.appendChild(el('p', { class: 'muted' }, 'No proposed ballot note drafted.'));
      }

      // Current ballot note (authored HTML), collapsed.
      if (n.CurrentBallotNoteHtml && String(n.CurrentBallotNoteHtml).trim().length > 0) {
        var cur = htmlBlock(String(n.CurrentBallotNoteHtml));
        cur.className = 'ballot-note current md';
        main.appendChild(accordion('Current ballot note (at HEAD)', cur, false));
      }

      // Roll-up summary (authored Markdown), open.
      if (n.RollupSummaryMarkdown && String(n.RollupSummaryMarkdown).trim().length > 0) {
        main.appendChild(accordion('Roll-up summary (after-applied)', mdBlock(String(n.RollupSummaryMarkdown)), true));
      }

      // Notes for reviewer (authored Markdown), open.
      if (n.NotesForReviewerMarkdown && String(n.NotesForReviewerMarkdown).trim().length > 0) {
        main.appendChild(accordion('Notes for reviewer', mdBlock(String(n.NotesForReviewerMarkdown)), true));
      }

      renderSourceFiles(main, noteId, n);
      renderCommits(main, noteId);
      renderTickets(main, noteId);

      main.appendChild(el('p', { class: 'back-link' }, el('a', { href: '#/' }, '← Back to all notes')));
    }
  };

  function renderSourceFiles(main, noteId, n) {
    var files = query(
      'SELECT Path, Role, TouchedInWindow FROM note_source_files WHERE NoteId = $id ORDER BY FileOrder',
      { $id: noteId }).rows;
    if (files.length === 0 && !(n.SourceFilesNote && String(n.SourceFilesNote).trim().length > 0)) return;
    main.appendChild(el('h3', null, 'Source files (' + files.length + ')'));
    if (files.length > 0) {
      var table = el('table');
      var thead = el('thead');
      thead.appendChild(rowOf('th', ['Path', 'Role', 'Touched in window']));
      table.appendChild(thead);
      var tbody = el('tbody');
      for (var i = 0; i < files.length; i++) {
        var f = files[i];
        var tr = el('tr');
        tr.appendChild(el('td', { class: 'mono' }, String(f.Path || '')));
        tr.appendChild(el('td', null, String(f.Role || '')));
        tr.appendChild(el('td', null, truthy(f.TouchedInWindow) ? 'yes' : 'no'));
        tbody.appendChild(tr);
      }
      table.appendChild(tbody);
      main.appendChild(table);
    }
    if (n.SourceFilesNote && String(n.SourceFilesNote).trim().length > 0) {
      main.appendChild(el('p', { class: 'muted' }, String(n.SourceFilesNote)));
    }
  }

  function renderCommits(main, noteId) {
    var commits = query(
      'SELECT Sha, ShortSha, AuthorName, AuthorDate, Subject, WebUrl, TicketKeys ' +
      'FROM note_commits WHERE NoteId = $id ORDER BY CommitOrder',
      { $id: noteId }).rows;
    if (commits.length === 0) return;
    main.appendChild(el('h3', null, 'Commits in window (' + commits.length + ')'));
    var table = el('table');
    var thead = el('thead');
    thead.appendChild(rowOf('th', ['Commit', 'Author', 'Date', 'Subject', 'Tickets']));
    table.appendChild(thead);
    var tbody = el('tbody');
    for (var i = 0; i < commits.length; i++) {
      var c = commits[i];
      var tr = el('tr');
      var shaCell = el('td', { class: 'mono' });
      var shaText = String(c.ShortSha || c.Sha || '');
      if (c.WebUrl) {
        shaCell.appendChild(el('a', { href: String(c.WebUrl), target: '_blank', rel: 'noopener noreferrer' }, shaText));
      } else {
        shaCell.appendChild(document.createTextNode(shaText));
      }
      tr.appendChild(shaCell);
      tr.appendChild(el('td', null, String(c.AuthorName || '')));
      tr.appendChild(el('td', { class: 'mono' }, fmtDate(c.AuthorDate)));
      tr.appendChild(el('td', { class: 'subject' }, String(c.Subject || '')));
      tr.appendChild(el('td', null, String(c.TicketKeys || '')));
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    main.appendChild(table);
  }

  function renderTickets(main, noteId) {
    var tickets = query(
      'SELECT TicketKey, Title, Resolution, WorkGroup, Specification, Url, CommitCount ' +
      'FROM note_tickets WHERE NoteId = $id ORDER BY TicketOrder',
      { $id: noteId }).rows;
    if (tickets.length === 0) return;
    main.appendChild(el('h3', null, 'Tickets attributed (' + tickets.length + ')'));
    var table = el('table');
    var thead = el('thead');
    thead.appendChild(rowOf('th', ['Key', 'Title', 'Resolution', 'Workgroup', 'Specification', 'Commits']));
    table.appendChild(thead);
    var tbody = el('tbody');
    for (var i = 0; i < tickets.length; i++) {
      var t = tickets[i];
      var tr = el('tr');
      var keyCell = el('td');
      var url = String(t.Url || '') || ('https://jira.hl7.org/browse/' + encodeURIComponent(String(t.TicketKey)));
      keyCell.appendChild(el('a', { href: url, target: '_blank', rel: 'noopener noreferrer' }, String(t.TicketKey)));
      tr.appendChild(keyCell);
      tr.appendChild(el('td', { class: 'subject' }, String(t.Title || '')));
      tr.appendChild(el('td', null, String(t.Resolution || '')));
      tr.appendChild(el('td', null, String(t.WorkGroup || '')));
      tr.appendChild(el('td', null, String(t.Specification || '')));
      tr.appendChild(el('td', { class: 'num' }, String(t.CommitCount || 0)));
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    main.appendChild(table);
  }

  // ---- cell builders ------------------------------------------------------

  function nameCell(r) {
    return el('a', { href: '#/note/' + encodeURIComponent(String(r.NoteId)) }, String(r.Name || ''));
  }

  function needsNoteCell(r) {
    return needsNoteBadge(String(r.NeedsNote || 'unknown'));
  }

  function needsNoteBadge(value) {
    var v = (value || 'unknown').toLowerCase();
    var cls = v === 'yes' ? 'badge badge-yes' : (v === 'no' ? 'badge badge-no' : 'badge badge-unknown');
    return el('span', { class: cls }, v);
  }

  function windowNode(n) {
    var since = String(n.SinceShortSha || n.SinceSha || '');
    var head = String(n.HeadShortSha || n.HeadSha || '');
    if (!since && !head) return document.createTextNode('—');
    var span = el('span', { class: 'mono' });
    span.appendChild(commitLink(n, n.SinceSha, since));
    span.appendChild(document.createTextNode(' .. '));
    span.appendChild(commitLink(n, n.HeadSha, head));
    return span;
  }

  function commitLink(n, sha, text) {
    if (!text) return document.createTextNode('—');
    if (sha && n.RepoOwner && n.RepoName) {
      return el('a', {
        href: 'https://github.com/' + encodeURIComponent(String(n.RepoOwner)) + '/' +
          encodeURIComponent(String(n.RepoName)) + '/commit/' + encodeURIComponent(String(sha)),
        target: '_blank', rel: 'noopener noreferrer'
      }, text);
    }
    return document.createTextNode(text);
  }

  // ---- grouping -----------------------------------------------------------

  function groupByWorkGroup(rows) {
    var byKey = Object.create(null);
    for (var i = 0; i < rows.length; i++) {
      var wg = String(rows[i].WorkGroup || '').trim() || UNKNOWN_WG;
      if (!byKey[wg]) byKey[wg] = [];
      byKey[wg].push(rows[i]);
    }
    var names = Object.keys(byKey);
    names.sort(function (a, b) {
      if (a === UNKNOWN_WG) return 1;
      if (b === UNKNOWN_WG) return -1;
      return a.localeCompare(b, undefined, { sensitivity: 'base' });
    });
    return names.map(function (name) { return { name: name, rows: byKey[name] }; });
  }

  // ---- helpers ------------------------------------------------------------

  function compareFor(cmpType) {
    if (cmpType === 'num') {
      return function (a, b) { return Number(a) - Number(b); };
    }
    if (cmpType === 'name') {
      return function (a, b) {
        return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' });
      };
    }
    return function (a, b) {
      return String(a).localeCompare(String(b), undefined, { sensitivity: 'base' });
    };
  }

  function query(sql, params) {
    var stmt = db.prepare(sql);
    if (params) stmt.bind(params);
    var rows = [];
    while (stmt.step()) rows.push(stmt.getAsObject());
    stmt.free();
    return { rows: rows };
  }

  function clearChildren(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  function el(tag, attrs, children) {
    var node = document.createElement(tag);
    if (attrs) {
      for (var k in attrs) {
        if (k === 'class') node.className = attrs[k];
        else node.setAttribute(k, attrs[k]);
      }
    }
    if (children != null) {
      if (typeof children === 'string') node.textContent = children;
      else if (Array.isArray(children)) {
        for (var i = 0; i < children.length; i++) {
          var c = children[i];
          if (c == null) continue;
          if (typeof c === 'string') node.appendChild(document.createTextNode(c));
          else node.appendChild(c);
        }
      } else if (children instanceof Node) node.appendChild(children);
      else node.textContent = String(children);
    }
    return node;
  }

  function rowOf(tag, labels) {
    var tr = el('tr');
    for (var i = 0; i < labels.length; i++) tr.appendChild(el(tag, null, labels[i]));
    return tr;
  }

  function accordion(labelText, bodyNode, open) {
    var d = el('details', open ? { class: 'accordion', open: '' } : { class: 'accordion' });
    var s = el('summary');
    s.appendChild(el('h3', null, labelText));
    d.appendChild(s);
    var body = el('div', { class: 'accordion-body' });
    body.appendChild(bodyNode);
    d.appendChild(body);
    return d;
  }

  function renderError(main, msg) {
    clearChildren(main);
    main.appendChild(el('p', { class: 'error' }, msg));
  }

  // SECURITY: sanitizer-gated exception to the "never innerHTML" rule. The
  // ballot-note HTML is authored content; DOMPurify is the single sanitization
  // layer. Degrades to escaped text when DOMPurify is unavailable.
  function htmlBlock(rawHtml) {
    var div = el('div', { class: 'md' });
    if (rawHtml == null || rawHtml === '') return div;
    if (typeof DOMPurify === 'undefined') { div.textContent = String(rawHtml); return div; }
    div.innerHTML = DOMPurify.sanitize(String(rawHtml));
    return div;
  }

  // SECURITY: authored Markdown rendered via marked, then sanitized via
  // DOMPurify. Degrades to an escaped <pre> when either library is missing.
  function mdBlock(rawMarkdown) {
    var div = el('div', { class: 'md' });
    if (rawMarkdown == null || rawMarkdown === '') return div;
    var src = String(rawMarkdown);
    if (typeof marked === 'undefined' || typeof DOMPurify === 'undefined') {
      div.appendChild(el('pre', null, src));
      return div;
    }
    var rendered = (typeof marked.parse === 'function') ? marked.parse(src) : marked(src);
    div.innerHTML = DOMPurify.sanitize(rendered);
    return div;
  }

  function setBreadcrumb(tail) {
    var bc = document.getElementById('breadcrumb');
    if (!bc) return;
    clearChildren(bc);
    var hasTail = Array.isArray(tail) && tail.length > 0;
    var parts = [{ label: 'All notes', href: hasTail ? '#/' : null }];
    if (hasTail) for (var i = 0; i < tail.length; i++) parts.push(tail[i]);
    for (var j = 0; j < parts.length; j++) {
      if (j > 0) bc.appendChild(document.createTextNode(' › '));
      var p = parts[j];
      if (p.href) bc.appendChild(el('a', { href: p.href }, p.label));
      else bc.appendChild(document.createTextNode(p.label));
    }
  }

  function renderProvenance() {
    var pv = document.getElementById('provenance');
    if (!pv) return;
    var run = (typeof window.__RUN__ === 'object' && window.__RUN__) ? window.__RUN__ : null;
    if (!run) { pv.textContent = ''; return; }
    var bits = [];
    if (run.repoOwner && run.repoName) {
      var repo = run.repoOwner + '/' + run.repoName;
      bits.push(run.repoCategory ? (repo + ' (' + run.repoCategory + ')') : repo);
    }
    if (run.sinceShortSha || run.headShortSha) {
      bits.push('window ' + (run.sinceShortSha || '?') + '..' + (run.headShortSha || '?'));
    }
    if (typeof run.noteCount === 'number') bits.push(run.noteCount + ' notes');
    if (run.runAt) bits.push('generated ' + fmtDate(run.runAt));
    pv.textContent = bits.join('  ·  ');
  }

  function truthy(v) {
    if (v === 1 || v === true) return true;
    var s = String(v).toLowerCase();
    return s === '1' || s === 'true' || s === 'yes';
  }

  function fmtDate(value) {
    if (value == null || value === '') return '';
    var s = String(value);
    var d = new Date(s);
    if (isNaN(d.getTime())) return s;
    var iso = d.toISOString();
    return iso.slice(0, 10) + ' ' + iso.slice(11, 16) + 'Z';
  }

  App.init();
})();
