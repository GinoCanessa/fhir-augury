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

      window.addEventListener('hashchange', App.route);
      App.route();
    },

    route: function () {
      var main = document.getElementById('app');
      clearChildren(main);
      clearCopyExport();
      var hash = window.location.hash || '#/';
      var stripped = hash.replace(/^#\/?/, '');
      var parts = stripped.split('/').filter(function (p) { return p.length > 0; });

      try {
        if (parts.length === 0) {
          setBreadcrumb([]);
          Views.landing(main);
        } else if (parts[0] === 'wg' && parts[2] === 'page' && parts[3]) {
          var pageCode = decodeURIComponent(parts[1]);
          Views.page(main, pageCode, Number(parts[3]));
        } else if (parts[0] === 'wg' && parts[2] === 'artifact' && parts[3]) {
          var artifactCode = decodeURIComponent(parts[1]);
          Views.artifact(main, artifactCode, Number(parts[3]));
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
          var idLink = el('a', { href: '#/wg/' + encodeURIComponent(key) + '/artifact/' + a.Id }, a.FhirId);
          return [idLink, a.Name, a.ArtifactType || '', boolText(a.SourceDirectoryExists),
            boolText(a.SourceDefinitionExists), a.IntroPageFilename || '', a.NotesPageFilename || '',
            a.Status || '', a.StandardsStatus || ''];
        });
        main.appendChild(buildTable(aHeaders, aRows));
      } else {
        main.appendChild(el('p', { class: 'muted' }, 'No artifacts.'));
      }

      // Pages (narrative pages only — artifact intro/notes live on the artifact detail)
      main.appendChild(el('h3', null, 'Pages (' + pages.length + ')'));
      if (pages.length > 0) {
        var pHeaders = ['Page', 'Maturity', 'Standards', 'Conformant', 'Non-conf.', 'Removed',
          'Unknown', 'Typos', 'Images', 'Prior ver.', 'Zulip', 'Confluence'];
        var pAlign = [false, false, false, true, true, true, true, true, true, true, true, true];
        var pRows = pages.map(function (p) {
          var pageLink = el('a', { href: '#/wg/' + encodeURIComponent(key) + '/page/' + p.Id }, p.PageFileName);
          return [pageLink, p.MaturityLabel || '', p.StandardsStatus || '',
            num(p.ConformantTotalCount), num(p.NonConformantTotalCount), num(p.RemovedFhirArtifactCount),
            num(p.UnknownWordCount), num(p.TypoWordCount), num(p.ImagesWithIssuesCount),
            num(p.PriorFhirVersionReferenceCount), num(p.ZulipLinkCount), num(p.ConfluenceLinkCount)];
        });
        main.appendChild(buildTable(pHeaders, pRows, pAlign));
      } else {
        main.appendChild(el('p', { class: 'muted' }, 'No pages.'));
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
    },

    page: function (main, code, id) {
      var page = loadPageById(id);
      if (!page) {
        setBreadcrumb([{ label: 'Not found', href: null }]);
        renderError(main, 'Page not found: ' + id);
        return;
      }
      setBreadcrumb([
        { label: wgBreadcrumbLabel(code), href: '#/wg/' + encodeURIComponent(code) },
        { label: String(page.PageFileName), href: null }
      ]);
      main.appendChild(el('h2', null, String(page.PageFileName)));
      renderPageDetailBlock(main, page);
      setCopyExport(function () { return serializePageMarkdown(page); });
    },

    artifact: function (main, code, id) {
      var artifact = loadArtifactById(id);
      if (!artifact) {
        setBreadcrumb([{ label: 'Not found', href: null }]);
        renderError(main, 'Artifact not found: ' + id);
        return;
      }
      setBreadcrumb([
        { label: wgBreadcrumbLabel(code), href: '#/wg/' + encodeURIComponent(code) },
        { label: String(artifact.FhirId), href: null }
      ]);
      main.appendChild(el('h2', null, 'Artifact ' + String(artifact.Name) + ' (' + String(artifact.FhirId) + ')'));

      // Urgent Item Checklist (static reviewer reminders).
      main.appendChild(el('h3', null, 'Urgent Item Checklist'));
      var checklist = el('ul');
      [
        'Confirm workgroup disposition vote has been recorded and sent to FMG.',
        'Confirm the resource boundaries and relationships are documented.',
        'Confirm every element has a clear definition and short description.',
        'Confirm required bindings and search parameters are correct.',
        'Confirm examples validate against the current build.'
      ].forEach(function (item) { checklist.appendChild(el('li', null, item)); });
      main.appendChild(checklist);

      // Inlined intro + notes page-detail blocks (intro first, then notes).
      var pages = loadArtifactPages(id);
      var ordered = orderArtifactPages(pages, artifact);
      for (var i = 0; i < ordered.length; i++) {
        main.appendChild(el('h3', null, ordered[i].label));
        renderPageDetailBlock(main, ordered[i].page);
      }

      // Element Review
      var elements = loadArtifactElements(id);
      main.appendChild(el('h3', null, 'Element Review (' + elements.length + ')'));
      if (elements.length > 0) {
        main.appendChild(buildTable(
          ['Path', 'Is Required', 'Max Cardinality', 'Trial Use', 'Has fixed[x]', 'Has pattern[x]',
            'Required Binding', 'External Required Binding', 'meaningWhenMissing', 'Is Modifier'],
          elements.map(function (e) {
            return [e.Path, boolText(e.IsRequired), e.MaxCardinality || '', boolText(e.IsTrialUse),
              boolText(e.HasFixed), boolText(e.HasPattern), boolText(e.RequiredBinding),
              boolText(e.ExternalRequiredBinding), e.MeaningWhenMissing || '', boolText(e.IsModifier)];
          })));
      } else {
        main.appendChild(el('p', { class: 'muted' }, 'No elements found.'));
      }

      // Operations
      var operations = loadArtifactOperations(id);
      main.appendChild(el('h3', null, 'Operations (' + operations.length + ')'));
      if (operations.length > 0) {
        main.appendChild(buildTable(
          ['Id', 'Code', 'Name', 'Kind', 'Status', 'Standards', 'FMM', 'Description'],
          operations.map(function (o) {
            return [o.OperationId, o.Code || '', o.Name || '', o.OperationKind || '', o.Status || '',
              o.StandardsStatus || '', o.FhirMaturity == null ? '' : String(o.FhirMaturity), o.Description || ''];
          })));
      } else {
        main.appendChild(el('p', { class: 'muted' }, 'No operations found.'));
      }

      // Search Parameters
      var searchParams = loadArtifactSearchParameters(id);
      main.appendChild(el('h3', null, 'Search Parameters (' + searchParams.length + ')'));
      if (searchParams.length > 0) {
        main.appendChild(buildTable(
          ['Id', 'Name', 'Publication Status', 'FMM', 'Standards Status', 'IsExperimental',
            'WorkGroup', 'Search Type', 'Description'],
          searchParams.map(function (s) {
            return [s.SearchParamId, s.Name || '', s.Status || '',
              s.FhirMaturity == null ? '' : String(s.FhirMaturity), s.StandardsStatus || '',
              boolText(s.IsExperimental), s.WorkGroup || '', s.SearchType || '', s.Description || ''];
          })));
      } else {
        main.appendChild(el('p', { class: 'muted' }, 'No search parameters found.'));
      }

      setCopyExport(function () { return serializeArtifactMarkdown(artifact, id); });
    }
  };

  // Orders an artifact's linked pages intro-first then notes, labelling each by
  // matching the artifact's Intro/Notes page filenames; any other linked page
  // keeps its file name as the label and sorts last.
  function orderArtifactPages(pages, artifact) {
    var result = [];
    for (var i = 0; i < pages.length; i++) {
      var p = pages[i];
      var label;
      var rank;
      if (artifact.IntroPageFilename != null && p.PageFileName === artifact.IntroPageFilename) {
        label = 'Information Page'; rank = 0;
      } else if (artifact.NotesPageFilename != null && p.PageFileName === artifact.NotesPageFilename) {
        label = 'Notes Page'; rank = 1;
      } else {
        label = String(p.PageFileName); rank = 2;
      }
      result.push({ page: p, label: label, rank: rank });
    }
    result.sort(function (a, b) { return a.rank - b.rank; });
    return result;
  }

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
      'SELECT Id, FhirId, Name, ArtifactType, SourceDirectoryExists, SourceDefinitionExists, ' +
      'IntroPageFilename, NotesPageFilename, Status, StandardsStatus FROM artifacts ' +
      'WHERE ' + wgWhere(key) + ' ORDER BY FhirId COLLATE NOCASE', wgParams(key)).rows;
  }

  function loadPages(key) {
    return query(
      'SELECT Id, PageFileName, MaturityLabel, StandardsStatus, ConformantTotalCount, NonConformantTotalCount, ' +
      'RemovedFhirArtifactCount, UnknownWordCount, TypoWordCount, ImagesWithIssuesCount, ' +
      'PriorFhirVersionReferenceCount, ZulipLinkCount, ConfluenceLinkCount FROM pages ' +
      'WHERE ' + wgWhere(key) + ' AND ArtifactId IS NULL ORDER BY PageFileName COLLATE NOCASE', wgParams(key)).rows;
  }

  function loadRemovedBaseline() {
    return query('SELECT EntityKind, Name, BaselineRelease FROM removed_baseline_entities ORDER BY EntityKind, Name', null).rows;
  }

  function loadDuplicateKeys() {
    return query(
      'SELECT FhirId, KeptName, DuplicateName, KeptCanonicalUrl, DuplicateCanonicalUrl, ArtifactType ' +
      'FROM duplicate_artifact_keys ORDER BY FhirId', null).rows;
  }

  function loadPageById(id) {
    var res = query('SELECT * FROM pages WHERE Id = ?', [id]);
    return res.rows.length > 0 ? res.rows[0] : null;
  }

  function loadRemovedRefsForPage(pageId) {
    return query(
      'SELECT Word, ArtifactClass, ContextSnippet FROM page_removed_fhir_artifacts ' +
      'WHERE PageId = ? ORDER BY Word COLLATE NOCASE', [pageId]).rows;
  }

  function loadUnknownWordsForPage(pageId) {
    return query(
      'SELECT Word, IsTypo, Correction, ContextSnippet FROM page_unknown_words ' +
      'WHERE PageId = ? ORDER BY Word COLLATE NOCASE', [pageId]).rows;
  }

  function loadImageIssuesForPage(pageId) {
    return query(
      'SELECT Source, MissingAlt, NotInFigure, ContextSnippet FROM page_images ' +
      'WHERE PageId = ? ORDER BY Source COLLATE NOCASE', [pageId]).rows;
  }

  function loadArtifactById(id) {
    var res = query('SELECT * FROM artifacts WHERE Id = ?', [id]);
    return res.rows.length > 0 ? res.rows[0] : null;
  }

  function loadArtifactPages(artifactId) {
    return query('SELECT * FROM pages WHERE ArtifactId = ? ORDER BY PageFileName COLLATE NOCASE', [artifactId]).rows;
  }

  function loadArtifactElements(artifactId) {
    return query(
      'SELECT Path, IsRequired, MaxCardinality, IsTrialUse, HasFixed, HasPattern, ' +
      'RequiredBinding, ExternalRequiredBinding, MeaningWhenMissing, IsModifier ' +
      'FROM artifact_elements WHERE ArtifactId = ? ORDER BY ElementOrder', [artifactId]).rows;
  }

  function loadArtifactOperations(artifactId) {
    return query(
      'SELECT OperationId, Code, Name, OperationKind, Status, StandardsStatus, FhirMaturity, Description ' +
      'FROM artifact_operations WHERE ArtifactId = ? ORDER BY OperationOrder', [artifactId]).rows;
  }

  function loadArtifactSearchParameters(artifactId) {
    return query(
      'SELECT SearchParamId, Name, Status, FhirMaturity, StandardsStatus, IsExperimental, ' +
      'WorkGroup, SearchType, Description ' +
      'FROM artifact_search_parameters WHERE ArtifactId = ? ORDER BY ParamOrder', [artifactId]).rows;
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

  function wgBreadcrumbLabel(code) {
    if (code === UNASSIGNED) return UNASSIGNED_LABEL;
    var name = workGroupName(code);
    return name === code ? code : name + ' (' + code + ')';
  }

  // Builds the full single-page detail block (General Information, Conformance
  // Language Summary, removed-literal / unknown-word / image-issue tables, and
  // the JSON-array marker/note lists) into `container`. Shared by the page view
  // and the artifact view (which inlines its intro/notes pages).
  function renderPageDetailBlock(container, page) {
    // General Information
    container.appendChild(el('h3', null, 'General Information'));
    var info = [
      ['Page File Name', page.PageFileName == null ? '' : String(page.PageFileName)],
      ['Responsible Workgroup', page.ResponsibleWorkGroupName == null ? '' : String(page.ResponsibleWorkGroupName)],
      ['Maturity Label', page.MaturityLabel == null ? '' : String(page.MaturityLabel)],
      ['Maturity Level', page.MaturityLevel == null ? '' : String(page.MaturityLevel)],
      ['Standards Status', page.StandardsStatus == null ? '' : String(page.StandardsStatus)],
      ['Exists In publish.ini', boolText(page.ExistsInPublishIni)],
      ['Exists In Source', boolText(page.ExistsInSource)],
      ['Exists In Baseline Site', boolText(page.ExistsInBaselineSite)],
      ['deprecated literal count', num(page.DeprecatedLiteralCount)],
      ['Zulip Link Count', num(page.ZulipLinkCount)],
      ['Confluence Link Count', num(page.ConfluenceLinkCount)],
      ['Prior FHIR version reference count', num(page.PriorFhirVersionReferenceCount)]
    ];
    container.appendChild(buildTable(['Field', 'Value'], info, [false, false]));

    // Conformance Language Summary
    container.appendChild(el('h3', null, 'Conformance Language Summary'));
    var conf = [
      ['SHALL', num(page.ConformantShallCount), num(page.NonConformantShallCount)],
      ['SHALL NOT', num(page.ConformantShallNotCount), num(page.NonConformantShallNotCount)],
      ['SHOULD', num(page.ConformantShouldCount), num(page.NonConformantShouldCount)],
      ['SHOULD NOT', num(page.ConformantShouldNotCount), num(page.NonConformantShouldNotCount)],
      ['MAY', num(page.ConformantMayCount), num(page.NonConformantMayCount)],
      ['MAY NOT', num(page.ConformantMayNotCount), num(page.NonConformantMayNotCount)]
    ];
    container.appendChild(buildTable(['Keyword', 'Conformant', 'Non-conformant'], conf, [false, true, true]));

    // Possibly Removed FHIR Artifact Literals
    var removed = loadRemovedRefsForPage(page.Id);
    container.appendChild(el('h3', null, 'Possibly Removed FHIR Artifact Literals (' + removed.length + ')'));
    if (removed.length > 0) {
      container.appendChild(buildTable(
        ['Word', 'Class', 'Snippet'],
        removed.map(function (r) { return [r.Word, r.ArtifactClass || '', snippet(r.ContextSnippet)]; })));
    } else {
      container.appendChild(el('p', { class: 'muted' }, 'No possibly-removed literals found.'));
    }

    // Unknown Words
    var unknown = loadUnknownWordsForPage(page.Id);
    container.appendChild(el('h3', null, 'Unknown Words (' + unknown.length + ')'));
    if (unknown.length > 0) {
      container.appendChild(buildTable(
        ['Word', 'Typo?', 'Correction', 'Snippet'],
        unknown.map(function (u) {
          return [u.Word, u.IsTypo ? 'yes' : 'no', u.Correction || '', snippet(u.ContextSnippet)];
        })));
    } else {
      container.appendChild(el('p', { class: 'muted' }, 'No unknown words found.'));
    }

    // Images with Issues
    var images = loadImageIssuesForPage(page.Id);
    container.appendChild(el('h3', null, 'Images with Issues (' + images.length + ')'));
    if (images.length > 0) {
      container.appendChild(buildTable(
        ['Source', 'Missing alt', 'Not in figure', 'Snippet'],
        images.map(function (im) {
          return [im.Source, im.MissingAlt ? 'yes' : 'no', im.NotInFigure ? 'yes' : 'no', snippet(im.ContextSnippet)];
        })));
    } else {
      container.appendChild(el('p', { class: 'muted' }, 'No images with issues found.'));
    }

    // Possible Incomplete Markers / Reader Review Notes (JSON-array TEXT columns)
    renderJsonList(container, 'Possible Incomplete Markers', page.PossibleIncompleteMarkers);
    renderJsonList(container, 'Reader Review Notes', page.ReaderReviewNotes);
  }

  // Parses a JSON-array TEXT column and renders it as a <ul>. Parsing is guarded
  // so malformed JSON degrades to an empty section rather than throwing.
  function renderJsonList(container, title, jsonText) {
    var items = [];
    if (jsonText != null && jsonText !== '') {
      try {
        var parsed = JSON.parse(String(jsonText));
        if (Array.isArray(parsed)) items = parsed;
      } catch (err) {
        items = [];
      }
    }
    container.appendChild(el('h3', null, title + ' (' + items.length + ')'));
    if (items.length === 0) {
      container.appendChild(el('p', { class: 'muted' }, 'None.'));
      return;
    }
    var ul = el('ul');
    for (var i = 0; i < items.length; i++) {
      ul.appendChild(el('li', null, items[i] == null ? '' : String(items[i])));
    }
    container.appendChild(ul);
  }

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

  // ---- Copy for AI serializers (fhir-spec-review specific) ----------------

  // Emits the page-detail sections (mirrors renderPageDetailBlock) at the given
  // heading level so the page view can nest them under an H1 and the artifact
  // view can nest them under an H2.
  function serializePageSections(page, lvl) {
    var out = '';

    out += lvl + ' General Information\n\n';
    out += mdTable(['Field', 'Value'], [
      ['Page File Name', page.PageFileName == null ? '' : String(page.PageFileName)],
      ['Responsible Workgroup', page.ResponsibleWorkGroupName == null ? '' : String(page.ResponsibleWorkGroupName)],
      ['Maturity Label', page.MaturityLabel == null ? '' : String(page.MaturityLabel)],
      ['Maturity Level', page.MaturityLevel == null ? '' : String(page.MaturityLevel)],
      ['Standards Status', page.StandardsStatus == null ? '' : String(page.StandardsStatus)],
      ['Exists In publish.ini', boolText(page.ExistsInPublishIni)],
      ['Exists In Source', boolText(page.ExistsInSource)],
      ['Exists In Baseline Site', boolText(page.ExistsInBaselineSite)],
      ['deprecated literal count', num(page.DeprecatedLiteralCount)],
      ['Zulip Link Count', num(page.ZulipLinkCount)],
      ['Confluence Link Count', num(page.ConfluenceLinkCount)],
      ['Prior FHIR version reference count', num(page.PriorFhirVersionReferenceCount)]
    ]) + '\n';

    out += lvl + ' Conformance Language Summary\n\n';
    out += mdTable(['Keyword', 'Conformant', 'Non-conformant'], [
      ['SHALL', num(page.ConformantShallCount), num(page.NonConformantShallCount)],
      ['SHALL NOT', num(page.ConformantShallNotCount), num(page.NonConformantShallNotCount)],
      ['SHOULD', num(page.ConformantShouldCount), num(page.NonConformantShouldCount)],
      ['SHOULD NOT', num(page.ConformantShouldNotCount), num(page.NonConformantShouldNotCount)],
      ['MAY', num(page.ConformantMayCount), num(page.NonConformantMayCount)],
      ['MAY NOT', num(page.ConformantMayNotCount), num(page.NonConformantMayNotCount)]
    ]) + '\n';

    var removed = loadRemovedRefsForPage(page.Id);
    out += lvl + ' Possibly Removed FHIR Artifact Literals (' + removed.length + ')\n\n';
    if (removed.length > 0) {
      out += mdTable(['Word', 'Class', 'Snippet'],
        removed.map(function (r) { return [r.Word, r.ArtifactClass || '', r.ContextSnippet || '']; })) + '\n';
    } else {
      out += '_No possibly-removed literals found._\n\n';
    }

    var unknown = loadUnknownWordsForPage(page.Id);
    out += lvl + ' Unknown Words (' + unknown.length + ')\n\n';
    if (unknown.length > 0) {
      out += mdTable(['Word', 'Typo?', 'Correction', 'Snippet'],
        unknown.map(function (u) { return [u.Word, u.IsTypo ? 'yes' : 'no', u.Correction || '', u.ContextSnippet || '']; })) + '\n';
    } else {
      out += '_No unknown words found._\n\n';
    }

    var images = loadImageIssuesForPage(page.Id);
    out += lvl + ' Images with Issues (' + images.length + ')\n\n';
    if (images.length > 0) {
      out += mdTable(['Source', 'Missing alt', 'Not in figure', 'Snippet'],
        images.map(function (im) { return [im.Source, im.MissingAlt ? 'yes' : 'no', im.NotInFigure ? 'yes' : 'no', im.ContextSnippet || '']; })) + '\n';
    } else {
      out += '_No images with issues found._\n\n';
    }

    out += serializeJsonList(lvl, 'Possible Incomplete Markers', page.PossibleIncompleteMarkers);
    out += serializeJsonList(lvl, 'Reader Review Notes', page.ReaderReviewNotes);

    return out;
  }

  // Parses a JSON-array TEXT column and emits a markdown bullet list. Parsing is
  // guarded so malformed JSON degrades to an empty section rather than throwing.
  function serializeJsonList(lvl, title, jsonText) {
    var items = [];
    if (jsonText != null && jsonText !== '') {
      try {
        var parsed = JSON.parse(String(jsonText));
        if (Array.isArray(parsed)) items = parsed;
      } catch (err) {
        items = [];
      }
    }
    var out = lvl + ' ' + title + ' (' + items.length + ')\n\n';
    if (items.length === 0) {
      return out + '_None._\n\n';
    }
    for (var i = 0; i < items.length; i++) {
      out += '- ' + (items[i] == null ? '' : String(items[i]).replace(/\r?\n/g, ' ')) + '\n';
    }
    return out + '\n';
  }

  function serializePageMarkdown(page) {
    return '# ' + String(page.PageFileName) + '\n\n' + serializePageSections(page, '##');
  }

  function serializeArtifactMarkdown(artifact, id) {
    var out = '# Artifact ' + String(artifact.Name) + ' (' + String(artifact.FhirId) + ')\n\n';

    var pages = loadArtifactPages(id);
    var ordered = orderArtifactPages(pages, artifact);
    for (var i = 0; i < ordered.length; i++) {
      out += '## ' + ordered[i].label + ': ' + String(ordered[i].page.PageFileName) + '\n\n';
      out += serializePageSections(ordered[i].page, '###');
    }

    var elements = loadArtifactElements(id);
    out += '## Element Review (' + elements.length + ')\n\n';
    if (elements.length > 0) {
      out += mdTable(
        ['Path', 'Is Required', 'Max Cardinality', 'Trial Use', 'Has fixed[x]', 'Has pattern[x]',
          'Required Binding', 'External Required Binding', 'meaningWhenMissing', 'Is Modifier'],
        elements.map(function (e) {
          return [e.Path, boolText(e.IsRequired), e.MaxCardinality || '', boolText(e.IsTrialUse),
            boolText(e.HasFixed), boolText(e.HasPattern), boolText(e.RequiredBinding),
            boolText(e.ExternalRequiredBinding), e.MeaningWhenMissing || '', boolText(e.IsModifier)];
        })) + '\n';
    } else {
      out += '_No elements found._\n\n';
    }

    var operations = loadArtifactOperations(id);
    out += '## Operations (' + operations.length + ')\n\n';
    if (operations.length > 0) {
      out += mdTable(
        ['Id', 'Code', 'Name', 'Kind', 'Status', 'Standards', 'FMM', 'Description'],
        operations.map(function (o) {
          return [o.OperationId, o.Code || '', o.Name || '', o.OperationKind || '', o.Status || '',
            o.StandardsStatus || '', o.FhirMaturity == null ? '' : String(o.FhirMaturity), o.Description || ''];
        })) + '\n';
    } else {
      out += '_No operations found._\n\n';
    }

    var searchParams = loadArtifactSearchParameters(id);
    out += '## Search Parameters (' + searchParams.length + ')\n\n';
    if (searchParams.length > 0) {
      out += mdTable(
        ['Id', 'Name', 'Publication Status', 'FMM', 'Standards Status', 'IsExperimental',
          'WorkGroup', 'Search Type', 'Description'],
        searchParams.map(function (s) {
          return [s.SearchParamId, s.Name || '', s.Status || '',
            s.FhirMaturity == null ? '' : String(s.FhirMaturity), s.StandardsStatus || '',
            boolText(s.IsExperimental), s.WorkGroup || '', s.SearchType || '', s.Description || ''];
        })) + '\n';
    } else {
      out += '_No search parameters found._\n\n';
    }

    return out;
  }

  App.init();
})();
