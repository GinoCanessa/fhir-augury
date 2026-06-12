// fhir-spec-review report SPA — vanilla JS, sql.js in the browser, hash router.
// SECURITY: all DB-derived content is rendered via textContent / createElement
// only — never innerHTML. No network access; the review DB is inlined as base64.

(function () {
  'use strict';

  var UNASSIGNED = '__unassigned__';
  var UNASSIGNED_LABEL = 'Unassigned';

  // Normalized work-group key shared by artifacts and pages. A bare `= ?` never
  // matches NULL, so the unassigned bucket is matched with an explicit predicate.
  var WG_KEY = "COALESCE(NULLIF(ResponsibleWorkGroupCode,''),'" + UNASSIGNED + "')";

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
        } else if (parts[0] === 'wg' && parts.length >= 2) {
          var key = decodeURIComponent(parts[1]);
          Views.workGroup(main, key);
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
      var groups = loadWorkGroupSummaries();

      main.appendChild(el('h2', null, 'Work groups (' + groups.length + ')'));

      var search = el('input', { class: 'search-box', type: 'search', placeholder: 'Type to filter work groups…', 'aria-label': 'Filter work groups' });
      main.appendChild(search);

      var tableWrap = el('div');
      main.appendChild(tableWrap);

      function renderTable(filter) {
        clearChildren(tableWrap);
        var needle = (filter || '').toLowerCase();
        var shown = groups.filter(function (g) {
          return needle === '' ||
            g.name.toLowerCase().indexOf(needle) >= 0 ||
            g.key.toLowerCase().indexOf(needle) >= 0;
        });

        if (shown.length === 0) {
          tableWrap.appendChild(el('p', { class: 'muted' }, 'No matching work groups.'));
          return;
        }

        var headers = ['Code', 'Work group', 'Artifacts', 'Pages', 'Conformant', 'Non-conformant',
          'Removed refs', 'Unknown', 'Typos', 'Image issues'];
        var rows = shown.map(function (g) {
          var code = g.key === UNASSIGNED ? '' : g.key;
          var link = el('a', { href: '#/wg/' + encodeURIComponent(g.key) }, g.name);
          return [code, link, num(g.artifacts), num(g.pages), num(g.conformant), num(g.nonConformant),
            num(g.removed), num(g.unknown), num(g.typos), num(g.images)];
        });
        tableWrap.appendChild(buildTable(headers, rows, [false, false, true, true, true, true, true, true, true, true]));
      }

      search.addEventListener('input', function () { renderTable(search.value); });
      renderTable('');
    },

    workGroup: function (main, key) {
      var name = workGroupName(key);
      setBreadcrumb([{ label: name, href: null }]);
      main.appendChild(el('h2', null, name));

      var artifacts = loadArtifacts(key);
      var pages = loadPages(key);

      // Artifacts
      main.appendChild(el('h3', null, 'Artifacts (' + artifacts.length + ')'));
      if (artifacts.length > 0) {
        var aHeaders = ['FHIR id', 'Name', 'Type', 'Source dir', 'Definition', 'Intro', 'Notes', 'Status', 'Standards'];
        var aRows = artifacts.map(function (a) {
          return [a.FhirId, a.Name, a.ArtifactType || '', boolText(a.SourceDirectoryExists),
            boolText(a.SourceDefinitionExists), a.IntroPageFilename || '', a.NotesPageFilename || '',
            a.Status || '', a.StandardsStatus || ''];
        });
        main.appendChild(buildTable(aHeaders, aRows));
      } else {
        main.appendChild(el('p', { class: 'muted' }, 'No artifacts.'));
      }

      // Pages
      main.appendChild(el('h3', null, 'Pages (' + pages.length + ')'));
      if (pages.length > 0) {
        var pHeaders = ['Page', 'Maturity', 'Standards', 'Conformant', 'Non-conf.', 'Removed',
          'Unknown', 'Typos', 'Images', 'Prior ver.', 'Zulip', 'Confluence'];
        var pAlign = [false, false, false, true, true, true, true, true, true, true, true, true];
        var pRows = pages.map(function (p) {
          return [p.PageFileName, p.MaturityLabel || '', p.StandardsStatus || '',
            num(p.ConformantTotalCount), num(p.NonConformantTotalCount), num(p.RemovedFhirArtifactCount),
            num(p.UnknownWordCount), num(p.TypoWordCount), num(p.ImagesWithIssuesCount),
            num(p.PriorFhirVersionReferenceCount), num(p.ZulipLinkCount), num(p.ConfluenceLinkCount)];
        });
        main.appendChild(buildTable(pHeaders, pRows, pAlign));
      } else {
        main.appendChild(el('p', { class: 'muted' }, 'No pages.'));
      }

      // Findings: removed FHIR artifact references
      var removed = loadRemovedRefs(key);
      if (removed.length > 0) {
        main.appendChild(el('h3', null, 'Removed FHIR artifact references (' + removed.length + ')'));
        main.appendChild(buildTable(
          ['Page', 'Word', 'Class', 'Source pointer', 'Snippet'],
          removed.map(function (r) {
            return [r.PageFileName, r.Word, r.ArtifactClass || '', r.SourceRelativePath || '', snippet(r.ContextSnippet)];
          })));
      }

      // Findings: unknown words & typos
      var unknown = loadUnknownWords(key);
      if (unknown.length > 0) {
        main.appendChild(el('h3', null, 'Unknown words & typos (' + unknown.length + ')'));
        main.appendChild(buildTable(
          ['Page', 'Word', 'Typo?', 'Correction', 'Source pointer', 'Snippet'],
          unknown.map(function (u) {
            return [u.PageFileName, u.Word, u.IsTypo ? 'yes' : 'no', u.Correction || '',
              u.SourceRelativePath || '', snippet(u.ContextSnippet)];
          })));
      }

      // Findings: image issues
      var images = loadImageIssues(key);
      if (images.length > 0) {
        main.appendChild(el('h3', null, 'Image issues (' + images.length + ')'));
        main.appendChild(buildTable(
          ['Page', 'Source', 'Missing alt', 'Not in figure', 'Snippet'],
          images.map(function (im) {
            return [im.PageFileName, im.Source, im.MissingAlt ? 'yes' : 'no',
              im.NotInFigure ? 'yes' : 'no', snippet(im.ContextSnippet)];
          })));
      }

      // Unassigned-only: removed-baseline entities + duplicate artifact keys
      if (key === UNASSIGNED) {
        var baseline = loadRemovedBaseline();
        if (baseline.length > 0) {
          main.appendChild(el('h3', null, 'Removed since baseline (' + baseline.length + ')'));
          main.appendChild(buildTable(
            ['Kind', 'Name', 'Baseline'],
            baseline.map(function (b) { return [b.EntityKind, b.Name, b.BaselineRelease]; })));
        }

        var dups = loadDuplicateKeys();
        if (dups.length > 0) {
          main.appendChild(el('h3', null, 'Duplicate artifact keys (' + dups.length + ')'));
          main.appendChild(buildTable(
            ['FHIR id', 'Kept', 'Skipped', 'Kept URL', 'Skipped URL', 'Type'],
            dups.map(function (d) {
              return [d.FhirId, d.KeptName, d.DuplicateName, d.KeptCanonicalUrl || '',
                d.DuplicateCanonicalUrl || '', d.ArtifactType || ''];
            })));
        }
      }
    }
  };

  // ---- data access --------------------------------------------------------

  function loadWorkGroupSummaries() {
    var byKey = {};
    function ensure(key) {
      if (!byKey[key]) {
        byKey[key] = { key: key, name: null, artifacts: 0, pages: 0, conformant: 0,
          nonConformant: 0, removed: 0, unknown: 0, typos: 0, images: 0 };
      }
      return byKey[key];
    }

    var ar = query(
      'SELECT ' + WG_KEY + ' AS k, MAX(ResponsibleWorkGroupName) AS name, COUNT(*) AS n ' +
      'FROM artifacts GROUP BY k', null);
    for (var i = 0; i < ar.rows.length; i++) {
      var a = ar.rows[i];
      var ga = ensure(String(a.k));
      ga.artifacts = Number(a.n) || 0;
      if (a.name && !ga.name) ga.name = String(a.name);
    }

    var pr = query(
      'SELECT ' + WG_KEY + ' AS k, MAX(ResponsibleWorkGroupName) AS name, COUNT(*) AS n, ' +
      'SUM(COALESCE(ConformantTotalCount,0)) AS conf, SUM(COALESCE(NonConformantTotalCount,0)) AS nonconf, ' +
      'SUM(COALESCE(RemovedFhirArtifactCount,0)) AS rem, SUM(COALESCE(UnknownWordCount,0)) AS unk, ' +
      'SUM(COALESCE(TypoWordCount,0)) AS typ, SUM(COALESCE(ImagesWithIssuesCount,0)) AS img ' +
      'FROM pages GROUP BY k', null);
    for (var j = 0; j < pr.rows.length; j++) {
      var p = pr.rows[j];
      var gp = ensure(String(p.k));
      gp.pages = Number(p.n) || 0;
      gp.conformant = Number(p.conf) || 0;
      gp.nonConformant = Number(p.nonconf) || 0;
      gp.removed = Number(p.rem) || 0;
      gp.unknown = Number(p.unk) || 0;
      gp.typos = Number(p.typ) || 0;
      gp.images = Number(p.img) || 0;
      if (p.name && !gp.name) gp.name = String(p.name);
    }

    // Surface the Unassigned bucket whenever removed-baseline or duplicate-key
    // rows exist, even if no artifact/page is itself unassigned.
    if (scalar('SELECT 1 FROM removed_baseline_entities LIMIT 1') !== null ||
        scalar('SELECT 1 FROM duplicate_artifact_keys LIMIT 1') !== null) {
      ensure(UNASSIGNED);
    }

    var list = [];
    for (var k in byKey) {
      if (!byKey.hasOwnProperty(k)) continue;
      var g = byKey[k];
      g.name = g.key === UNASSIGNED ? UNASSIGNED_LABEL : (g.name || g.key);
      list.push(g);
    }
    list.sort(function (x, y) { return x.name.toLowerCase().localeCompare(y.name.toLowerCase()); });
    return list;
  }

  function workGroupName(key) {
    if (key === UNASSIGNED) return UNASSIGNED_LABEL;
    var n = scalar(
      'SELECT ResponsibleWorkGroupName FROM pages WHERE ResponsibleWorkGroupCode = ? AND ResponsibleWorkGroupName IS NOT NULL ' +
      'UNION SELECT ResponsibleWorkGroupName FROM artifacts WHERE ResponsibleWorkGroupCode = ? AND ResponsibleWorkGroupName IS NOT NULL LIMIT 1',
      [key, key]);
    return n !== null ? String(n) : key;
  }

  function loadArtifacts(key) {
    return query(
      'SELECT FhirId, Name, ArtifactType, SourceDirectoryExists, SourceDefinitionExists, ' +
      'IntroPageFilename, NotesPageFilename, Status, StandardsStatus FROM artifacts ' +
      'WHERE ' + wgWhere(key) + ' ORDER BY FhirId COLLATE NOCASE', wgParams(key)).rows;
  }

  function loadPages(key) {
    return query(
      'SELECT PageFileName, MaturityLabel, StandardsStatus, ConformantTotalCount, NonConformantTotalCount, ' +
      'RemovedFhirArtifactCount, UnknownWordCount, TypoWordCount, ImagesWithIssuesCount, ' +
      'PriorFhirVersionReferenceCount, ZulipLinkCount, ConfluenceLinkCount FROM pages ' +
      'WHERE ' + wgWhere(key) + ' ORDER BY PageFileName COLLATE NOCASE', wgParams(key)).rows;
  }

  function loadRemovedRefs(key) {
    return query(
      'SELECT p.PageFileName, p.SourceRelativePath, r.Word, r.ArtifactClass, r.ContextSnippet ' +
      'FROM page_removed_fhir_artifacts r JOIN pages p ON p.Id = r.PageId ' +
      'WHERE ' + wgWhere(key, 'p.') + ' ORDER BY p.PageFileName COLLATE NOCASE, r.Word COLLATE NOCASE',
      wgParams(key)).rows;
  }

  function loadUnknownWords(key) {
    return query(
      'SELECT p.PageFileName, p.SourceRelativePath, u.Word, u.IsTypo, u.Correction, u.ContextSnippet ' +
      'FROM page_unknown_words u JOIN pages p ON p.Id = u.PageId ' +
      'WHERE ' + wgWhere(key, 'p.') + ' ORDER BY p.PageFileName COLLATE NOCASE, u.Word COLLATE NOCASE',
      wgParams(key)).rows;
  }

  function loadImageIssues(key) {
    return query(
      'SELECT p.PageFileName, i.Source, i.MissingAlt, i.NotInFigure, i.ContextSnippet ' +
      'FROM page_images i JOIN pages p ON p.Id = i.PageId ' +
      'WHERE ' + wgWhere(key, 'p.') + ' ORDER BY p.PageFileName COLLATE NOCASE, i.Source COLLATE NOCASE',
      wgParams(key)).rows;
  }

  function loadRemovedBaseline() {
    return query('SELECT EntityKind, Name, BaselineRelease FROM removed_baseline_entities ORDER BY EntityKind, Name', null).rows;
  }

  function loadDuplicateKeys() {
    return query(
      'SELECT FhirId, KeptName, DuplicateName, KeptCanonicalUrl, DuplicateCanonicalUrl, ArtifactType ' +
      'FROM duplicate_artifact_keys ORDER BY FhirId', null).rows;
  }

  // For the unassigned bucket a bare `= ?` never matches NULL, so use an
  // explicit IS NULL / '' predicate; otherwise match the code directly.
  function wgWhere(key, prefix) {
    var col = (prefix || '') + 'ResponsibleWorkGroupCode';
    return key === UNASSIGNED
      ? '(' + col + " IS NULL OR " + col + " = '')"
      : col + ' = ?';
  }

  function wgParams(key) {
    return key === UNASSIGNED ? null : [key];
  }

  // ---- rendering helpers --------------------------------------------------

  function renderProvenance() {
    var node = document.getElementById('provenance');
    if (!node) return;
    var run = (typeof window.__RUN__ === 'object' && window.__RUN__) ? window.__RUN__ : null;
    clearChildren(node);
    if (!run) {
      node.appendChild(document.createTextNode('No review run recorded.'));
      return;
    }
    appendKv(node, 'Repository ', run.repo);
    node.appendChild(document.createTextNode(' · '));
    appendKv(node, 'Build ', run.build);
    node.appendChild(document.createTextNode(' · '));
    appendKv(node, 'Baseline ', run.baseline);
    node.appendChild(document.createTextNode(' · Run at ' + (run.runAt || '')));
  }

  function appendKv(parent, label, value) {
    parent.appendChild(document.createTextNode(label));
    parent.appendChild(el('strong', null, value != null ? String(value) : ''));
  }

  function buildTable(headers, rows, numericCols) {
    var table = el('table');
    var thead = el('thead');
    var htr = el('tr');
    for (var h = 0; h < headers.length; h++) htr.appendChild(el('th', null, headers[h]));
    thead.appendChild(htr);
    table.appendChild(thead);

    var tbody = el('tbody');
    for (var i = 0; i < rows.length; i++) {
      var tr = el('tr');
      var cells = rows[i];
      for (var c = 0; c < cells.length; c++) {
        var isNum = numericCols && numericCols[c];
        var cell = cells[c];
        var td;
        if (cell instanceof Node) {
          td = el('td', isNum ? { class: 'num' } : null);
          td.appendChild(cell);
        } else {
          td = el('td', isNum ? { class: 'num' } : null, cell == null ? '' : String(cell));
        }
        tr.appendChild(td);
      }
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    return table;
  }

  function snippet(text) {
    if (text == null || text === '') return '';
    return el('span', { class: 'snippet' }, String(text));
  }

  function num(value) {
    var n = Number(value) || 0;
    return String(n);
  }

  function boolText(value) {
    if (value === null || value === undefined) return '';
    return Number(value) !== 0 ? 'yes' : 'no';
  }

  // ---- sql / dom utilities ------------------------------------------------

  function query(sql, params) {
    var stmt = db.prepare(sql);
    if (params) stmt.bind(params);
    var columns = stmt.getColumnNames();
    var rows = [];
    while (stmt.step()) rows.push(stmt.getAsObject());
    stmt.free();
    return { columns: columns, rows: rows };
  }

  function scalar(sql, params) {
    var res = query(sql, params || null);
    if (res.rows.length === 0) return null;
    var row = res.rows[0];
    for (var k in row) { if (row.hasOwnProperty(k)) return row[k]; }
    return null;
  }

  function clearChildren(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  function el(tag, attrs, children) {
    var node = document.createElement(tag);
    if (attrs) {
      for (var k in attrs) {
        if (!attrs.hasOwnProperty(k)) continue;
        if (k === 'class') node.className = attrs[k];
        else node.setAttribute(k, attrs[k]);
      }
    }
    if (children != null) {
      if (typeof children === 'string') {
        node.textContent = children;
      } else if (Array.isArray(children)) {
        for (var i = 0; i < children.length; i++) {
          var c = children[i];
          if (c == null) continue;
          if (typeof c === 'string') node.appendChild(document.createTextNode(c));
          else node.appendChild(c);
        }
      } else if (children instanceof Node) {
        node.appendChild(children);
      } else {
        node.textContent = String(children);
      }
    }
    return node;
  }

  function renderError(main, msg) {
    clearChildren(main);
    main.appendChild(el('p', { class: 'error' }, msg));
  }

  function setBreadcrumb(tail) {
    var bc = document.getElementById('breadcrumb');
    if (!bc) return;
    clearChildren(bc);
    var hasTail = Array.isArray(tail) && tail.length > 0;
    var parts = [{ label: 'All work groups', href: hasTail ? '#/' : null }];
    if (hasTail) {
      for (var i = 0; i < tail.length; i++) parts.push(tail[i]);
    }
    for (var p = 0; p < parts.length; p++) {
      if (p > 0) bc.appendChild(document.createTextNode(' › '));
      var part = parts[p];
      if (part.href) bc.appendChild(el('a', { href: part.href }, part.label));
      else bc.appendChild(document.createTextNode(part.label));
    }
  }

  App.init();
})();
