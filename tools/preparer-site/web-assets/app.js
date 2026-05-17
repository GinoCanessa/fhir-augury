// preparer-site SPA — vanilla JS, sql.js in the browser, hash router.
// SECURITY: all ticket content is user-supplied text. Render via
// textContent / createElement only — never innerHTML.

(function () {
  'use strict';

  /** @type {any} */
  let db = null;

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
          Views.landing(main);
        } else if (parts[0] === 'list') {
          Views.stub(main, 'List view (Phase 4)');
        } else if (parts[0] === 'ticket' && parts.length >= 2) {
          Views.stub(main, 'Per-ticket view (Phase 4): ' + decodeURIComponent(parts[1]));
        } else if (parts[0] === 'by-workgroup') {
          Views.stub(main, parts.length >= 2
            ? 'By-workgroup detail (Phase 5): ' + decodeURIComponent(parts[1])
            : 'By-workgroup index (Phase 5)');
        } else if (parts[0] === 'by-recommendation') {
          Views.stub(main, parts.length >= 2
            ? 'By-recommendation detail (Phase 5): ' + decodeURIComponent(parts[1])
            : 'By-recommendation index (Phase 5)');
        } else if (parts[0] === 'by-impact') {
          Views.stub(main, parts.length >= 2
            ? 'By-impact detail (Phase 5): ' + decodeURIComponent(parts[1])
            : 'By-impact index (Phase 5)');
        } else {
          Views.notFound(main, hash);
        }
      } catch (err) {
        renderError(main, 'Route render failed: ' + err.message);
      }
    },
  };

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
      } else {
        node.appendChild(children);
      }
    }
    return node;
  }

  function renderError(main, msg) {
    clearChildren(main);
    main.appendChild(el('p', { class: 'error' }, msg));
  }

  const Views = {
    landing: function (main) {
      const totalRes = query('SELECT count(*) AS n FROM prepared_tickets', null);
      const total = totalRes.rows.length ? totalRes.rows[0].n : 0;

      main.appendChild(el('p', null, total + ' prepared tickets in this run.'));

      const grid = el('div', { class: 'summary-grid' });

      grid.appendChild(buildSummarySection(
        'By workgroup',
        "SELECT COALESCE(NULLIF(jst.WorkGroup, ''), '(unknown)') AS k, count(*) AS n " +
          'FROM prepared_tickets pt ' +
          'LEFT JOIN jira_processing_source_tickets jst ON jst.Key = pt.Key ' +
          'GROUP BY k ORDER BY n DESC, k',
        'by-workgroup'));

      grid.appendChild(buildSummarySection(
        'By recommendation',
        "SELECT COALESCE(NULLIF(Recommendation, ''), '(unknown)') AS k, count(*) AS n " +
          'FROM prepared_tickets GROUP BY k ORDER BY n DESC, k',
        'by-recommendation'));

      grid.appendChild(buildSummarySection(
        'By impact',
        "SELECT k, count(*) AS n FROM (" +
          "SELECT COALESCE(NULLIF(ProposalAImpact, ''), '(unknown)') AS k FROM prepared_tickets " +
          'UNION ALL ' +
          "SELECT COALESCE(NULLIF(ProposalBImpact, ''), '(unknown)') AS k FROM prepared_tickets" +
          ') GROUP BY k ORDER BY n DESC, k',
        'by-impact'));

      main.appendChild(grid);

      const nav = el('p', { class: 'muted' }, null);
      nav.appendChild(el('a', { href: '#/list' }, 'Browse all tickets →'));
      main.appendChild(nav);
    },

    stub: function (main, label) {
      main.appendChild(el('p', null, label + ' — coming soon.'));
      main.appendChild(el('p', null, el('a', { href: '#/' }, '← Home')));
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
