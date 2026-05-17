// preparer-site SPA — vanilla JS, sql.js in the browser, hash router.
// SECURITY: all ticket content is user-supplied text. Render via
// textContent / createElement only — never innerHTML.

(function () {
  'use strict';

  /** @type {any} */
  let db = null;
  /** @type {Set<string>} */
  const inRunKeys = new Set();
  /** @type {{spec?: string, project?: string, wg?: string}} */
  const ActiveFilters = (typeof window.__FILTERS__ === 'object' && window.__FILTERS__) ? window.__FILTERS__ : {};
  const HasActiveFilters = Object.keys(ActiveFilters).length > 0;

  const App = {
    init: async function () {
      const main = document.getElementById('app');
      try {
        // initSqlJs is global, set by sql-wasm.js.
        // eslint-disable-next-line no-undef
        const SQL = await initSqlJs({ locateFile: function (f) { return 'assets/' + f; } });
        const blob = (typeof window.__DB__ === 'string') ? window.__DB__ : '';
        if (!blob) {
          throw new Error('window.__DB__ missing — emitter did not inline the database.');
        }
        const bin = atob(blob);
        const bytes = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        db = new SQL.Database(bytes);
        const keysRes = query('SELECT Key FROM prepared_tickets', null);
        for (let i = 0; i < keysRes.rows.length; i++) inRunKeys.add(String(keysRes.rows[i].Key));
      } catch (err) {
        renderError(main, 'Failed to load database: ' + err.message);
        return;
      }

      window.addEventListener('hashchange', App.route);
      App.route();
    },

    route: function () {
      const main = document.getElementById('app');
      clearChildren(main);
      const hash = window.location.hash || '#/';
      const parts = hash.replace(/^#\/?/, '').split('/').filter(function (p) { return p.length > 0; });
      try {
        if (parts.length === 0) {
          setBreadcrumb([{ label: 'Home', href: null }]);
          Views.landing(main);
        } else if (parts[0] === 'list') {
          setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'List', href: null }]);
          Views.list(main, null);
        } else if (parts[0] === 'ticket' && parts.length >= 2) {
          const key = decodeURIComponent(parts[1]);
          setBreadcrumb([
            { label: 'Home', href: '#/' },
            { label: 'List', href: '#/list' },
            { label: key, href: null },
          ]);
          Views.ticket(main, key);
        } else if (parts[0] === 'by-workgroup') {
          if (parts.length >= 2) {
            const wg = decodeURIComponent(parts[1]);
            setBreadcrumb([
              { label: 'Home', href: '#/' },
              { label: 'By workgroup', href: '#/by-workgroup' },
              { label: wg, href: null },
            ]);
            Views.list(main, { kind: 'workgroup', value: wg });
          } else {
            setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'By workgroup', href: null }]);
            Views.crosscutIndex(main, 'by-workgroup');
          }
        } else if (parts[0] === 'by-recommendation') {
          if (parts.length >= 2) {
            const rv = decodeURIComponent(parts[1]);
            setBreadcrumb([
              { label: 'Home', href: '#/' },
              { label: 'By recommendation', href: '#/by-recommendation' },
              { label: rv, href: null },
            ]);
            Views.list(main, { kind: 'recommendation', value: rv });
          } else {
            setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'By recommendation', href: null }]);
            Views.crosscutIndex(main, 'by-recommendation');
          }
        } else if (parts[0] === 'by-impact') {
          if (parts.length >= 2) {
            const iv = decodeURIComponent(parts[1]);
            setBreadcrumb([
              { label: 'Home', href: '#/' },
              { label: 'By impact', href: '#/by-impact' },
              { label: iv, href: null },
            ]);
            Views.list(main, { kind: 'impact', value: iv });
          } else {
            setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'By impact', href: null }]);
            Views.crosscutIndex(main, 'by-impact');
          }
        } else if (parts[0] === 'by-specification') {
          if (parts.length >= 2) {
            const sv = decodeURIComponent(parts[1]);
            setBreadcrumb([
              { label: 'Home', href: '#/' },
              { label: 'By specification', href: '#/by-specification' },
              { label: sv, href: null },
            ]);
            Views.list(main, { kind: 'specification', value: sv });
          } else {
            setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'By specification', href: null }]);
            Views.crosscutIndex(main, 'by-specification');
          }
        } else if (parts[0] === 'by-github-state') {
          if (parts.length >= 2) {
            const gv = decodeURIComponent(parts[1]);
            setBreadcrumb([
              { label: 'Home', href: '#/' },
              { label: 'By GitHub item state', href: '#/by-github-state' },
              { label: gv, href: null },
            ]);
            Views.list(main, { kind: 'github-state', value: gv });
          } else {
            setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'By GitHub item state', href: null }]);
            Views.crosscutIndex(main, 'by-github-state');
          }
        } else if (parts[0] === 'by-hydration-status') {
          if (parts.length >= 2) {
            const hv = decodeURIComponent(parts[1]);
            setBreadcrumb([
              { label: 'Home', href: '#/' },
              { label: 'By hydration status', href: '#/by-hydration-status' },
              { label: hv, href: null },
            ]);
            Views.list(main, { kind: 'hydration-status', value: hv });
          } else {
            setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'By hydration status', href: null }]);
            Views.crosscutIndex(main, 'by-hydration-status');
          }
        } else {
          setBreadcrumb([{ label: 'Home', href: '#/' }]);
          Views.notFound(main, hash);
        }
      } catch (err) {
        renderError(main, 'Route render failed: ' + err.message);
      }
      renderFilterFooter(main);
    },
  };

  function renderFilterBanner(main) {
    if (!HasActiveFilters) return;
    const banner = document.createElement('div');
    banner.id = 'filter-banner';
    const keys = ['spec', 'project', 'wg'];
    for (let i = 0; i < keys.length; i++) {
      const k = keys[i];
      if (!ActiveFilters[k]) continue;
      const chip = document.createElement('span');
      chip.className = 'filter-chip';
      chip.textContent = k + ': ' + ActiveFilters[k];
      banner.appendChild(chip);
    }
    if (main.firstChild) {
      main.insertBefore(banner, main.firstChild);
    } else {
      main.appendChild(banner);
    }
  }

  function renderFilterFooter(main) {
    if (!HasActiveFilters) return;
    const parts = [];
    if (ActiveFilters.spec) parts.push('spec=' + ActiveFilters.spec);
    if (ActiveFilters.project) parts.push('project=' + ActiveFilters.project);
    if (ActiveFilters.wg) parts.push('wg=' + ActiveFilters.wg);
    const footer = document.createElement('p');
    footer.className = 'filter-footer';
    footer.textContent = 'Filtered: ' + parts.join(', ');
    main.appendChild(footer);
  }

  function query(sql, params) {
    const stmt = db.prepare(sql);
    if (params) stmt.bind(params);
    const columns = stmt.getColumnNames();
    const rows = [];
    while (stmt.step()) rows.push(stmt.getAsObject());
    stmt.free();
    return { columns: columns, rows: rows };
  }

  function clearChildren(el) {
    while (el.firstChild) el.removeChild(el.firstChild);
  }

  function el(tag, attrs, children) {
    const node = document.createElement(tag);
    if (attrs) {
      for (const k in attrs) {
        if (k === 'class') node.className = attrs[k];
        else if (k === 'href') node.setAttribute('href', attrs[k]);
        else node.setAttribute(k, attrs[k]);
      }
    }
    if (children) {
      if (typeof children === 'string') {
        node.textContent = children;
      } else if (Array.isArray(children)) {
        for (let i = 0; i < children.length; i++) {
          const c = children[i];
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

  function setBreadcrumb(parts) {
    const bc = document.getElementById('breadcrumb');
    if (!bc) return;
    clearChildren(bc);
    for (let i = 0; i < parts.length; i++) {
      if (i > 0) bc.appendChild(document.createTextNode(' › '));
      const p = parts[i];
      if (p.href) bc.appendChild(el('a', { href: p.href }, p.label));
      else bc.appendChild(document.createTextNode(p.label));
    }
  }

  const Crosscuts = {
    'by-workgroup': {
      title: 'By workgroup',
      sql:
        "SELECT COALESCE(NULLIF(jst.WorkGroup, ''), '(unknown)') AS k, count(*) AS n " +
        'FROM prepared_tickets pt ' +
        'LEFT JOIN jira_processing_source_tickets jst ON jst.Key = pt.Key ' +
        'GROUP BY k ORDER BY n DESC, k',
    },
    'by-recommendation': {
      title: 'By recommendation',
      sql:
        "SELECT COALESCE(NULLIF(Recommendation, ''), '(unknown)') AS k, count(*) AS n " +
        'FROM prepared_tickets GROUP BY k ORDER BY n DESC, k',
    },
    'by-impact': {
      title: 'By impact',
      sql:
        'SELECT k, count(*) AS n FROM (' +
        "SELECT COALESCE(NULLIF(ProposalAImpact, ''), '(unknown)') AS k FROM prepared_tickets " +
        'UNION ALL ' +
        "SELECT COALESCE(NULLIF(ProposalBImpact, ''), '(unknown)') AS k FROM prepared_tickets" +
        ') GROUP BY k ORDER BY n DESC, k',
    },
    'by-specification': {
      title: 'By specification',
      sql:
        "SELECT COALESCE(NULLIF(pth.Specification, ''), '(unknown)') AS k, " +
        '       COUNT(DISTINCT pt.Key) AS n ' +
        'FROM prepared_tickets pt ' +
        'LEFT JOIN prepared_ticket_hydration pth ON pth.TicketKey = pt.Key ' +
        'GROUP BY k ORDER BY n DESC, k',
    },
    'by-github-state': {
      title: 'By GitHub item state',
      sql:
        "SELECT COALESCE(NULLIF(pgh.State, ''), '(unknown)') AS k, " +
        '       COUNT(DISTINCT pgh.TicketKey) AS n ' +
        'FROM prepared_github_hydration pgh ' +
        "WHERE pgh.HydrationStatus = 'resolved' " +
        'GROUP BY k ORDER BY n DESC, k',
    },
    'by-hydration-status': {
      title: 'By hydration status',
      sql:
        'SELECT k, COUNT(*) AS n FROM (' +
        '  SELECT pt.Key, ' +
        '         CASE WHEN EXISTS (' +
        "           SELECT 1 FROM prepared_ticket_hydration WHERE TicketKey = pt.Key AND HydrationStatus = 'unresolved' " +
        '           UNION ALL ' +
        "           SELECT 1 FROM prepared_jira_hydration WHERE TicketKey = pt.Key AND HydrationStatus = 'unresolved' " +
        '           UNION ALL ' +
        "           SELECT 1 FROM prepared_zulip_hydration WHERE TicketKey = pt.Key AND HydrationStatus = 'unresolved' " +
        '           UNION ALL ' +
        "           SELECT 1 FROM prepared_github_hydration WHERE TicketKey = pt.Key AND HydrationStatus = 'unresolved' " +
        '           UNION ALL ' +
        "           SELECT 1 FROM prepared_repo_hydration WHERE TicketKey = pt.Key AND HydrationStatus = 'unresolved' " +
        "         ) THEN 'has-unresolved' ELSE 'fully-resolved' END AS k " +
        '  FROM prepared_tickets pt' +
        ') GROUP BY k ORDER BY n DESC, k',
    },
  };

  const Views = {
    landing: function (main) {
      renderFilterBanner(main);
      const totalRes = query('SELECT count(*) AS n FROM prepared_tickets', null);
      const total = totalRes.rows.length ? totalRes.rows[0].n : 0;

      if (total === 0 && HasActiveFilters) {
        main.appendChild(el('p', null, '0 prepared tickets match this filter.'));
      } else {
        main.appendChild(el('p', null, total + ' prepared tickets in this run.'));
      }

      const grid = el('div', { class: 'summary-grid' });
      grid.appendChild(buildSummarySection(Crosscuts['by-workgroup'].title, Crosscuts['by-workgroup'].sql, 'by-workgroup'));
      grid.appendChild(buildSummarySection(Crosscuts['by-recommendation'].title, Crosscuts['by-recommendation'].sql, 'by-recommendation'));
      grid.appendChild(buildSummarySection(Crosscuts['by-impact'].title, Crosscuts['by-impact'].sql, 'by-impact'));
      grid.appendChild(buildSummarySection(Crosscuts['by-specification'].title, Crosscuts['by-specification'].sql, 'by-specification'));
      grid.appendChild(buildSummarySection(Crosscuts['by-github-state'].title, Crosscuts['by-github-state'].sql, 'by-github-state'));
      grid.appendChild(buildSummarySection(Crosscuts['by-hydration-status'].title, Crosscuts['by-hydration-status'].sql, 'by-hydration-status'));
      main.appendChild(grid);

      const nav = el('p', { class: 'muted' });
      nav.appendChild(el('a', { href: '#/list' }, 'Browse all tickets →'));
      main.appendChild(nav);
    },

    crosscutIndex: function (main, route) {
      const cfg = Crosscuts[route];
      if (!cfg) {
        Views.notFound(main, '#/' + route);
        return;
      }
      main.appendChild(el('h2', null, cfg.title));
      const section = buildSummarySection(cfg.title, cfg.sql, route);
      // buildSummarySection already wraps in <section> with its own <h2>; unwrap.
      while (section.firstChild) main.appendChild(section.firstChild);
    },

    list: function (main, filter) {
      const baseSql =
        'SELECT pt.Key, jst.Title, jst.WorkGroup, jst.Status, jst.Type, ' +
        'pt.Recommendation, pt.ProposalAImpact, pt.ProposalBImpact, pt.SavedAt, ' +
        'pt.RequestSummary AS _SearchBody ' +
        'FROM prepared_tickets pt ' +
        'LEFT JOIN jira_processing_source_tickets jst ON jst.Key = pt.Key';
      let where = '';
      let bind = null;
      let heading = 'All prepared tickets';
      if (filter && filter.kind === 'workgroup') {
        if (filter.value === '(unknown)') {
          where = " WHERE COALESCE(NULLIF(jst.WorkGroup, ''), '(unknown)') = '(unknown)'";
        } else {
          where = ' WHERE jst.WorkGroup = $v';
          bind = { $v: filter.value };
        }
        heading = 'Workgroup: ' + filter.value;
      } else if (filter && filter.kind === 'recommendation') {
        if (filter.value === '(unknown)') {
          where = " WHERE COALESCE(NULLIF(pt.Recommendation, ''), '(unknown)') = '(unknown)'";
        } else {
          where = ' WHERE pt.Recommendation = $v';
          bind = { $v: filter.value };
        }
        heading = 'Recommendation: ' + filter.value;
      } else if (filter && filter.kind === 'impact') {
        if (filter.value === '(unknown)') {
          where =
            " WHERE COALESCE(NULLIF(pt.ProposalAImpact, ''), '(unknown)') = '(unknown)' " +
            "    OR COALESCE(NULLIF(pt.ProposalBImpact, ''), '(unknown)') = '(unknown)'";
        } else {
          where = ' WHERE pt.ProposalAImpact = $v OR pt.ProposalBImpact = $v';
          bind = { $v: filter.value };
        }
        heading = 'Impact: ' + filter.value;
      } else if (filter && filter.kind === 'specification') {
        if (filter.value === '(unknown)') {
          where =
            ' WHERE pt.Key IN (' +
            '   SELECT pt2.Key FROM prepared_tickets pt2 ' +
            '   LEFT JOIN prepared_ticket_hydration pth ON pth.TicketKey = pt2.Key ' +
            "   WHERE COALESCE(NULLIF(pth.Specification, ''), '(unknown)') = '(unknown)'" +
            ' )';
        } else {
          where = ' WHERE pt.Key IN (SELECT TicketKey FROM prepared_ticket_hydration WHERE Specification = $v)';
          bind = { $v: filter.value };
        }
        heading = 'Specification: ' + filter.value;
      } else if (filter && filter.kind === 'github-state') {
        if (filter.value === '(unknown)') {
          where =
            ' WHERE pt.Key IN (' +
            '   SELECT pgh.TicketKey FROM prepared_github_hydration pgh ' +
            "   WHERE pgh.HydrationStatus = 'resolved' AND COALESCE(NULLIF(pgh.State, ''), '(unknown)') = '(unknown)'" +
            ' )';
        } else {
          where = ' WHERE pt.Key IN (SELECT TicketKey FROM prepared_github_hydration WHERE State = $v AND HydrationStatus = \'resolved\')';
          bind = { $v: filter.value };
        }
        heading = 'GitHub state: ' + filter.value;
      } else if (filter && filter.kind === 'hydration-status') {
        const hasUnresolvedSql =
          ' EXISTS (SELECT 1 FROM prepared_ticket_hydration WHERE TicketKey = pt.Key AND HydrationStatus = \'unresolved\') ' +
          'OR EXISTS (SELECT 1 FROM prepared_jira_hydration WHERE TicketKey = pt.Key AND HydrationStatus = \'unresolved\') ' +
          'OR EXISTS (SELECT 1 FROM prepared_zulip_hydration WHERE TicketKey = pt.Key AND HydrationStatus = \'unresolved\') ' +
          'OR EXISTS (SELECT 1 FROM prepared_github_hydration WHERE TicketKey = pt.Key AND HydrationStatus = \'unresolved\') ' +
          'OR EXISTS (SELECT 1 FROM prepared_repo_hydration WHERE TicketKey = pt.Key AND HydrationStatus = \'unresolved\')';
        if (filter.value === 'has-unresolved') {
          where = ' WHERE (' + hasUnresolvedSql + ')';
        } else {
          where = ' WHERE NOT (' + hasUnresolvedSql + ')';
        }
        heading = 'Hydration status: ' + filter.value;
      }
      const finalSql = baseSql + where + ' ORDER BY pt.Key';
      const res = query(finalSql, bind);

      main.appendChild(el('h2', null, heading + ' (' + res.rows.length + ')'));

      const filterRow = el('div', { class: 'filter-row' });
      const input = el('input', {
        type: 'text',
        placeholder: 'Filter by key, title, or request summary…',
        autocomplete: 'off',
      });
      filterRow.appendChild(input);
      main.appendChild(filterRow);

      const countWrap = el('p', { class: 'muted' });
      const countSpan = el('span', null, String(res.rows.length));
      countWrap.appendChild(countSpan);
      countWrap.appendChild(document.createTextNode(' rows'));
      main.appendChild(countWrap);

      const table = el('table');
      const thead = el('thead');
      const headRow = el('tr');
      ['Key', 'Title', 'Workgroup', 'Status', 'Type', 'Recommendation', 'Impact', 'Saved'].forEach(function (c) {
        headRow.appendChild(el('th', null, c));
      });
      thead.appendChild(headRow);
      table.appendChild(thead);

      const tbody = el('tbody');
      const renderRows = function (needle) {
        clearChildren(tbody);
        const n = needle.toLowerCase();
        let shown = 0;
        for (let i = 0; i < res.rows.length; i++) {
          const r = res.rows[i];
          if (n.length > 0) {
            const hay = String(r.Key || '') + '\n' + String(r.Title || '') + '\n' + String(r._SearchBody || '');
            if (hay.toLowerCase().indexOf(n) < 0) continue;
          }
          const tr = el('tr');
          const keyCell = el('td');
          keyCell.appendChild(el('a', { href: '#/ticket/' + encodeURIComponent(String(r.Key)) }, String(r.Key)));
          tr.appendChild(keyCell);
          tr.appendChild(el('td', null, String(r.Title || '')));
          tr.appendChild(el('td', null, String(r.WorkGroup || '')));
          tr.appendChild(el('td', null, String(r.Status || '')));
          tr.appendChild(el('td', null, String(r.Type || '')));
          tr.appendChild(el('td', null, String(r.Recommendation || '')));
          tr.appendChild(el('td', null,
            'A: ' + String(r.ProposalAImpact || '') +
            ' · B: ' + String(r.ProposalBImpact || '')));
          tr.appendChild(el('td', null, String(r.SavedAt || '')));
          tbody.appendChild(tr);
          shown++;
        }
        countSpan.textContent = needle.length > 0 ? (shown + ' of ' + res.rows.length) : String(res.rows.length);
      };

      table.appendChild(tbody);
      main.appendChild(table);

      let debounce = 0;
      input.addEventListener('input', function () {
        if (debounce) window.clearTimeout(debounce);
        debounce = window.setTimeout(function () { renderRows(input.value); }, 150);
      });
      renderRows('');
    },

    ticket: function (main, key) {
      const head = query(
        'SELECT pt.*, jst.Title, jst.WorkGroup, jst.Status, jst.Type ' +
        'FROM prepared_tickets pt ' +
        'LEFT JOIN jira_processing_source_tickets jst ON jst.Key = pt.Key ' +
        'WHERE pt.Key = $k',
        { $k: key });
      if (head.rows.length === 0) {
        main.appendChild(el('p', { class: 'error' }, 'No prepared ticket with key ' + key + '.'));
        main.appendChild(el('p', null, el('a', { href: '#/list' }, '← Back to list')));
        return;
      }
      const t = head.rows[0];

      const repos = query('SELECT Repo, RepoCategory, Justification FROM prepared_ticket_repos WHERE TicketKey = $k ORDER BY Repo', { $k: key }).rows;
      const relatedJira = query('SELECT AssociatedTicketKey, LinkType, Justification FROM prepared_ticket_related_jira WHERE TicketKey = $k ORDER BY AssociatedTicketKey', { $k: key }).rows;
      const relatedZulip = query('SELECT ZulipThreadId, Justification FROM prepared_ticket_related_zulip WHERE TicketKey = $k ORDER BY ZulipThreadId', { $k: key }).rows;
      const relatedGitHub = query('SELECT GitHubItemId, Justification FROM prepared_ticket_related_github WHERE TicketKey = $k ORDER BY GitHubItemId', { $k: key }).rows;

      const hydrationParentRes = query('SELECT * FROM prepared_ticket_hydration WHERE TicketKey = $k', { $k: key });
      const hydrationParent = hydrationParentRes.rows.length > 0 ? hydrationParentRes.rows[0] : null;

      const buildMap = function (sql, idCol) {
        const m = Object.create(null);
        const rows = query(sql, { $k: key }).rows;
        for (let i = 0; i < rows.length; i++) m[String(rows[i][idCol])] = rows[i];
        return m;
      };
      const jiraHydration = buildMap('SELECT * FROM prepared_jira_hydration WHERE TicketKey = $k', 'JiraKey');
      const zulipHydration = buildMap('SELECT * FROM prepared_zulip_hydration WHERE TicketKey = $k', 'ZulipThreadId');
      const githubHydration = buildMap('SELECT * FROM prepared_github_hydration WHERE TicketKey = $k', 'GitHubItemId');
      const repoHydration = buildMap('SELECT * FROM prepared_repo_hydration WHERE TicketKey = $k', 'Repo');
      const jiraXref = query('SELECT * FROM prepared_ticket_jira_xref WHERE TicketKey = $k ORDER BY Source, JiraKey', { $k: key }).rows;

      const headerWrap = el('section', { class: 'ticket-header' });
      headerWrap.appendChild(el('h2', null, String(t.Key) + (t.Title ? ' — ' + String(t.Title) : '')));
      const dl = el('dl');
      const kv = function (k, v) {
        dl.appendChild(el('dt', null, k));
        dl.appendChild(el('dd', null, v == null || v === '' ? '—' : String(v)));
      };
      kv('Key', t.Key);
      kv('Title', t.Title);
      kv('Workgroup', t.WorkGroup);
      kv('Status', t.Status);
      kv('Type', t.Type);
      kv('Priority', hydrationParent ? hydrationParent.Priority : null);
      kv('Resolution', hydrationParent ? hydrationParent.Resolution : null);
      kv('Specification', hydrationParent ? hydrationParent.Specification : null);
      kv('Raised in', hydrationParent ? hydrationParent.RaisedInVersion : null);
      kv('Selected ballot', hydrationParent ? hydrationParent.SelectedBallot : null);
      kv('Change category', hydrationParent ? hydrationParent.ChangeCategory : null);
      kv('Impact', hydrationParent ? hydrationParent.Impact : null);
      kv('Comments', hydrationParent ? hydrationParent.CommentCount : null);
      kv('Recommendation', t.Recommendation);
      kv('Saved', t.SavedAt);
      headerWrap.appendChild(dl);
      if (hydrationParent && hydrationParent.HydrationStatus === 'unresolved') {
        headerWrap.appendChild(el('p', { class: 'muted' }, 'Hydration unresolved: ' + String(hydrationParent.HydrationReason || '')));
      }
      const jiraLink = el('p');
      jiraLink.appendChild(el('a', {
        href: 'https://jira.hl7.org/browse/' + encodeURIComponent(String(t.Key)),
        target: '_blank',
        rel: 'noopener noreferrer',
      }, 'Open in Jira ↗'));
      headerWrap.appendChild(jiraLink);
      main.appendChild(headerWrap);

      if (hydrationParent && hydrationParent.DescriptionPlain) {
        const details = el('details');
        details.appendChild(el('summary', null, 'Show Jira description'));
        details.appendChild(el('pre', null, String(hydrationParent.DescriptionPlain)));
        main.appendChild(details);
      }

      const body = el('section', { class: 'ticket-body' });
      const sect = function (title, value) {
        if (value == null || value === '') return;
        body.appendChild(el('h2', null, title));
        body.appendChild(el('pre', null, String(value)));
      };
      const subsect = function (title, value) {
        if (value == null || value === '') return;
        body.appendChild(el('h3', null, title));
        body.appendChild(el('pre', null, String(value)));
      };

      sect('Request Summary', t.RequestSummary);
      sect('Comment Summary', t.CommentSummary);
      sect('Linked Ticket Summary', t.LinkedTicketSummary);
      sect('Related Ticket Summary', t.RelatedTicketSummary);
      sect('Related Zulip Summary', t.RelatedZulipSummary);
      sect('Related GitHub Summary', t.RelatedGitHubSummary);
      sect('Existing Proposed', t.ExistingProposed);

      if (t.ProposalA || t.ProposalAJustification || t.ProposalAImpact) {
        body.appendChild(el('h2', null, 'Proposal A'));
        if (t.ProposalA) body.appendChild(el('pre', null, String(t.ProposalA)));
        subsect('Justification', t.ProposalAJustification);
        subsect('Impact', t.ProposalAImpact);
      }
      if (t.ProposalB || t.ProposalBJustification || t.ProposalBImpact) {
        body.appendChild(el('h2', null, 'Proposal B'));
        if (t.ProposalB) body.appendChild(el('pre', null, String(t.ProposalB)));
        subsect('Justification', t.ProposalBJustification);
        subsect('Impact', t.ProposalBImpact);
      }
      if (t.ProposalC || t.ProposalCJustification) {
        body.appendChild(el('h2', null, 'Proposal C'));
        if (t.ProposalC) body.appendChild(el('pre', null, String(t.ProposalC)));
        subsect('Justification', t.ProposalCJustification);
      }
      if (t.Recommendation || t.RecommendationJustification) {
        body.appendChild(el('h2', null, 'Recommendation'));
        if (t.Recommendation) body.appendChild(el('pre', null, String(t.Recommendation)));
        subsect('Justification', t.RecommendationJustification);
      }

      main.appendChild(body);

      const sidebar = el('section', { class: 'related-sidebar' });
      sidebar.appendChild(el('h2', null, 'Related items'));

      const relatedList = function (label, items, renderItem) {
        if (!items || items.length === 0) return;
        sidebar.appendChild(el('h3', null, label));
        const ul = el('ul');
        for (let i = 0; i < items.length; i++) {
          const li = el('li');
          renderItem(li, items[i]);
          ul.appendChild(li);
        }
        sidebar.appendChild(ul);
      };

      const appendUnresolvedBadge = function (li, hydrationRow) {
        if (hydrationRow && hydrationRow.HydrationStatus === 'unresolved') {
          li.appendChild(document.createTextNode(' '));
          li.appendChild(el('span', { class: 'muted' }, '(unresolved: ' + String(hydrationRow.HydrationReason || '') + ')'));
        }
      };

      relatedList('Repos', repos, function (li, r) {
        const cat = r.RepoCategory ? ' [' + r.RepoCategory + ']' : '';
        li.appendChild(document.createTextNode(String(r.Repo) + cat));
        const h = repoHydration[String(r.Repo)];
        if (h && h.HydrationStatus === 'resolved' && h.Description) {
          li.appendChild(document.createTextNode(' · ' + String(h.Description)));
        }
        appendUnresolvedBadge(li, h);
        if (r.Justification) {
          li.appendChild(document.createElement('br'));
          li.appendChild(el('span', { class: 'muted' }, String(r.Justification)));
        }
      });

      const renderJiraRelatedItem = function (li, r, hydrationRow) {
        const k = String(r.AssociatedTicketKey || r.JiraKey || '');
        if (inRunKeys.has(k)) {
          li.appendChild(el('a', { href: '#/ticket/' + encodeURIComponent(k) }, k));
        } else {
          li.appendChild(el('a', {
            href: 'https://jira.hl7.org/browse/' + encodeURIComponent(k),
            target: '_blank',
            rel: 'noopener noreferrer',
          }, k + ' ↗'));
        }
        if (hydrationRow && hydrationRow.HydrationStatus === 'resolved') {
          const parts = [];
          if (hydrationRow.Title) parts.push(String(hydrationRow.Title));
          if (hydrationRow.Status) parts.push(String(hydrationRow.Status));
          if (hydrationRow.Type) parts.push(String(hydrationRow.Type));
          if (hydrationRow.Resolution) parts.push(String(hydrationRow.Resolution));
          if (parts.length > 0) li.appendChild(document.createTextNode(' · ' + parts.join(' · ')));
        }
        if (r.LinkType) {
          li.appendChild(document.createTextNode(' (' + r.LinkType + ')'));
        }
        appendUnresolvedBadge(li, hydrationRow);
        if (r.Justification) {
          li.appendChild(document.createElement('br'));
          li.appendChild(el('span', { class: 'muted' }, String(r.Justification)));
        }
      };

      relatedList('Related Jira tickets', relatedJira, function (li, r) {
        const h = jiraHydration[String(r.AssociatedTicketKey || '')];
        renderJiraRelatedItem(li, r, h);
      });

      if (jiraXref.length > 0) {
        sidebar.appendChild(el('h3', null, 'Other Jira-declared links'));
        const groups = {};
        for (let i = 0; i < jiraXref.length; i++) {
          const src = String(jiraXref[i].Source || '');
          if (!groups[src]) groups[src] = [];
          groups[src].push(jiraXref[i]);
        }
        const sources = Object.keys(groups).sort();
        for (let i = 0; i < sources.length; i++) {
          sidebar.appendChild(el('h4', null, sources[i]));
          const ul = el('ul');
          for (let j = 0; j < groups[sources[i]].length; j++) {
            const xref = groups[sources[i]][j];
            const li = el('li');
            renderJiraRelatedItem(li, { JiraKey: xref.JiraKey }, jiraHydration[String(xref.JiraKey)]);
            ul.appendChild(li);
          }
          sidebar.appendChild(ul);
        }
      }

      relatedList('Related Zulip threads', relatedZulip, function (li, r) {
        const h = zulipHydration[String(r.ZulipThreadId)];
        if (h && h.HydrationStatus === 'resolved') {
          const stream = h.StreamName || '';
          const topic = h.Topic || '';
          const headline = (stream ? stream + ' › ' : '') + topic;
          li.appendChild(document.createTextNode(headline || String(r.ZulipThreadId)));
          const meta = [];
          if (h.MessageCount != null) meta.push(String(h.MessageCount) + ' messages');
          if (h.LastMessageAt) meta.push('last ' + String(h.LastMessageAt));
          if (meta.length > 0) li.appendChild(document.createTextNode(' · ' + meta.join(' · ')));
          if (h.FirstMessageExcerpt) {
            li.appendChild(document.createElement('br'));
            li.appendChild(el('span', { class: 'muted' }, '“' + String(h.FirstMessageExcerpt) + '”'));
          }
        } else {
          li.appendChild(document.createTextNode(String(r.ZulipThreadId)));
        }
        appendUnresolvedBadge(li, h);
        if (r.Justification) {
          li.appendChild(document.createElement('br'));
          li.appendChild(el('span', { class: 'muted' }, String(r.Justification)));
        }
      });

      relatedList('Related GitHub items', relatedGitHub, function (li, r) {
        const id = String(r.GitHubItemId);
        const h = githubHydration[id];
        if (h && h.HydrationStatus === 'resolved') {
          const kind = h.IsPullRequest ? '(PR)' : '(Issue)';
          if (h.Path) {
            li.appendChild(document.createTextNode((h.Repo ? String(h.Repo) + ': ' : '') + String(h.Path) +
              (h.Title ? ' · ' + String(h.Title) : '')));
          } else {
            const headline = (h.Repo ? String(h.Repo) : '') + (h.Number != null ? '#' + h.Number : '');
            li.appendChild(document.createTextNode((headline || id) +
              (h.Title ? ' · ' + String(h.Title) : '') +
              (h.State ? ' · ' + String(h.State) : '') + ' ' + kind));
          }
        } else {
          li.appendChild(document.createTextNode(id));
        }
        appendUnresolvedBadge(li, h);
        if (r.Justification) {
          li.appendChild(document.createElement('br'));
          li.appendChild(el('span', { class: 'muted' }, String(r.Justification)));
        }
      });

      main.appendChild(sidebar);

      const back = el('p', { class: 'muted' });
      back.appendChild(el('a', { href: '#/list' }, '← Back to list'));
      main.appendChild(back);
    },

    notFound: function (main, hash) {
      main.appendChild(el('p', { class: 'error' }, 'No view for route ' + hash + '.'));
      main.appendChild(el('p', null, el('a', { href: '#/' }, '← Home')));
    },
  };

  function buildSummarySection(title, sql, routePrefix) {
    const section = el('section');
    section.appendChild(el('h2', null, title));
    const res = query(sql, null);
    if (res.rows.length === 0) {
      section.appendChild(el('p', { class: 'muted' }, 'No data.'));
      return section;
    }
    const table = el('table');
    const thead = el('thead');
    const headRow = el('tr');
    headRow.appendChild(el('th', null, title.replace(/^By /, '')));
    headRow.appendChild(el('th', null, 'Count'));
    thead.appendChild(headRow);
    table.appendChild(thead);
    const tbody = el('tbody');
    for (let i = 0; i < res.rows.length; i++) {
      const r = res.rows[i];
      const tr = el('tr');
      const keyCell = el('td');
      const keyText = String(r.k != null ? r.k : '');
      keyCell.appendChild(el('a', { href: '#/' + routePrefix + '/' + encodeURIComponent(keyText) }, keyText));
      tr.appendChild(keyCell);
      tr.appendChild(el('td', null, String(r.n)));
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    section.appendChild(table);
    return section;
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', App.init);
  } else {
    App.init();
  }
})();
