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

  var STATIC_TITLE = '';

  function truncate(str, max) {
    if (!str) return str;
    return str.length > max ? str.slice(0, max - 1) + '…' : str;
  }

  function setDocTitle(subject) {
    document.title = subject ? subject + ' — ' + STATIC_TITLE : STATIC_TITLE;
  }

  var App = {
    init: async function () {
      var main = document.getElementById('app');
      renderProvenance();
      installCopyButton();
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

      STATIC_TITLE = document.title;
      window.addEventListener('hashchange', App.route);
      App.route();
    },

    route: function () {
      var main = document.getElementById('app');
      clearChildren(main);
      clearCopyExport();
      setDocTitle(null);
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
      setDocTitle(String(n.Name || noteId));
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
        main.appendChild(copyHtmlButton(String(n.ProposedBallotNoteHtml)));
      } else {
        main.appendChild(el('p', { class: 'muted' }, 'No proposed ballot note drafted.'));
      }

      // Consolidation status: the regenerated note replaces only the prior
      // tool-generated block; hand-authored notes are preserved verbatim.
      var preservedRaw = String(n.PreservedHandAuthoredHtml || '');
      var preserved = preservedRaw.trim();
      var preservedCount = preserved ? preserved.split('</blockquote>').filter(function (s) { return s.trim().length > 0; }).length : 0;
      var statusBits = [];
      statusBits.push(truthy(n.CurrentNoteIsAuguryGenerated)
        ? 'Replaces the prior tool-generated note.'
        : 'No prior tool-generated note to replace.');
      if (preservedCount > 0) {
        statusBits.push(preservedCount + ' hand-authored note' + (preservedCount === 1 ? '' : 's') + ' preserved.');
      }
      main.appendChild(el('p', { class: 'muted consolidation-status' }, statusBits.join(' ')));

      // Hand-authored notes carried forward verbatim, collapsed.
      if (preserved.length > 0) {
        var pres = htmlBlock(preserved);
        pres.className = 'ballot-note preserved md';
        var presWrap = el('div', null, [pres, copyHtmlButton(preservedRaw)]);
        main.appendChild(accordion('Hand-authored notes (preserved)', presWrap, false));
      }

      // Current ballot note (authored HTML), collapsed.
      if (n.CurrentBallotNoteHtml && String(n.CurrentBallotNoteHtml).trim().length > 0) {
        var cur = htmlBlock(String(n.CurrentBallotNoteHtml));
        cur.className = 'ballot-note current md';
        var curLabel = 'Current ballot note (at HEAD)' + (truthy(n.CurrentNoteIsAuguryGenerated) ? ' — tool-generated' : ' — hand-authored');
        var curWrap = el('div', null, [cur, copyHtmlButton(String(n.CurrentBallotNoteHtml))]);
        main.appendChild(accordion(curLabel, curWrap, false));
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
      renderStructuralChanges(main, noteId);
      renderExtensionRefs(main, noteId);

      setCopyExport(function () { return serializeNoteMarkdown(noteId, n); });

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

  // Normalizes a ticket's ChangeImpact string to one of four ordered buckets.
  // Defer strictly to the ticket's classification: unset/unknown is always
  // "Unclassified" (rendered last), never folded into Non-substantive.
  function changeImpactBucket(value) {
    var v = String(value || '').toLowerCase().replace(/[\s_]+/g, ' ').trim();
    if (v.indexOf('non-compatible') >= 0 || v.indexOf('noncompatible') >= 0 || v.indexOf('non compatible') >= 0) {
      return { order: 0, label: 'Non-compatible' };
    }
    if (v.indexOf('non-substantive') >= 0 || v.indexOf('nonsubstantive') >= 0 || v.indexOf('non substantive') >= 0) {
      return { order: 2, label: 'Non-substantive' };
    }
    if (v.indexOf('substantive') >= 0) {
      return { order: 1, label: 'Compatible substantive' };
    }
    return { order: 3, label: 'Unclassified' };
  }

  // Technical-Correction issue Type wins over ChangeImpact: such tickets form
  // their own lowest-ranked group, never folded into the impact buckets.
  function ticketGroup(t) {
    var type = String((t && t.IssueType) || '').toLowerCase().replace(/[\s_]+/g, ' ').trim();
    if (type === 'technical correction') return { order: 4, label: 'Technical Correction' };
    return changeImpactBucket(t.ChangeImpact);
  }

  function renderTickets(main, noteId) {
    var tickets = query(
      'SELECT TicketKey, Title, Resolution, WorkGroup, Specification, Url, CommitCount, ChangeImpact, ChangeCategory, IssueType, RelatedTicketKeys ' +
      'FROM note_tickets WHERE NoteId = $id ORDER BY TicketOrder',
      { $id: noteId }).rows;
    if (tickets.length === 0) return;
    main.appendChild(el('h3', null, 'Tickets attributed (' + tickets.length + ')'));

    // Group by the ticket's group bucket, ordered Non-compatible → Compatible
    // substantive → Non-substantive → Unclassified → Technical Correction.
    var buckets = [[], [], [], [], []];
    for (var i = 0; i < tickets.length; i++) {
      buckets[ticketGroup(tickets[i]).order].push(tickets[i]);
    }
    var labels = ['Non-compatible', 'Compatible substantive', 'Non-substantive', 'Unclassified', 'Technical Correction'];

    for (var b = 0; b < buckets.length; b++) {
      if (buckets[b].length === 0) continue;
      main.appendChild(el('h4', { class: 'impact-header impact-' + b }, labels[b] + ' (' + buckets[b].length + ')'));
      var table = el('table');
      var thead = el('thead');
      thead.appendChild(rowOf('th', ['Key', 'Title', 'Resolution', 'Workgroup', 'Specification', 'Category', 'See also', 'Commits']));
      table.appendChild(thead);
      var tbody = el('tbody');
      for (var j = 0; j < buckets[b].length; j++) {
        var t = buckets[b][j];
        var tr = el('tr');
        var keyCell = el('td');
        var url = String(t.Url || '') || ('https://jira.hl7.org/browse/' + encodeURIComponent(String(t.TicketKey)));
        keyCell.appendChild(el('a', { href: url, target: '_blank', rel: 'noopener noreferrer' }, String(t.TicketKey)));
        tr.appendChild(keyCell);
        tr.appendChild(el('td', { class: 'subject' }, String(t.Title || '')));
        tr.appendChild(el('td', null, String(t.Resolution || '')));
        tr.appendChild(el('td', null, String(t.WorkGroup || '')));
        tr.appendChild(el('td', null, String(t.Specification || '')));
        var catCell = el('td');
        var cat = String(t.ChangeCategory || '').trim();
        if (cat) catCell.appendChild(el('span', { class: 'tag tag-category' }, cat));
        tr.appendChild(catCell);
        tr.appendChild(relatedCell(t.RelatedTicketKeys));
        tr.appendChild(el('td', { class: 'num' }, String(t.CommitCount || 0)));
        tbody.appendChild(tr);
      }
      table.appendChild(tbody);
      main.appendChild(table);
    }
  }

  // Renders the related/linked ticket keys as a "see also" list of Jira links.
  function relatedCell(value) {
    var cell = el('td', { class: 'related' });
    var keys = String(value || '').split(';').map(function (s) { return s.trim(); }).filter(function (s) { return s.length > 0; });
    if (keys.length === 0) return cell;
    for (var i = 0; i < keys.length; i++) {
      if (i > 0) cell.appendChild(document.createTextNode(', '));
      cell.appendChild(el('a', {
        href: 'https://jira.hl7.org/browse/' + encodeURIComponent(keys[i]),
        target: '_blank', rel: 'noopener noreferrer'
      }, keys[i]));
    }
    return cell;
  }

  // Renders the structural-change evidence panel from note_structural_changes.
  // The SPA cannot line-match opaque authored HTML to an element path, so this
  // is a separate accessible badge/list; the authoring skills embed inline
  // badges into the note itself.
  function renderStructuralChanges(main, noteId) {
    var changes = query(
      'SELECT SourcePath, ElementPath, ChangeKind, Detail, TicketKeys ' +
      'FROM note_structural_changes WHERE NoteId = $id ORDER BY ChangeOrder',
      { $id: noteId }).rows;
    if (changes.length === 0) return;
    main.appendChild(el('h3', null, 'Structural changes (' + changes.length + ')'));
    var table = el('table');
    var thead = el('thead');
    thead.appendChild(rowOf('th', ['', 'Element', 'Change', 'Detail', 'Tickets', 'Source']));
    table.appendChild(thead);
    var tbody = el('tbody');
    for (var i = 0; i < changes.length; i++) {
      var c = changes[i];
      var tr = el('tr');
      var badgeCell = el('td');
      var kind = String(c.ChangeKind || '');
      badgeCell.appendChild(el('span', {
        class: 'structural-badge',
        title: kind + ': ' + String(c.Detail || ''),
        'aria-label': 'structural change: ' + kind
      }, 'structural'));
      tr.appendChild(badgeCell);
      tr.appendChild(el('td', { class: 'mono' }, String(c.ElementPath || '')));
      tr.appendChild(el('td', null, kind));
      tr.appendChild(el('td', null, String(c.Detail || '')));
      tr.appendChild(relatedCell(c.TicketKeys));
      tr.appendChild(el('td', { class: 'mono muted' }, String(c.SourcePath || '')));
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    main.appendChild(table);
  }

  // ---- cell builders ------------------------------------------------------

  function renderExtensionRefs(main, noteId) {
    var refs = query(
      'SELECT ExtensionUrl, ExtensionName, ReplacementCoreElement, Rationale ' +
      'FROM note_extension_refs WHERE NoteId = $id ORDER BY RefOrder',
      { $id: noteId }).rows;
    if (refs.length === 0) return;
    main.appendChild(el('h3', null, 'Extensions cross-reference (' + refs.length + ')'));
    var table = el('table');
    var thead = el('thead');
    thead.appendChild(rowOf('th', ['Extension', 'Replaced by core element', 'Rationale']));
    table.appendChild(thead);
    var tbody = el('tbody');
    for (var i = 0; i < refs.length; i++) {
      var r = refs[i];
      var tr = el('tr');
      var extCell = el('td');
      var name = String(r.ExtensionName || '') || String(r.ExtensionUrl || '');
      var url = String(r.ExtensionUrl || '');
      if (url) {
        extCell.appendChild(el('a', { href: url, target: '_blank', rel: 'noopener noreferrer' }, name));
      } else {
        extCell.appendChild(document.createTextNode(name));
      }
      tr.appendChild(extCell);
      tr.appendChild(el('td', { class: 'mono' }, String(r.ReplacementCoreElement || '')));
      tr.appendChild(el('td', null, String(r.Rationale || '')));
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    main.appendChild(table);
  }

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
    var label = String(n.WindowLabel || '').trim();
    if (!since && !head && !label) return document.createTextNode('—');
    var span = el('span');
    if (label) {
      span.appendChild(document.createTextNode('Changes since ' + label));
      if (since || head) {
        var detail = el('span', { class: 'mono muted' }, ' (');
        detail.appendChild(commitLink(n, n.SinceSha, since || '?'));
        detail.appendChild(document.createTextNode(' .. '));
        detail.appendChild(commitLink(n, n.HeadSha, head || '?'));
        detail.appendChild(document.createTextNode(')'));
        span.appendChild(document.createTextNode(' '));
        span.appendChild(detail);
      }
      return span;
    }
    span.className = 'mono';
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
      var label = run.windowLabel ? String(run.windowLabel).trim() : '';
      if (label) {
        bits.push('changes since ' + label + ' (' + (run.sinceShortSha || '?') + '..' + (run.headShortSha || '?') + ')');
      } else {
        bits.push('window ' + (run.sinceShortSha || '?') + '..' + (run.headShortSha || '?'));
      }
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
    clipboardWrite(text, setCopyStatus);
  }

  function clipboardWrite(text, setStatus) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(
        function () { setStatus('Copied!'); },
        function () { setStatus(copyViaTextarea(text) ? 'Copied!' : 'Copy failed'); }
      );
    } else {
      setStatus(copyViaTextarea(text) ? 'Copied!' : 'Copy failed');
    }
  }

  // Inline "Copy HTML" affordance: a small button + its own status span that
  // copies a block's raw stored HTML string (verbatim, pre-sanitization) to
  // the clipboard as plain text. Distinct per instance so multiple buttons on
  // one page report status independently (does not touch the global
  // .copy-ai-status used by Copy for AI).
  function copyHtmlButton(rawHtml) {
    var status = el('span', { class: 'copy-html-status', role: 'status' });
    var btn = el('button', { type: 'button', class: 'copy-html' }, 'Copy HTML');
    btn.addEventListener('click', function () {
      clipboardWrite(String(rawHtml == null ? '' : rawHtml), function (m) { status.textContent = m; });
    });
    return el('div', { class: 'copy-html-wrap' }, [btn, status]);
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

  // ---- Copy for AI serializer (notes-site specific) -----------------------

  function serializeNoteMarkdown(noteId, note) {
    var out = '# ' + String(note.Name || noteId) + ' (' + String(note.Type || '') + ')\n\n';

    var since = String(note.SinceShortSha || note.SinceSha || '');
    var head = String(note.HeadShortSha || note.HeadSha || '');
    var winLabel = String(note.WindowLabel || '').trim();
    var winValue = winLabel
      ? ('changes since ' + winLabel + ((since || head) ? (' (' + since + ' .. ' + head + ')') : ''))
      : ((since || head) ? (since + ' .. ' + head) : '');
    out += mdTable(['Field', 'Value'], [
      ['NoteId', String(note.NoteId || noteId)],
      ['Repository', String(note.RepoOwner || '') + '/' + String(note.RepoName || '')],
      ['Category', note.RepoCategory == null ? '' : String(note.RepoCategory)],
      ['Workgroup', note.WorkGroup == null ? '' : String(note.WorkGroup)],
      ['Window', winValue],
      ['Commits in window', String(note.CommitsInWindow == null ? 0 : note.CommitsInWindow)],
      ['Tickets attributed', String(note.TicketsAttributed == null ? 0 : note.TicketsAttributed)],
      ['Needs note', String(note.NeedsNote || 'unknown')],
      ['Generated', note.GeneratedAt == null ? '' : String(note.GeneratedAt)]
    ]) + '\n';

    // Ballot-note HTML fields converted to markdown; roll-up / reviewer fields
    // are authored markdown and pass through verbatim (D2).
    out += serializeNoteSection('Proposed ballot note', htmlToMarkdown(note.ProposedBallotNoteHtml), 'No proposed ballot note drafted.');
    out += serializeNoteSection('Current ballot note (at HEAD)', htmlToMarkdown(note.CurrentBallotNoteHtml), 'No current ballot note.');
    out += serializeNoteSection('Roll-up summary (after-applied)', note.RollupSummaryMarkdown == null ? '' : String(note.RollupSummaryMarkdown), 'None.');
    out += serializeNoteSection('Notes for reviewer', note.NotesForReviewerMarkdown == null ? '' : String(note.NotesForReviewerMarkdown), 'None.');

    var files = query(
      'SELECT Path, Role, TouchedInWindow FROM note_source_files WHERE NoteId = $id ORDER BY FileOrder',
      { $id: noteId }).rows;
    out += '## Source files (' + files.length + ')\n\n';
    if (files.length > 0) {
      out += mdTable(['Path', 'Role', 'Touched in window'],
        files.map(function (f) { return [String(f.Path || ''), String(f.Role || ''), truthy(f.TouchedInWindow) ? 'yes' : 'no']; })) + '\n';
    } else {
      out += '_None._\n\n';
    }

    var commits = query(
      'SELECT Sha, ShortSha, AuthorName, AuthorDate, Subject, WebUrl, TicketKeys ' +
      'FROM note_commits WHERE NoteId = $id ORDER BY CommitOrder',
      { $id: noteId }).rows;
    out += '## Commits in window (' + commits.length + ')\n\n';
    if (commits.length > 0) {
      out += mdTable(['Commit', 'Author', 'Date', 'Subject', 'Tickets'],
        commits.map(function (c) {
          return [String(c.ShortSha || c.Sha || ''), String(c.AuthorName || ''), fmtDate(c.AuthorDate),
            String(c.Subject || ''), String(c.TicketKeys || '')];
        })) + '\n';
    } else {
      out += '_None._\n\n';
    }

    var tickets = query(
      'SELECT TicketKey, Title, Resolution, WorkGroup, Specification, Url, CommitCount, ChangeImpact, ChangeCategory, IssueType, RelatedTicketKeys, TicketOrder ' +
      'FROM note_tickets WHERE NoteId = $id ORDER BY TicketOrder',
      { $id: noteId }).rows;
    out += '## Tickets attributed (' + tickets.length + ')\n\n';
    if (tickets.length > 0) {
      // Order corrections last (group order) while preserving in-group order
      // deterministically via TicketOrder, then render the flat table.
      var ordered = tickets.slice().sort(function (a, b) {
        var ga = ticketGroup(a).order, gb = ticketGroup(b).order;
        if (ga !== gb) return ga - gb;
        return (Number(a.TicketOrder) || 0) - (Number(b.TicketOrder) || 0);
      });
      out += mdTable(['Key', 'Title', 'Resolution', 'Workgroup', 'Specification', 'Change impact', 'Category', 'See also', 'Commits'],
        ordered.map(function (t) {
          return [String(t.TicketKey || ''), String(t.Title || ''), String(t.Resolution || ''),
            String(t.WorkGroup || ''), String(t.Specification || ''),
            ticketGroup(t).label, String(t.ChangeCategory || ''),
            String(t.RelatedTicketKeys || '').split(';').map(function (s) { return s.trim(); }).filter(function (s) { return s.length > 0; }).join(', '),
            String(t.CommitCount || 0)];
        })) + '\n';
    } else {
      out += '_None._\n\n';
    }

    var structural = query(
      'SELECT SourcePath, ElementPath, ChangeKind, Detail, TicketKeys ' +
      'FROM note_structural_changes WHERE NoteId = $id ORDER BY ChangeOrder',
      { $id: noteId }).rows;
    out += '## Structural changes (' + structural.length + ')\n\n';
    if (structural.length > 0) {
      out += mdTable(['Element', 'Change', 'Detail', 'Tickets', 'Source'],
        structural.map(function (c) {
          return [String(c.ElementPath || ''), String(c.ChangeKind || ''), String(c.Detail || ''),
            String(c.TicketKeys || '').split(';').map(function (s) { return s.trim(); }).filter(function (s) { return s.length > 0; }).join(', '),
            String(c.SourcePath || '')];
        })) + '\n';
    } else {
      out += '_None._\n\n';
    }

    var extRefs = query(
      'SELECT ExtensionUrl, ExtensionName, ReplacementCoreElement, Rationale ' +
      'FROM note_extension_refs WHERE NoteId = $id ORDER BY RefOrder',
      { $id: noteId }).rows;
    out += '## Extensions cross-reference (' + extRefs.length + ')\n\n';
    if (extRefs.length > 0) {
      out += mdTable(['Extension', 'Replaced by core element', 'Rationale'],
        extRefs.map(function (r) {
          return [String(r.ExtensionName || r.ExtensionUrl || ''), String(r.ReplacementCoreElement || ''), String(r.Rationale || '')];
        })) + '\n';
    } else {
      out += '_None._\n\n';
    }

    return out;
  }

  function serializeNoteSection(title, body, emptyText) {
    var trimmed = (body == null) ? '' : String(body).trim();
    return '## ' + title + '\n\n' + (trimmed.length > 0 ? trimmed : '_' + emptyText + '_') + '\n\n';
  }

  // ---- htmlToMarkdown (notes-site only) -----------------------------------
  // Converts the two authored ballot-note HTML fields to markdown. DOM-based,
  // not regex: sanitize via the already-vendored DOMPurify, parse the cleaned
  // string in an inert detached document (no scripts run, no subresources load),
  // then walk the node tree. Unrecognized elements recurse into their children;
  // malformed markup degrades to its text content rather than throwing. Nothing
  // is ever assigned to the live document's innerHTML.

  function htmlToMarkdown(html) {
    if (html == null || String(html).trim() === '') return '';
    var src = String(html);
    var clean = (typeof DOMPurify !== 'undefined') ? DOMPurify.sanitize(src) : src;
    var doc;
    try {
      doc = new DOMParser().parseFromString(clean, 'text/html');
    } catch (e) {
      return src.replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim();
    }
    var md = mdNodesToMarkdown(doc.body, 0);
    return md.replace(/[ \t]+\n/g, '\n').replace(/\n{3,}/g, '\n\n').trim();
  }

  function mdNodesToMarkdown(parent, depth) {
    var out = '';
    var kids = parent.childNodes;
    for (var i = 0; i < kids.length; i++) out += mdNodeToMarkdown(kids[i], depth);
    return out;
  }

  function mdNodeToMarkdown(node, depth) {
    if (node.nodeType === 3) return String(node.nodeValue).replace(/\s+/g, ' ');
    if (node.nodeType !== 1) return '';
    var tag = node.tagName.toLowerCase();
    switch (tag) {
      case 'h1': case 'h2': case 'h3': case 'h4': case 'h5': case 'h6':
        return '\n\n' + mdRepeat('#', Number(tag.charAt(1))) + ' ' + mdNodesToMarkdown(node, depth).trim() + '\n\n';
      case 'p':
        return '\n\n' + mdNodesToMarkdown(node, depth).trim() + '\n\n';
      case 'br':
        return '  \n';
      case 'hr':
        return '\n\n---\n\n';
      case 'strong': case 'b':
        return '**' + mdNodesToMarkdown(node, depth) + '**';
      case 'em': case 'i':
        return '*' + mdNodesToMarkdown(node, depth) + '*';
      case 'code':
        return '`' + String(node.textContent || '').replace(/`/g, '') + '`';
      case 'pre':
        return '\n\n```\n' + String(node.textContent || '').replace(/\n$/, '') + '\n```\n\n';
      case 'a':
        var href = node.getAttribute('href');
        var text = mdNodesToMarkdown(node, depth).replace(/\s+/g, ' ').trim();
        return href ? '[' + (text || href) + '](' + href + ')' : text;
      case 'ul': case 'ol':
        return '\n' + mdListToMarkdown(node, tag === 'ol', depth) + '\n';
      case 'blockquote':
        var inner = mdNodesToMarkdown(node, depth).trim();
        return '\n\n' + inner.split('\n').map(function (line) { return '> ' + line; }).join('\n') + '\n\n';
      default:
        return mdNodesToMarkdown(node, depth);
    }
  }

  function mdListToMarkdown(listNode, ordered, depth) {
    var out = '';
    var idx = 0;
    var kids = listNode.childNodes;
    for (var i = 0; i < kids.length; i++) {
      var li = kids[i];
      if (li.nodeType !== 1 || li.tagName.toLowerCase() !== 'li') continue;
      idx++;
      var marker = ordered ? (idx + '. ') : '- ';
      var inlineText = '';
      var nested = '';
      for (var c = 0; c < li.childNodes.length; c++) {
        var child = li.childNodes[c];
        var childTag = (child.nodeType === 1) ? child.tagName.toLowerCase() : '';
        if (childTag === 'ul' || childTag === 'ol') {
          nested += mdListToMarkdown(child, childTag === 'ol', depth + 1);
        } else {
          inlineText += mdNodeToMarkdown(child, depth);
        }
      }
      out += mdRepeat('  ', depth) + marker + inlineText.replace(/\s+/g, ' ').trim() + '\n' + nested;
    }
    return out;
  }

  function mdRepeat(s, n) {
    var o = '';
    for (var i = 0; i < n; i++) o += s;
    return o;
  }

  App.init();
})();
