// preparer-site SPA — vanilla JS, sql.js in the browser, hash router.
// SECURITY: all ticket content is user-supplied text. Render via
// textContent / createElement only — never innerHTML.

(function () {
  'use strict';

  /** @type {any} */
  let db = null;
  /** @type {Set<string>} */
  const inRunKeys = new Set();

  // Chip dimensions that can appear as filter chips. `spec`, `project`,
  // and `wg` are the three the generation pipeline can pre-pin (baked
  // into the trimmed DB); `artifact` and `page` are in-page-only and
  // surface from clicking crosscut rows (Phase 4).
  const FilterableDimensions = ['spec', 'project', 'wg', 'type', 'artifact', 'page', 'impact'];
  const GenerationDimensions = ['spec', 'project', 'wg'];

  // Each chip value is stored as a list (today: length one). The UX is
  // single-value per dimension but the underlying state is forward-proofed
  // for a future "let me OR two artifacts together" enhancement.
  /** @type {{[k: string]: string[]}} */
  const GenerationChips = {};
  (function seedGenerationChips() {
    const raw = (typeof window.__FILTERS__ === 'object' && window.__FILTERS__) ? window.__FILTERS__ : {};
    for (let i = 0; i < GenerationDimensions.length; i++) {
      const dim = GenerationDimensions[i];
      if (typeof raw[dim] === 'string' && raw[dim].length > 0) {
        GenerationChips[dim] = [raw[dim]];
      }
    }
  })();
  /** @type {{[k: string]: string[]}} */
  let ActiveChips = {};

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
      const fullHash = window.location.hash || '#/';

      // Split path and chip-query suffix. Encoded as a `?` after the
      // route path inside the hash, e.g. `#/list?wg=PA&artifact=Observation`.
      // GenerationChips always win — if both URL and generation pin a
      // dimension, the GenerationChips value is preserved.
      const stripped = fullHash.replace(/^#\/?/, '');
      const queryIdx = stripped.indexOf('?');
      const pathPart = queryIdx >= 0 ? stripped.slice(0, queryIdx) : stripped;
      const queryPart = queryIdx >= 0 ? stripped.slice(queryIdx + 1) : '';
      ActiveChips = parseChipsFromQuery(queryPart);

      const parts = pathPart.split('/').filter(function (p) { return p.length > 0; });
      try {
        if (parts.length === 0) {
          setBreadcrumb([{ label: 'Home', href: null }]);
          Views.landing(main);
        } else if (parts[0] === 'list') {
          setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'List', href: null }]);
          Views.list(main, null);
        } else if (parts[0] === 'topics') {
          setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'Topics', href: null }]);
          Views.topics(main);
        } else if (parts[0] === 'topic' && parts.length >= 2) {
          const topicId = decodeURIComponent(parts[1]);
          setBreadcrumb([
            { label: 'Home', href: '#/' },
            { label: 'Topics', href: '#/topics' + currentHashSuffix() },
            { label: topicId, href: null },
          ]);
          Views.topic(main, topicId);
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
            // Backwards-compat: deep-links like #/by-workgroup/Foo are
            // redirected into the equivalent chip-applied list view.
            const wg = decodeURIComponent(parts[1]);
            redirectToChipListView('wg', wg);
            return;
          }
          setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'By workgroup', href: null }]);
          Views.crosscutIndex(main, 'by-workgroup');
        } else if (parts[0] === 'by-type') {
          if (parts.length >= 2) {
            const tv = decodeURIComponent(parts[1]);
            redirectToChipListView('type', tv);
            return;
          }
          setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'By type', href: null }]);
          Views.crosscutIndex(main, 'by-type');
        } else if (parts[0] === 'by-artifact') {
          if (parts.length >= 2) {
            const av = decodeURIComponent(parts[1]);
            redirectToChipListView('artifact', av);
            return;
          }
          setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'By artifact', href: null }]);
          Views.crosscutIndex(main, 'by-artifact');
        } else if (parts[0] === 'by-page') {
          if (parts.length >= 2) {
            const pv = decodeURIComponent(parts[1]);
            redirectToChipListView('page', pv);
            return;
          }
          setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'By page', href: null }]);
          Views.crosscutIndex(main, 'by-page');
        } else if (parts[0] === 'by-impact') {
          if (parts.length >= 2) {
            const iv = decodeURIComponent(parts[1]);
            redirectToChipListView('impact', iv);
            return;
          }
          setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'By impact', href: null }]);
          Views.crosscutIndex(main, 'by-impact');
        } else if (parts[0] === 'by-specification') {
          if (parts.length >= 2) {
            const sv = decodeURIComponent(parts[1]);
            redirectToChipListView('spec', sv);
            return;
          }
          setBreadcrumb([{ label: 'Home', href: '#/' }, { label: 'By specification', href: null }]);
          Views.crosscutIndex(main, 'by-specification');
        } else {
          setBreadcrumb([{ label: 'Home', href: '#/' }]);
          Views.notFound(main, fullHash);
        }
      } catch (err) {
        renderError(main, 'Route render failed: ' + err.message);
      }
    },
  };

  // ------- Chip-state helpers -------

  function parseChipsFromQuery(queryPart) {
    /** @type {{[k: string]: string[]}} */
    const result = {};
    // Seed from GenerationChips first so they always survive.
    for (const k in GenerationChips) {
      if (GenerationChips[k] && GenerationChips[k].length > 0) {
        result[k] = GenerationChips[k].slice();
      }
    }
    if (!queryPart) return result;
    // URLSearchParams handles `+`, `%xx`, repeated keys, etc. Each
    // dimension may appear multiple times (e.g., `impact=A&impact=B`);
    // values are never comma-split because impact values legitimately
    // contain commas (e.g., "Compatible, substantive").
    const params = new URLSearchParams(queryPart);
    for (let i = 0; i < FilterableDimensions.length; i++) {
      const dim = FilterableDimensions[i];
      const values = params.getAll(dim).filter(function (v) { return v.length > 0; });
      if (values.length === 0) continue;
      // Merge with GenerationChips (already in `result[dim]`); if a value
      // is already present (case-insensitive) keep it; otherwise append.
      const existing = result[dim] ? result[dim].slice() : [];
      const seen = {};
      for (let j = 0; j < existing.length; j++) seen[existing[j].toLowerCase()] = true;
      for (let j = 0; j < values.length; j++) {
        if (!seen[values[j].toLowerCase()]) {
          existing.push(values[j]);
          seen[values[j].toLowerCase()] = true;
        }
      }
      result[dim] = existing;
    }
    return result;
  }

  function getInPageChips() {
    // ActiveChips minus the generation pins (those can't be removed from
    // the URL). Returned shape matches ActiveChips: { dim: string[] }.
    /** @type {{[k: string]: string[]}} */
    const out = {};
    for (const dim in ActiveChips) {
      const all = ActiveChips[dim] || [];
      const gen = GenerationChips[dim] || [];
      const genSet = {};
      for (let i = 0; i < gen.length; i++) genSet[gen[i].toLowerCase()] = true;
      const remaining = [];
      for (let i = 0; i < all.length; i++) {
        if (!genSet[all[i].toLowerCase()]) remaining.push(all[i]);
      }
      if (remaining.length > 0) out[dim] = remaining;
    }
    return out;
  }

  function buildChipQuerySuffix(chips) {
    const params = new URLSearchParams();
    for (let i = 0; i < FilterableDimensions.length; i++) {
      const dim = FilterableDimensions[i];
      const values = chips[dim];
      if (!values || values.length === 0) continue;
      // Repeated-key encoding (one `dim=value` per value) so values
      // containing `,` (e.g., "Compatible, substantive") survive a
      // round-trip without being split.
      for (let j = 0; j < values.length; j++) {
        params.append(dim, values[j]);
      }
    }
    const s = params.toString();
    return s.length > 0 ? '?' + s : '';
  }

  function currentHashSuffix() {
    return buildChipQuerySuffix(getInPageChips());
  }

  function setHashChips(inPageChips) {
    const fullHash = window.location.hash || '#/';
    const stripped = fullHash.replace(/^#\/?/, '');
    const queryIdx = stripped.indexOf('?');
    const pathPart = queryIdx >= 0 ? stripped.slice(0, queryIdx) : stripped;
    const suffix = buildChipQuerySuffix(inPageChips);
    window.location.hash = '#/' + pathPart + suffix;
  }

  function toggleChip(dim, value) {
    if (FilterableDimensions.indexOf(dim) < 0) return;
    const current = getInPageChips();
    const existing = current[dim] ? current[dim].slice() : [];
    const lc = value.toLowerCase();
    let removed = false;
    for (let i = existing.length - 1; i >= 0; i--) {
      if (existing[i].toLowerCase() === lc) {
        existing.splice(i, 1);
        removed = true;
      }
    }
    if (removed) {
      if (existing.length === 0) delete current[dim];
      else current[dim] = existing;
    } else {
      // Single-value UX: replace any other in-page values in this dim.
      current[dim] = [value];
    }
    setHashChips(current);
  }

  function removeChipValue(dim, value) {
    const current = getInPageChips();
    const existing = current[dim] ? current[dim].slice() : [];
    const lc = value.toLowerCase();
    const filtered = existing.filter(function (v) { return v.toLowerCase() !== lc; });
    if (filtered.length === 0) delete current[dim];
    else current[dim] = filtered;
    setHashChips(current);
  }

  function isGenerationChip(dim, value) {
    const gen = GenerationChips[dim] || [];
    const lc = value.toLowerCase();
    for (let i = 0; i < gen.length; i++) {
      if (gen[i].toLowerCase() === lc) return true;
    }
    return false;
  }

  function hasAnyActiveChips() {
    for (const dim in ActiveChips) {
      if (ActiveChips[dim] && ActiveChips[dim].length > 0) return true;
    }
    return false;
  }

  function renderChipBanner(main) {
    if (!hasAnyActiveChips()) return;
    const banner = document.createElement('div');
    banner.id = 'filter-banner';
    for (let i = 0; i < FilterableDimensions.length; i++) {
      const dim = FilterableDimensions[i];
      const values = ActiveChips[dim] || [];
      for (let j = 0; j < values.length; j++) {
        const value = values[j];
        const chip = document.createElement('span');
        chip.className = 'filter-chip';
        chip.appendChild(document.createTextNode(dim + ': ' + value));
        if (!isGenerationChip(dim, value)) {
          // Wrap dim+value in a closure-stable pair to avoid loop var bugs
          // in the click handler.
          const removeBtn = document.createElement('button');
          removeBtn.type = 'button';
          removeBtn.className = 'chip-remove';
          removeBtn.setAttribute('aria-label', 'Remove ' + dim + ' filter ' + value);
          removeBtn.appendChild(document.createTextNode('×'));
          (function (capturedDim, capturedValue) {
            removeBtn.addEventListener('click', function () {
              removeChipValue(capturedDim, capturedValue);
            });
          })(dim, value);
          chip.appendChild(removeBtn);
        }
        banner.appendChild(chip);
      }
    }
    if (main.firstChild) {
      main.insertBefore(banner, main.firstChild);
    } else {
      main.appendChild(banner);
    }
  }

  // Expose toggleChip for crosscut-row click handlers added in Phase 4.
  window.__preparerToggleChip = toggleChip;
  window.__preparerCurrentHashSuffix = currentHashSuffix;

  function redirectToChipListView(dim, value) {
    // Deep-link compatibility for `#/by-<dim>/<value>`: convert into the
    // canonical chip-applied `#/list?dim=value` form. Trigger a hashchange
    // by assigning a new hash; the router re-runs and picks up the chip.
    if (FilterableDimensions.indexOf(dim) < 0) return;
    const params = new URLSearchParams();
    params.set(dim, value);
    window.location.hash = '#/list?' + params.toString();
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
      dim: 'wg',
      sql: function (chipKeysSql) {
        return (
          "SELECT COALESCE(NULLIF(jst.WorkGroup, ''), '(unknown)') AS k, count(*) AS n " +
          'FROM prepared_tickets pt ' +
          'LEFT JOIN jira_processing_source_tickets jst ON jst.Key = pt.Key ' +
          'WHERE pt.Key IN (' + chipKeysSql + ') ' +
          'GROUP BY k ORDER BY n DESC, k'
        );
      },
    },
    'by-type': {
      title: 'By type',
      dim: 'type',
      sql: function (chipKeysSql) {
        return (
          "SELECT COALESCE(NULLIF(jst.Type, ''), '(unknown)') AS k, count(*) AS n " +
          'FROM prepared_tickets pt ' +
          'LEFT JOIN jira_processing_source_tickets jst ON jst.Key = pt.Key ' +
          'WHERE pt.Key IN (' + chipKeysSql + ') ' +
          'GROUP BY k ORDER BY n DESC, k'
        );
      },
    },
    'by-artifact': {
      title: 'By artifact',
      dim: 'artifact',
      sql: function (chipKeysSql) {
        return (
          'SELECT Value AS k, COUNT(DISTINCT TicketKey) AS n ' +
          'FROM prepared_ticket_artifacts ' +
          'WHERE TicketKey IN (' + chipKeysSql + ') ' +
          'GROUP BY k ORDER BY n DESC, k'
        );
      },
    },
    'by-page': {
      title: 'By page',
      dim: 'page',
      sql: function (chipKeysSql) {
        return (
          'SELECT Value AS k, COUNT(DISTINCT TicketKey) AS n ' +
          'FROM prepared_ticket_pages ' +
          'WHERE TicketKey IN (' + chipKeysSql + ') ' +
          'GROUP BY k ORDER BY n DESC, k'
        );
      },
    },
    'by-impact': {
      title: 'By impact',
      dim: 'impact',
      sql: function (chipKeysSql) {
        return (
          'SELECT k, count(*) AS n FROM (' +
          "SELECT COALESCE(NULLIF(ProposalAImpact, ''), '(unknown)') AS k FROM prepared_tickets " +
          'WHERE Key IN (' + chipKeysSql + ') ' +
          'UNION ALL ' +
          "SELECT COALESCE(NULLIF(ProposalBImpact, ''), '(unknown)') AS k FROM prepared_tickets " +
          'WHERE Key IN (' + chipKeysSql + ')' +
          ') GROUP BY k ORDER BY n DESC, k'
        );
      },
    },
    'by-specification': {
      title: 'By specification',
      dim: 'spec',
      sql: function (chipKeysSql) {
        return (
          "SELECT COALESCE(NULLIF(pth.Specification, ''), '(unknown)') AS k, " +
          '       COUNT(DISTINCT pt.Key) AS n ' +
          'FROM prepared_tickets pt ' +
          'LEFT JOIN prepared_ticket_hydration pth ON pth.TicketKey = pt.Key ' +
          'WHERE pt.Key IN (' + chipKeysSql + ') ' +
          'GROUP BY k ORDER BY n DESC, k'
        );
      },
    },
  };

  // Order of columns on the landing page. by-recommendation is gone;
  // by-artifact and by-page are new.
  const LandingCrosscutOrder = [
    'by-workgroup',
    'by-type',
    'by-artifact',
    'by-page',
    'by-impact',
    'by-specification',
  ];

  // ------- Chip-composed WHERE for the ticket list and crosscuts -------

  // Maps a chip dimension to the predicate it injects against pt.Key,
  // plus the params object to bind. Used by both Views.list and the
  // crosscut SQL builders.
  function chipPredicateAndParams(dim, values, paramPrefix) {
    if (!values || values.length === 0) return null;
    const placeholders = [];
    /** @type {{[k: string]: string}} */
    const params = {};
    for (let i = 0; i < values.length; i++) {
      const pname = '$' + paramPrefix + dim + i;
      placeholders.push(pname);
      params[pname] = values[i];
    }
    const inList = placeholders.join(', ');
    switch (dim) {
      case 'spec':
        return {
          predicate: 'pt.Key IN (SELECT TicketKey FROM prepared_ticket_hydration WHERE Specification IN (' + inList + '))',
          params: params,
        };
      case 'wg':
        return {
          predicate: 'pt.Key IN (SELECT Key FROM jira_processing_source_tickets WHERE WorkGroup IN (' + inList + '))',
          params: params,
        };
      case 'type':
        return {
          predicate: 'pt.Key IN (SELECT Key FROM jira_processing_source_tickets WHERE Type IN (' + inList + '))',
          params: params,
        };
      case 'project':
        return {
          predicate: 'pt.Key IN (SELECT Key FROM jira_processing_source_tickets WHERE Project IN (' + inList + '))',
          params: params,
        };
      case 'artifact':
        return {
          predicate: 'pt.Key IN (SELECT TicketKey FROM prepared_ticket_artifacts WHERE Value IN (' + inList + '))',
          params: params,
        };
      case 'page':
        return {
          predicate: 'pt.Key IN (SELECT TicketKey FROM prepared_ticket_pages WHERE Value IN (' + inList + '))',
          params: params,
        };
      case 'impact':
        return {
          predicate:
            'pt.Key IN (SELECT Key FROM prepared_tickets WHERE ' +
            "COALESCE(NULLIF(ProposalAImpact, ''), '(unknown)') IN (" + inList + ') ' +
            'OR ' +
            "COALESCE(NULLIF(ProposalBImpact, ''), '(unknown)') IN (" + inList + '))',
          params: params,
        };
      default:
        return null;
    }
  }

  // Returns { sql, params } where `sql` is a subquery yielding the set of
  // ticket keys that survive the active chip filter (excluding the chip
  // dimensions named in `excludeDims`). Used to scope crosscut counts to
  // the post-chip data set without zeroing out the column being filtered.
  function buildChipKeysSubquery(excludeDims) {
    const excludeSet = {};
    if (excludeDims) {
      for (let i = 0; i < excludeDims.length; i++) excludeSet[excludeDims[i]] = true;
    }
    const predicates = [];
    /** @type {{[k: string]: string}} */
    const params = {};
    let idx = 0;
    for (let i = 0; i < FilterableDimensions.length; i++) {
      const dim = FilterableDimensions[i];
      if (excludeSet[dim]) continue;
      const values = ActiveChips[dim];
      const part = chipPredicateAndParams(dim, values, 'ckq' + idx + '_');
      if (!part) continue;
      predicates.push(part.predicate);
      for (const p in part.params) params[p] = part.params[p];
      idx++;
    }
    if (predicates.length === 0) {
      return { sql: 'SELECT Key FROM prepared_tickets', params: {} };
    }
    return {
      sql: 'SELECT pt.Key FROM prepared_tickets pt WHERE ' + predicates.join(' AND '),
      params: params,
    };
  }

  // Returns { where, params } scoping Views.list's main SELECT by every
  // active chip dimension. Empty where when no chips are active.
  function buildListChipWhere() {
    const predicates = [];
    /** @type {{[k: string]: string}} */
    const params = {};
    let idx = 0;
    for (let i = 0; i < FilterableDimensions.length; i++) {
      const dim = FilterableDimensions[i];
      const values = ActiveChips[dim];
      const part = chipPredicateAndParams(dim, values, 'cw' + idx + '_');
      if (!part) continue;
      predicates.push(part.predicate);
      for (const p in part.params) params[p] = part.params[p];
      idx++;
    }
    if (predicates.length === 0) {
      return { where: '', params: null };
    }
    return { where: ' WHERE ' + predicates.join(' AND '), params: params };
  }

  // Returns { where, params } scoping the topic-list SELECT (alias `t`)
  // by every active chip dimension. Direct-column dimensions
  // (`wg`, `spec`, `type`) translate into case-insensitive IN-lists on
  // columns of prepared_ticket_topics; the remaining dimensions
  // (`artifact`, `page`, `impact`, `project`) translate into a
  // member-ticket subquery built from `buildChipKeysSubquery` with the
  // direct-column dims excluded (so they aren't double-applied).
  function buildTopicChipWhere() {
    const predicates = ['1=1'];
    /** @type {{[k: string]: string}} */
    const params = {};
    let idx = 0;

    function pushInList(column, values) {
      const placeholders = [];
      for (let i = 0; i < values.length; i++) {
        const pname = '$tw' + idx + '_' + i;
        placeholders.push(pname);
        params[pname] = String(values[i]).toLowerCase();
      }
      predicates.push('LOWER(' + column + ') IN (' + placeholders.join(', ') + ')');
      idx++;
    }

    const wgValues = ActiveChips['wg'];
    if (wgValues && wgValues.length > 0) pushInList('t.WorkGroupClean', wgValues);
    const specValues = ActiveChips['spec'];
    if (specValues && specValues.length > 0) pushInList('t.Specification', specValues);
    const typeValues = ActiveChips['type'];
    if (typeValues && typeValues.length > 0) pushInList('t.Type', typeValues);

    // Member-ticket dimensions: build the surviving-key subquery without
    // the direct-column dims (they're already applied above).
    const memberDims = ['artifact', 'page', 'impact', 'project'];
    let anyMember = false;
    for (let i = 0; i < memberDims.length; i++) {
      const dim = memberDims[i];
      const values = ActiveChips[dim];
      if (values && values.length > 0) { anyMember = true; break; }
    }
    if (anyMember) {
      const sub = buildChipKeysSubquery(['wg', 'spec', 'type']);
      // Params are namespaced ($ckq…) and disjoint from the topic
      // chip-where's $tw… params, so merge them directly.
      for (const k in sub.params) params[k] = sub.params[k];
      predicates.push(
        't.RowId IN (SELECT m.TopicRowId FROM prepared_ticket_topic_members m ' +
        'WHERE m.TicketKey IN (' + sub.sql + '))'
      );
      idx++;
    }

    return {
      where: ' WHERE ' + predicates.join(' AND '),
      params: Object.keys(params).length > 0 ? params : null,
    };
  }

  const Views = {
    landing: function (main) {
      renderChipBanner(main);

      // Post-chip surviving ticket count.
      const chipKeys = buildChipKeysSubquery([]);
      const totalRes = query('SELECT count(*) AS n FROM (' + chipKeys.sql + ')', chipKeys.params);
      const total = totalRes.rows.length ? totalRes.rows[0].n : 0;

      const summaryRow = el('p', { class: 'summary-row' });
      const countSpan = el('span', null,
        (total === 0 && hasAnyActiveChips())
          ? '0 prepared tickets match this filter.'
          : (total + ' prepared tickets in this run.'));
      summaryRow.appendChild(countSpan);

      const showListLink = el('a', { href: '#/list' + currentHashSuffix(), class: 'show-ticket-list' },
        'Show Ticket List →');
      summaryRow.appendChild(showListLink);

      // Topic surface affordance: render `Show Topic List →` next to
      // the ticket-list link. The trimmer guarantees no orphan topics
      // ship in the inlined DB, so the probe just counts surviving
      // topic rows. Defensive try/catch handles older inlined DBs
      // emitted before the topic tables existed.
      let topicsTotal = 0;
      try {
        const topicsRes = query('SELECT count(*) AS n FROM prepared_ticket_topics', null);
        topicsTotal = (topicsRes.rows.length > 0 && topicsRes.rows[0].n != null)
          ? Number(topicsRes.rows[0].n) : 0;
      } catch (e) {
        topicsTotal = 0;
      }
      if (topicsTotal > 0) {
        const showTopicLink = el('a', {
          href: '#/topics' + currentHashSuffix(),
          class: 'show-topic-list',
        }, 'Show Topic List →');
        summaryRow.appendChild(showTopicLink);
      } else {
        const disabled = el('span', {
          class: 'show-topic-list show-topic-list-disabled',
          title: 'No topics in this run.',
        }, 'Show Topic List →');
        summaryRow.appendChild(disabled);
      }
      main.appendChild(summaryRow);

      const grid = el('div', { class: 'summary-grid' });
      for (let i = 0; i < LandingCrosscutOrder.length; i++) {
        const route = LandingCrosscutOrder[i];
        const cfg = Crosscuts[route];
        if (!cfg) continue;
        const section = buildSummarySectionFor(route, true);
        if (section) grid.appendChild(section);
      }
      main.appendChild(grid);
    },

    crosscutIndex: function (main, route) {
      const cfg = Crosscuts[route];
      if (!cfg) {
        Views.notFound(main, '#/' + route);
        return;
      }
      renderChipBanner(main);
      main.appendChild(el('h2', null, cfg.title));
      const section = buildSummarySectionFor(route, false);
      if (section) {
        // Unwrap the inner section into main; strip the duplicate <h2>
        // emitted by buildSummarySectionFor.
        while (section.firstChild) {
          const child = section.firstChild;
          section.removeChild(child);
          if (child.tagName && child.tagName.toLowerCase() === 'h2') continue;
          main.appendChild(child);
        }
      }
    },

    list: function (main, filter) {
      renderChipBanner(main);
      const baseSql =
        'SELECT pt.Key, jst.Title, jst.WorkGroup, jst.Status, jst.Type, ' +
        'pt.ProposalAImpact, pt.ProposalBImpact, ' +
        'pt.RequestSummary AS _SearchBody ' +
        'FROM prepared_tickets pt ' +
        'LEFT JOIN jira_processing_source_tickets jst ON jst.Key = pt.Key';

      // Chip-composed WHERE: every active chip dimension participates.
      const chipBind = buildListChipWhere();
      const wherePredicates = [];
      /** @type {{[k: string]: any}} */
      const bind = {};
      if (chipBind.where) {
        wherePredicates.push('(' + chipBind.where.replace(/^ WHERE /, '') + ')');
        for (const k in chipBind.params) bind[k] = chipBind.params[k];
      }

      let heading;
      if (hasAnyActiveChips()) {
        heading = 'Filtered ticket list';
      } else {
        heading = 'All prepared tickets';
      }

      const where = wherePredicates.length > 0
        ? ' WHERE ' + wherePredicates.join(' AND ')
        : '';
      const finalSql = baseSql + where + ' ORDER BY pt.Key';
      const res = query(finalSql, Object.keys(bind).length > 0 ? bind : null);

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

      const columns = [
        { label: 'Key',       get: function (r) { return String(r.Key || ''); },             cmp: 'key' },
        { label: 'Title',     get: function (r) { return String(r.Title || ''); },           cmp: 'ci' },
        { label: 'Workgroup', get: function (r) { return String(r.WorkGroup || ''); },       cmp: 'ci' },
        { label: 'Status',    get: function (r) { return String(r.Status || ''); },          cmp: 'ci' },
        { label: 'Type',      get: function (r) { return String(r.Type || ''); },            cmp: 'ci' },
        { label: 'Impact A',  get: function (r) { return String(r.ProposalAImpact || ''); }, cmp: 'ci' },
        { label: 'Impact B',  get: function (r) { return String(r.ProposalBImpact || ''); }, cmp: 'ci' },
      ];

      // Per-mount sort state — resets every time the list view re-renders
      // because `let` locals live in the closure created by this call.
      let sortCol = 'Key';
      let sortDir = 'asc';

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
          if (ev.key === 'Enter' || ev.key === ' ') {
            ev.preventDefault();
            onActivate();
          }
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
          return function (a, b) {
            return a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });
          };
        }
        return function (a, b) {
          return a.localeCompare(b, undefined, { sensitivity: 'base' });
        };
      }

      const tbody = el('tbody');
      const renderRows = function (needle) {
        clearChildren(tbody);
        const n = needle.toLowerCase();
        const filtered = [];
        for (let i = 0; i < res.rows.length; i++) {
          const r = res.rows[i];
          if (n.length > 0) {
            const hay = String(r.Key || '') + '\n' + String(r.Title || '') + '\n' + String(r._SearchBody || '');
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
          const get = activeCol.get;
          filtered.sort(function (a, b) { return cmp(get(a), get(b)) * dirMul; });
        }

        for (let i = 0; i < filtered.length; i++) {
          const r = filtered[i];
          const tr = el('tr');
          const keyCell = el('td');
          keyCell.appendChild(el('a', { href: '#/ticket/' + encodeURIComponent(String(r.Key)) }, String(r.Key)));
          tr.appendChild(keyCell);
          tr.appendChild(el('td', null, String(r.Title || '')));
          tr.appendChild(el('td', null, String(r.WorkGroup || '')));
          tr.appendChild(el('td', null, String(r.Status || '')));
          tr.appendChild(el('td', null, String(r.Type || '')));
          tr.appendChild(el('td', null, String(r.ProposalAImpact || '')));
          tr.appendChild(el('td', null, String(r.ProposalBImpact || '')));
          tbody.appendChild(tr);
        }
        countSpan.textContent = needle.length > 0 ? (filtered.length + ' of ' + res.rows.length) : String(res.rows.length);
      };

      table.appendChild(tbody);
      main.appendChild(table);

      let debounce = 0;
      input.addEventListener('input', function () {
        if (debounce) window.clearTimeout(debounce);
        debounce = window.setTimeout(function () { renderRows(input.value); }, 150);
      });
      renderRows('');
      updateHeaderAffordances();
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

      // Topic back-link(s): if this ticket is a member of one or more
      // topics, render one `Member of topic: <ShortDescription>` line
      // per topic, sorted by short description for deterministic order.
      // Defensive try/catch: older inlined DBs may pre-date the topic
      // schema entirely.
      let topicMemberships = { rows: [] };
      try {
        topicMemberships = query(
          'SELECT t.Id AS TopicId, t.ShortDescription AS Short ' +
          'FROM prepared_ticket_topic_members m ' +
          'INNER JOIN prepared_ticket_topics t ON t.RowId = m.TopicRowId ' +
          'WHERE m.TicketKey = $k ORDER BY t.ShortDescription',
          { $k: key });
      } catch (e) {
        topicMemberships = { rows: [] };
      }
      for (let ti = 0; ti < topicMemberships.rows.length; ti++) {
        const tr = topicMemberships.rows[ti];
        const p = el('p', { class: 'topic-back' });
        p.appendChild(document.createTextNode('Member of topic: '));
        p.appendChild(el('a', {
          href: '#/topic/' + encodeURIComponent(String(tr.TopicId)) + currentHashSuffix(),
        }, String(tr.Short || '')));
        main.appendChild(p);
      }

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

    topics: function (main) {
      renderChipBanner(main);

      const chip = buildTopicChipWhere();
      const baseSql =
        'SELECT t.Id AS Id, t.ShortDescription AS ShortDescription, ' +
        '       t.LongerDescription AS LongerDescription, ' +
        '       t.WorkGroupDisplay AS WorkGroupDisplay, ' +
        '       t.WorkGroupClean AS WorkGroupClean, ' +
        '       t.Specification AS Specification, t.Type AS Type, ' +
        '       t.RenderOrderHint AS RenderOrderHint, ' +
        '       (SELECT COUNT(*) FROM prepared_ticket_topic_groups g WHERE g.TopicRowId = t.RowId) AS GroupCount, ' +
        '       (SELECT COUNT(*) FROM prepared_ticket_topic_members m WHERE m.TopicRowId = t.RowId) AS TicketCount ' +
        'FROM prepared_ticket_topics t';
      const orderBy =
        ' ORDER BY (CASE WHEN t.RenderOrderHint IS NULL THEN 1 ELSE 0 END), ' +
        't.RenderOrderHint ASC, t.ShortDescription ASC';
      const finalSql = baseSql + chip.where + orderBy;
      const res = query(finalSql, chip.params);

      const heading = hasAnyActiveChips() ? 'Filtered topic list' : 'Topics';
      main.appendChild(el('h2', null, heading + ' (' + res.rows.length + ')'));

      if (res.rows.length === 0) {
        main.appendChild(el('p', { class: 'muted' },
          hasAnyActiveChips() ? 'No topics match this filter.' : 'No topics in this run.'));
        return;
      }

      const filterRow = el('div', { class: 'filter-row' });
      const input = el('input', {
        type: 'text',
        placeholder: 'Filter by topic description…',
        autocomplete: 'off',
      });
      filterRow.appendChild(input);
      main.appendChild(filterRow);

      const countWrap = el('p', { class: 'muted' });
      const countSpan = el('span', null, String(res.rows.length));
      countWrap.appendChild(countSpan);
      countWrap.appendChild(document.createTextNode(' rows'));
      main.appendChild(countWrap);

      const columns = [
        { label: 'Topic',     get: function (r) { return String(r.ShortDescription || ''); }, cmp: 'ci' },
        { label: 'Workgroup', get: function (r) { return String(r.WorkGroupDisplay || ''); }, cmp: 'ci' },
        { label: 'Spec',      get: function (r) { return String(r.Specification || ''); },   cmp: 'ci' },
        { label: 'Type',      get: function (r) { return String(r.Type || ''); },            cmp: 'ci' },
        { label: 'Groups',    get: function (r) { return String(r.GroupCount != null ? r.GroupCount : ''); }, cmp: 'num' },
        { label: 'Tickets',   get: function (r) { return String(r.TicketCount != null ? r.TicketCount : ''); }, cmp: 'num' },
      ];

      const table = el('table');
      const thead = el('thead');
      const headRow = el('tr');

      // No initial sort column — keep the SQL-driven order
      // (`RenderOrderHint` then `ShortDescription`) on first render.
      let sortCol = null;
      let sortDir = 'asc';

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
          if (ev.key === 'Enter' || ev.key === ' ') {
            ev.preventDefault();
            onActivate();
          }
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
        if (cmpType === 'num') {
          return function (a, b) {
            const na = Number(a);
            const nb = Number(b);
            if (!isFinite(na) && !isFinite(nb)) return 0;
            if (!isFinite(na)) return 1;
            if (!isFinite(nb)) return -1;
            return na - nb;
          };
        }
        return function (a, b) {
          return a.localeCompare(b, undefined, { sensitivity: 'base' });
        };
      }

      const tbody = el('tbody');
      const renderRows = function (needle) {
        clearChildren(tbody);
        const n = needle.toLowerCase();
        const filtered = [];
        for (let i = 0; i < res.rows.length; i++) {
          const r = res.rows[i];
          if (n.length > 0) {
            const hay = String(r.ShortDescription || '') + '\n' + String(r.LongerDescription || '');
            if (hay.toLowerCase().indexOf(n) < 0) continue;
          }
          filtered.push(r);
        }

        if (sortCol) {
          let activeCol = null;
          for (let i = 0; i < columns.length; i++) {
            if (columns[i].label === sortCol) { activeCol = columns[i]; break; }
          }
          if (activeCol) {
            const cmp = compareFor(activeCol.cmp);
            const dirMul = (sortDir === 'desc') ? -1 : 1;
            const get = activeCol.get;
            filtered.sort(function (a, b) { return cmp(get(a), get(b)) * dirMul; });
          }
        }

        const suffix = currentHashSuffix();
        for (let i = 0; i < filtered.length; i++) {
          const r = filtered[i];
          const tr = el('tr');
          const topicCell = el('td');
          topicCell.appendChild(el('a', {
            href: '#/topic/' + encodeURIComponent(String(r.Id)) + suffix,
          }, String(r.ShortDescription || '')));
          tr.appendChild(topicCell);
          tr.appendChild(el('td', null, String(r.WorkGroupDisplay || '')));
          tr.appendChild(el('td', null, String(r.Specification || '')));
          tr.appendChild(el('td', null, String(r.Type || '')));
          tr.appendChild(el('td', null, String(r.GroupCount != null ? r.GroupCount : '')));
          tr.appendChild(el('td', null, String(r.TicketCount != null ? r.TicketCount : '')));
          tbody.appendChild(tr);
        }
        countSpan.textContent = needle.length > 0
          ? (filtered.length + ' of ' + res.rows.length)
          : String(res.rows.length);
      };

      table.appendChild(tbody);
      main.appendChild(table);

      let debounce = 0;
      input.addEventListener('input', function () {
        if (debounce) window.clearTimeout(debounce);
        debounce = window.setTimeout(function () { renderRows(input.value); }, 150);
      });
      renderRows('');
      updateHeaderAffordances();
    },

    topic: function (main, topicId) {
      const head = query(
        'SELECT * FROM prepared_ticket_topics WHERE Id = $id',
        { $id: topicId });
      if (head.rows.length === 0) {
        renderChipBanner(main);
        main.appendChild(el('p', { class: 'error' }, 'No topic with id ' + topicId + '.'));
        main.appendChild(el('p', null, el('a', {
          href: '#/topics' + currentHashSuffix(),
        }, '← Back to topics')));
        return;
      }
      const t = head.rows[0];

      // Overwrite the route-time breadcrumb placeholder with the
      // resolved short description.
      setBreadcrumb([
        { label: 'Home', href: '#/' },
        { label: 'Topics', href: '#/topics' + currentHashSuffix() },
        { label: String(t.ShortDescription || topicId), href: null },
      ]);

      renderChipBanner(main);

      const headerSection = el('section', { class: 'topic-detail' });
      headerSection.appendChild(el('h2', null, String(t.ShortDescription || '')));
      const metaParts = [];
      if (t.WorkGroupDisplay) metaParts.push(String(t.WorkGroupDisplay));
      if (t.Specification) metaParts.push(String(t.Specification));
      if (t.Type) metaParts.push(String(t.Type));
      if (metaParts.length > 0) {
        headerSection.appendChild(el('p', { class: 'topic-meta' }, metaParts.join(' · ')));
      }
      if (t.LongerDescription) {
        headerSection.appendChild(el('p', { class: 'topic-longer' }, String(t.LongerDescription)));
      }
      main.appendChild(headerSection);

      const groupsRes = query(
        'SELECT RowId, Id, FirstTicketKey, Rationale, OrderInTopic ' +
        'FROM prepared_ticket_topic_groups WHERE TopicRowId = $rid ' +
        'ORDER BY OrderInTopic, RowId',
        { $rid: t.RowId });

      const membersRes = query(
        'SELECT m.TopicGroupRowId, m.OrderInContainer, m.TicketKey, ' +
        '       jst.Title, jst.Status, jst.Type ' +
        'FROM prepared_ticket_topic_members m ' +
        'LEFT JOIN jira_processing_source_tickets jst ON jst.Key = m.TicketKey ' +
        'WHERE m.TopicRowId = $rid ' +
        'ORDER BY m.OrderInContainer, m.TicketKey',
        { $rid: t.RowId });

      // Partition members by TopicGroupRowId.
      /** @type {{[rowId: string]: any[]}} */
      const byGroup = Object.create(null);
      const ungrouped = [];
      for (let i = 0; i < membersRes.rows.length; i++) {
        const m = membersRes.rows[i];
        if (m.TopicGroupRowId == null) {
          ungrouped.push(m);
        } else {
          const k = String(m.TopicGroupRowId);
          if (!byGroup[k]) byGroup[k] = [];
          byGroup[k].push(m);
        }
      }

      function renderTicketTable(items) {
        const table = el('table');
        const thead = el('thead');
        const headRow = el('tr');
        headRow.appendChild(el('th', null, 'Key'));
        headRow.appendChild(el('th', null, 'Title'));
        headRow.appendChild(el('th', null, 'Status'));
        headRow.appendChild(el('th', null, 'Type'));
        thead.appendChild(headRow);
        table.appendChild(thead);
        const tbody = el('tbody');
        for (let i = 0; i < items.length; i++) {
          const r = items[i];
          const tr = el('tr');
          const keyCell = el('td');
          keyCell.appendChild(el('a', {
            href: '#/ticket/' + encodeURIComponent(String(r.TicketKey)),
          }, String(r.TicketKey)));
          tr.appendChild(keyCell);
          tr.appendChild(el('td', null, String(r.Title || '')));
          tr.appendChild(el('td', null, String(r.Status || '')));
          tr.appendChild(el('td', null, String(r.Type || '')));
          tbody.appendChild(tr);
        }
        table.appendChild(tbody);
        return table;
      }

      for (let gi = 0; gi < groupsRes.rows.length; gi++) {
        const g = groupsRes.rows[gi];
        const section = el('section', { class: 'topic-group' });
        section.appendChild(el('h3', null, 'Group: ' + String(g.FirstTicketKey || '')));
        if (g.Rationale) {
          const p = el('p', { class: 'muted' });
          p.appendChild(document.createTextNode('Rationale: '));
          p.appendChild(document.createTextNode(String(g.Rationale)));
          section.appendChild(p);
        }
        const items = byGroup[String(g.RowId)] || [];
        if (items.length === 0) {
          section.appendChild(el('p', { class: 'muted' }, 'No tickets in this group.'));
        } else {
          section.appendChild(renderTicketTable(items));
        }
        main.appendChild(section);
      }

      if (ungrouped.length > 0) {
        const section = el('section', { class: 'topic-group' });
        section.appendChild(el('h3', null,
          groupsRes.rows.length > 0 ? 'Other tickets in this topic' : 'Tickets in this topic'));
        section.appendChild(renderTicketTable(ungrouped));
        main.appendChild(section);
      }

      if (groupsRes.rows.length === 0 && ungrouped.length === 0) {
        main.appendChild(el('p', { class: 'muted' }, 'No tickets in this topic.'));
      }

      const back = el('p', { class: 'topic-back' });
      back.appendChild(el('a', { href: '#/topics' + currentHashSuffix() }, '← Back to topics'));
      main.appendChild(back);
    },
  };

  // Builds a crosscut <section> for `route` (e.g., 'by-workgroup'). When
  // `applyAutoHide` is true (landing grid), returns null if the column
  // should be hidden because its own dimension is pinned by a chip or
  // has ≤ 1 distinct non-null value in the post-chip data set.
  function buildSummarySectionFor(route, applyAutoHide) {
    const cfg = Crosscuts[route];
    if (!cfg) return null;

    // Exclude the column's own dim from the chip WHERE so the column
    // doesn't pre-filter against the value it's about to render.
    const ownDim = cfg.dim;
    const chipKeys = ownDim
      ? buildChipKeysSubquery([ownDim])
      : buildChipKeysSubquery([]);
    const sql = (typeof cfg.sql === 'function') ? cfg.sql(chipKeys.sql) : cfg.sql;
    const res = query(sql, Object.keys(chipKeys.params).length > 0 ? chipKeys.params : null);

    if (applyAutoHide && ownDim) {
      // Hide if the dim is already pinned by an active chip.
      if (ActiveChips[ownDim] && ActiveChips[ownDim].length > 0) return null;
      // Hide if there is ≤ 1 distinct non-"(unknown)" value in the
      // post-chip data set (column would add only noise).
      let distinctReal = 0;
      for (let i = 0; i < res.rows.length; i++) {
        const k = res.rows[i].k;
        if (k != null && String(k) !== '' && String(k) !== '(unknown)') distinctReal++;
      }
      if (distinctReal <= 1) return null;
    }

    const section = el('section');
    section.appendChild(el('h2', null, cfg.title));
    if (res.rows.length === 0) {
      section.appendChild(el('p', { class: 'muted' }, 'No data.'));
      return section;
    }
    const table = el('table');
    const thead = el('thead');
    const headRow = el('tr');
    headRow.appendChild(el('th', null, cfg.title.replace(/^By /, '')));
    headRow.appendChild(el('th', null, 'Count'));
    thead.appendChild(headRow);
    table.appendChild(thead);
    const tbody = el('tbody');
    for (let i = 0; i < res.rows.length; i++) {
      const r = res.rows[i];
      const tr = el('tr');
      const keyCell = el('td');
      const keyText = String(r.k != null ? r.k : '');
      if (ownDim) {
        // Filterable column: row toggles the chip on the current view.
        const btn = el('button', { type: 'button', class: 'crosscut-row' }, keyText);
        (function (capturedDim, capturedValue) {
          btn.addEventListener('click', function () {
            toggleChip(capturedDim, capturedValue);
          });
        })(ownDim, keyText);
        keyCell.appendChild(btn);
      } else {
        // Non-filterable column: row stays a navigation link.
        keyCell.appendChild(el('a', { href: '#/' + route + '/' + encodeURIComponent(keyText) }, keyText));
      }
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
