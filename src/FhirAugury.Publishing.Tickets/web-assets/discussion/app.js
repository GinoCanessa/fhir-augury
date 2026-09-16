// Tickets for Discussion SPA. The browser reads only renderer schema v3.
(function () {
  'use strict';

  var db = null;
  var presentation = null;
  var components = window.DiscussionComponents;
  var state = window.DiscussionState;
  var routeState = null;
  var currentExport = null;
  var inRunKeys = Object.create(null);
  var tableStates = Object.create(null);
  var viewCleanup = [];
  var viewRevision = 0;
  var filterFocus = null;

  var filterableDimensions = [];
  var generationDimensions = ['spec', 'project', 'wg'];
  var rendererDimensions = Object.create(null);
  var facetKeyParameters = Object.create(null);
  var generationChips = {};
  var activeChips = Object.freeze({});
  var facetDimensions = Object.freeze([]);
  var facetDimensionsByKey = Object.create(null);
  var facetCatalog = Object.create(null);
  var Crosscuts = Object.create(null);
  var landingCrosscutOrder = Object.freeze([]);

  function parsePresentation() {
    var element = document.getElementById('site-presentation');
    if (!element) throw new Error('Discussion presentation data is missing.');
    var value = JSON.parse(element.textContent || '');
    if (!value || value.rendererSchemaVersion !== 3 ||
        typeof value.siteName !== 'string' ||
        typeof value.baseTitle !== 'string') {
      throw new Error('Discussion presentation data is invalid.');
    }
    if (!value.filters || typeof value.filters !== 'object') {
      value.filters = {};
    }
    if (!value.readiness || typeof value.readiness !== 'object' ||
        !Array.isArray(value.readiness.reasons)) {
      throw new Error('Discussion publication readiness is missing.');
    }
    if (!value.corpusSummary || typeof value.corpusSummary !== 'object' ||
        !Array.isArray(value.corpusSummary.linksByKind) ||
        typeof value.corpusSummary.ticketCount !== 'number' ||
        typeof value.corpusSummary.exportedProjectCount !== 'number' ||
        typeof value.corpusSummary.validJiraUpdatedAtCount !== 'number' ||
        ['empty', 'none', 'partial', 'complete'].indexOf(
          value.corpusSummary.dateCoverage) < 0) {
      throw new Error('Discussion corpus summary is missing or invalid.');
    }
    return value;
  }

  function decodeBase64(value) {
    var binary = atob(value);
    var bytes = new Uint8Array(binary.length);
    for (var index = 0; index < binary.length; index++) {
      bytes[index] = binary.charCodeAt(index);
    }
    return bytes;
  }

  function loadFacetCatalog() {
    var dimensionRows = query(
      'SELECT Dimension, Route, Label, SortOrder, ShowInList ' +
      'FROM facet_dimensions ORDER BY SortOrder',
      null).rows;
    var dimensions = [];
    var routes = [];
    for (var dimensionIndex = 0;
      dimensionIndex < dimensionRows.length;
      dimensionIndex++) {
      var dimensionRow = dimensionRows[dimensionIndex];
      var dimensionKey = String(dimensionRow.Dimension);
      var definition = Object.freeze({
        dimension: dimensionKey,
        route: String(dimensionRow.Route),
        label: String(dimensionRow.Label),
        sortOrder: Number(dimensionRow.SortOrder),
        showInList: Number(dimensionRow.ShowInList) === 1
      });
      dimensions.push(definition);
      facetDimensionsByKey[dimensionKey] = definition;
      rendererDimensions[dimensionKey] = dimensionKey;
      facetKeyParameters[dimensionKey] = dimensionKey + 'Key';
      Crosscuts[definition.route] = Object.freeze({
        pageTitle: 'By ' + definition.label.toLowerCase(),
        columnLabel: definition.label,
        dimension: dimensionKey
      });
      routes.push(definition.route);
    }
    facetDimensions = Object.freeze(dimensions);
    filterableDimensions = Object.freeze(dimensions.map(function (value) {
      return value.dimension;
    }));
    landingCrosscutOrder = Object.freeze(routes);

    var rows = query(
      'SELECT DISTINCT Dimension, ValueKey, DisplayValue, SortKey, IsUnknown ' +
      'FROM ticket_facets ' +
      'ORDER BY Dimension, IsUnknown, SortKey, DisplayValue, ValueKey',
      null).rows;
    for (var index = 0; index < rows.length; index++) {
      var row = rows[index];
      var dimension = String(row.Dimension);
      if (!facetCatalog[dimension]) {
        facetCatalog[dimension] = {
          byKey: Object.create(null),
          byDisplay: Object.create(null)
        };
      }
      var entry = {
        valueKey: String(row.ValueKey),
        displayValue: String(row.DisplayValue),
        isUnknown: Number(row.IsUnknown) === 1
      };
      facetCatalog[dimension].byKey[entry.valueKey.toLowerCase()] = entry;
      var displayKey = entry.displayValue.toLowerCase();
      if (!facetCatalog[dimension].byDisplay[displayKey]) {
        facetCatalog[dimension].byDisplay[displayKey] = entry;
      }
    }
  }

  function resolveFacetValueKey(dimension, rawValue) {
    var value = rawValue == null ? '' : String(rawValue).trim();
    if (!value) return null;
    var rendererDimension = rendererDimensions[dimension];
    var catalog = facetCatalog[rendererDimension];
    if (catalog) {
      var byKey = catalog.byKey[value.toLowerCase()];
      if (byKey) return byKey.valueKey;
    }
    return value;
  }

  function resolveLegacyFacetDisplayValue(dimension, rawValue) {
    var value = rawValue == null ? '' : String(rawValue).trim();
    if (!value) return null;
    if (value.toLowerCase() === '(unknown)') return '__unknown__';
    var rendererDimension = rendererDimensions[dimension];
    var catalog = facetCatalog[rendererDimension];
    if (catalog) {
      var byDisplay = catalog.byDisplay[value.toLowerCase()];
      if (byDisplay) return byDisplay.valueKey;
    }
    return 'value:' + value;
  }

  function facetLabel(dimension, valueKey) {
    if (String(valueKey).toLowerCase() === '__unknown__') return '(unknown)';
    var catalog = facetCatalog[rendererDimensions[dimension]];
    var entry = catalog && catalog.byKey[String(valueKey).toLowerCase()];
    if (entry) return entry.displayValue;
    return String(valueKey).toLowerCase().indexOf('value:') === 0
      ? String(valueKey).slice(6)
      : String(valueKey);
  }

  function seedGenerationChips() {
    var filters = presentation.filters || {};
    var values = {
      spec: filters.specification,
      project: filters.project,
      wg: filters.workGroup
    };
    for (var index = 0; index < generationDimensions.length; index++) {
      var dimension = generationDimensions[index];
      var valueKey = resolveLegacyFacetDisplayValue(
        dimension,
        values[dimension]);
      if (valueKey) generationChips[dimension] = [valueKey];
    }
  }

  var App = {
    init: async function () {
      var main = document.getElementById('app');
      try {
        components = window.DiscussionComponents;
        if (!components) throw new Error('Discussion components failed to load.');
        if (!state) throw new Error('Discussion state failed to load.');
        presentation = parsePresentation();
        document.title = presentation.siteName;
        var heading = document.querySelector('header h1');
        if (heading) heading.textContent = presentation.siteName;

        var assetVersion = typeof window.__ASSET_VERSION__ === 'string'
          ? window.__ASSET_VERSION__
          : '';
        var encodedWasm = typeof window.__SQL_WASM__ === 'string'
          ? window.__SQL_WASM__
          : '';
        if (!encodedWasm) {
          throw new Error('The embedded SQL.js WebAssembly is missing.');
        }
        var wasmBinary = decodeBase64(encodedWasm);
        // initSqlJs is provided by the classic sql-wasm.js asset.
        var SQL = await initSqlJs({
          locateFile: function (fileName) {
            return 'assets/' + fileName +
              (assetVersion ? '?v=' + encodeURIComponent(assetVersion) : '');
          },
          wasmBinary: wasmBinary
        });
        var encodedDatabase = typeof window.__DB__ === 'string'
          ? window.__DB__
          : '';
        if (!encodedDatabase) {
          throw new Error('The embedded discussion database is missing.');
        }
        var bytes = decodeBase64(encodedDatabase);
        if (window.__DBGZ__) {
          if (typeof DecompressionStream !== 'function') {
            throw new Error(
              'This browser needs DecompressionStream to open the discussion database.');
          }
          var stream = new Blob([bytes]).stream()
            .pipeThrough(new DecompressionStream('gzip'));
          bytes = new Uint8Array(await new Response(stream).arrayBuffer());
        }
        db = new SQL.Database(bytes);

        loadFacetCatalog();
        var metadata = query(
          'SELECT RendererSchemaVersion, BaseTitle, SiteName, ' +
          'JiraSourceLastSuccessfulRefreshAt, ReadinessJson, CorpusSummaryJson, FilterSpecification, ' +
          'FilterProject, FilterWorkGroup FROM site_metadata',
          null).rows;
        if (metadata.length !== 1 ||
            Number(metadata[0].RendererSchemaVersion) !==
              Number(presentation.rendererSchemaVersion) ||
            String(metadata[0].BaseTitle) !== presentation.baseTitle ||
            String(metadata[0].SiteName) !== presentation.siteName ||
            JSON.stringify(JSON.parse(String(metadata[0].ReadinessJson))) !==
              JSON.stringify(presentation.readiness) ||
            JSON.stringify(JSON.parse(String(metadata[0].CorpusSummaryJson))) !==
              JSON.stringify(presentation.corpusSummary) ||
            metadata[0].FilterSpecification !== (presentation.filters.specification || null) ||
            metadata[0].FilterProject !== (presentation.filters.project || null) ||
            metadata[0].FilterWorkGroup !== (presentation.filters.workGroup || null) ||
            (metadata[0].JiraSourceLastSuccessfulRefreshAt == null
              ? presentation.jiraSourceLastSuccessfulRefreshAt != null
              : presentation.jiraSourceLastSuccessfulRefreshAt == null ||
                Date.parse(metadata[0].JiraSourceLastSuccessfulRefreshAt) !==
                  Date.parse(presentation.jiraSourceLastSuccessfulRefreshAt))) {
          throw new Error(
            'The embedded discussion database does not match its presentation.');
        }

        var keyRows = query('SELECT Key FROM tickets', null).rows;
        for (var keyIndex = 0; keyIndex < keyRows.length; keyIndex++) {
          inRunKeys[String(keyRows[keyIndex].Key).toLowerCase()] = true;
        }
        seedGenerationChips();
        exposeRuntimeTestHook();
      } catch (error) {
        renderError(main, 'Failed to load database: ' + error.message);
        return;
      }

      installCopyButton();
      window.addEventListener('hashchange', App.route);
      App.route();
    },

    route: function () {
      var main = document.getElementById('app');
      viewCleanup.forEach(function (cleanup) { cleanup(); });
      viewCleanup = [];
      viewRevision++;
      clearChildren(main);
      clearCopyExport();
      setDocumentTitle(null);

      var fullHash = window.location.hash || '#/';
      try {
        var previousRoute = routeState && routeState.route;
        routeState = readRouteState(fullHash);
        activeChips = state.activeFilters(routeState);
        var parts = routeState.route.split('/').filter(function (part) {
          return part.length > 0;
        });
        if (parts.length === 0) {
          setBreadcrumb([]);
          Views.landing(main);
        } else if (parts[0] === 'list') {
          setBreadcrumb([{ label: 'List', href: null }]);
          Views.list(main);
        } else if (parts[0] === 'topics') {
          setBreadcrumb([{ label: 'Topics', href: null }]);
          Views.topics(main);
        } else if (parts[0] === 'topic' && parts.length >= 2) {
          Views.topic(main, decodeURIComponent(parts[1]));
        } else if (parts[0] === 'ticket' && parts.length >= 2) {
          Views.ticket(main, decodeURIComponent(parts[1]));
        } else if (Crosscuts[parts[0]]) {
          if (parts.length >= 2) {
            redirectToFacetList(
              Crosscuts[parts[0]].dimension,
              decodeURIComponent(parts[1]));
            return;
          }
          setBreadcrumb([
            { label: Crosscuts[parts[0]].pageTitle, href: null }
          ]);
          Views.crosscut(main, parts[0]);
        } else {
          setBreadcrumb([]);
          Views.notFound(main, fullHash);
        }
        if (filterFocus || previousRoute === routeState.route) {
          restoreFilterFocus(main);
        }
      } catch (error) {
        renderError(main, 'Route render failed: ' + error.message);
      }
      filterFocus = null;
    }
  };

  function parseChipsFromQuery(queryPart) {
    var result = {};
    var parameters = new URLSearchParams(queryPart);
    for (var index = 0; index < filterableDimensions.length; index++) {
      var currentDimension = filterableDimensions[index];
      var existing = [];
      var rawKeyValues =
        parameters.getAll(facetKeyParameters[currentDimension]);
      for (var keyIndex = 0; keyIndex < rawKeyValues.length; keyIndex++) {
        var valueKey = resolveFacetValueKey(
          currentDimension,
          rawKeyValues[keyIndex]);
        if (valueKey) existing.push(valueKey);
      }
      var rawDisplayValues = parameters.getAll(currentDimension);
      for (var displayIndex = 0;
        displayIndex < rawDisplayValues.length;
        displayIndex++) {
        var legacyValueKey = resolveLegacyFacetDisplayValue(
          currentDimension,
          rawDisplayValues[displayIndex]);
        if (legacyValueKey) existing.push(legacyValueKey);
      }
      if (existing.length > 0) result[currentDimension] = existing;
    }
    return result;
  }

  function readRouteState(hash) {
    var parsed = state.parseHash(hash);
    return state.create(
      parsed.route,
      parseChipsFromQuery(parsed.query),
      generationChips,
      presentation.corpusSummary.exportedProjectCount);
  }

  function currentHashSuffix() {
    return state.querySuffix(routeState.filters, filterableDimensions);
  }

  function selectFacet(dimension, valueKey) {
    filterFocus = { dimension: dimension, valueKey: valueKey };
    window.location.hash = state.toHash(
      state.select(readRouteState(window.location.hash), dimension, valueKey),
      filterableDimensions);
  }

  function redirectToFacetList(dimension, rawValue) {
    var valueKey = resolveLegacyFacetDisplayValue(dimension, rawValue);
    if (!valueKey) return;
    window.location.hash = state.toHash(
      state.navigate(state.select(routeState, dimension, valueKey), 'list'),
      filterableDimensions);
  }

  function removeChipValue(dimension, valueKey) {
    filterFocus = { dimension: dimension };
    window.location.hash = state.toHash(
      state.remove(readRouteState(window.location.hash), dimension, valueKey),
      filterableDimensions);
  }

  function isGenerationChip(dimension, valueKey) {
    return (routeState.fixedFilters[dimension] || []).some(function (value) {
      return String(value).toLowerCase() === String(valueKey).toLowerCase();
    });
  }

  function isDimensionVisible(dimension) {
    return state.isDimensionVisible(routeState, dimension);
  }

  function restoreFilterFocus(main) {
    var buttons = main.querySelectorAll('.chip-remove');
    var target = null;
    for (var index = 0; index < buttons.length; index++) {
      var button = buttons[index];
      if (filterFocus &&
          button.getAttribute('data-dimension') === filterFocus.dimension &&
          (!filterFocus.valueKey ||
           button.getAttribute('data-value-key') === filterFocus.valueKey)) {
        target = button;
        break;
      }
    }
    target = target || buttons[0] || main.querySelector('.view-heading');
    if (target) target.focus();
  }

  function appendViewHeading(main, title) {
    main.appendChild(el('h2', {
      class: 'view-heading',
      tabindex: '-1'
    }, title));
  }

  function tableState(tableKey, initialSort) {
    var key = routeState.route + '|' + tableKey;
    if (!tableStates[key]) {
      tableStates[key] = { search: '', sort: initialSort };
    }
    return tableStates[key];
  }

  function visibleSort(savedSort, columns, fallback) {
    return savedSort && columns.some(function (column) {
      return column.key === savedSort.key;
    }) ? savedSort : fallback;
  }

  function bindTableSearch(input, localState, apply) {
    input.value = localState.search;
    var debounce = 0;
    var revision = viewRevision;
    viewCleanup.push(function () { window.clearTimeout(debounce); });
    input.addEventListener('input', function () {
      if (revision !== viewRevision) return;
      localState.search = input.value;
      window.clearTimeout(debounce);
      debounce = window.setTimeout(function () {
        if (revision === viewRevision) apply();
      }, 150);
    });
    apply();
  }

  function hasActiveChips() {
    for (var dimension in activeChips) {
      if (activeChips[dimension] && activeChips[dimension].length > 0) {
        return true;
      }
    }
    return false;
  }

  function renderChipBanner(main) {
    if (!hasActiveChips()) return;
    var banner = el('div', {
      id: 'filter-banner',
      role: 'group',
      'aria-label': 'Active ticket filters'
    });
    for (var index = 0; index < filterableDimensions.length; index++) {
      var dimension = filterableDimensions[index];
      var values = activeChips[dimension] || [];
      for (var valueIndex = 0; valueIndex < values.length; valueIndex++) {
        var valueKey = values[valueIndex];
        var label = facetLabel(dimension, valueKey);
        var chip = el('span', { class: 'filter-chip' });
        chip.appendChild(document.createTextNode(
          facetDimensionsByKey[dimension].label + ': ' + label));
        if (!isGenerationChip(dimension, valueKey)) {
          var removeButton = el('button', {
            type: 'button',
            class: 'chip-remove',
            'data-dimension': dimension,
            'data-value-key': valueKey,
            'aria-label': 'Remove ' + dimension + ' filter ' + label
          }, '×');
          (function (capturedDimension, capturedValueKey) {
            removeButton.addEventListener('click', function () {
              removeChipValue(capturedDimension, capturedValueKey);
            });
          })(dimension, valueKey);
          chip.appendChild(removeButton);
        } else {
          chip.className += ' filter-chip-fixed';
          chip.appendChild(el('span', {
            class: 'chip-fixed-label'
          }, ' (fixed at publication)'));
        }
        banner.appendChild(chip);
      }
    }
    main.appendChild(banner);
  }

  function buildTicketKeysSubquery(excludedDimensions) {
    var excluded = Object.create(null);
    var predicates = [];
    var parameters = {};
    for (var excludedIndex = 0;
      excludedDimensions && excludedIndex < excludedDimensions.length;
      excludedIndex++) {
      excluded[excludedDimensions[excludedIndex]] = true;
    }

    var predicateIndex = 0;
    for (var index = 0; index < filterableDimensions.length; index++) {
      var dimension = filterableDimensions[index];
      var values = activeChips[dimension] || [];
      if (excluded[dimension] || values.length === 0) continue;
      var dimensionParameter = '$facetDimension' + predicateIndex;
      parameters[dimensionParameter] = rendererDimensions[dimension];
      var valueParameters = [];
      for (var valueIndex = 0; valueIndex < values.length; valueIndex++) {
        var valueParameter =
          '$facetValue' + predicateIndex + '_' + valueIndex;
        parameters[valueParameter] = values[valueIndex];
        valueParameters.push(valueParameter);
      }
      predicates.push(
        'EXISTS (SELECT 1 FROM ticket_facets f' + predicateIndex +
        ' WHERE f' + predicateIndex + '.TicketKey = t.Key' +
        ' AND f' + predicateIndex + '.Dimension = ' + dimensionParameter +
        ' AND f' + predicateIndex + '.ValueKey IN (' +
        valueParameters.join(', ') + '))');
      predicateIndex++;
    }

    return {
      sql: 'SELECT t.Key FROM tickets t' +
        (predicates.length > 0 ? ' WHERE ' + predicates.join(' AND ') : ''),
      params: parameters
    };
  }

  function mergeParameters(target, source) {
    for (var key in source) target[key] = source[key];
  }

  function readTicketListRows(ticketKeys) {
    var rows = query(
      'SELECT Key, Title, Status, RequestSummary AS SearchBody ' +
      'FROM tickets WHERE Key IN (' + ticketKeys.sql + ') ' +
      'ORDER BY Key COLLATE NOCASE',
      ticketKeys.params).rows;
    var facets = query(
      'SELECT TicketKey, Dimension, ValueKey, DisplayValue, SortKey, ' +
      'IsUnknown FROM ticket_facets WHERE TicketKey IN (' +
      ticketKeys.sql + ') ORDER BY TicketKey COLLATE NOCASE, Dimension, ' +
      'IsUnknown, SortKey, DisplayValue COLLATE NOCASE, ValueKey',
      ticketKeys.params).rows;
    var byTicket = Object.create(null);
    for (var facetIndex = 0; facetIndex < facets.length; facetIndex++) {
      var facet = facets[facetIndex];
      var ticketKey = String(facet.TicketKey).toLowerCase();
      if (!byTicket[ticketKey]) byTicket[ticketKey] = Object.create(null);
      var dimension = String(facet.Dimension);
      if (!byTicket[ticketKey][dimension]) {
        byTicket[ticketKey][dimension] = [];
      }
      byTicket[ticketKey][dimension].push(facet);
    }
    for (var rowIndex = 0; rowIndex < rows.length; rowIndex++) {
      rows[rowIndex].Facets =
        byTicket[String(rows[rowIndex].Key).toLowerCase()] ||
        Object.create(null);
    }
    return rows;
  }

  function visibleListFacetDimensions() {
    return facetDimensions.filter(function (definition) {
      return definition.showInList && isDimensionVisible(definition.dimension);
    });
  }

  function createTicketListColumns() {
    var columns = [
      {
        key: 'key',
        label: 'Key',
        value: function (row) { return row.Key; },
        compare: 'natural',
        render: function (row) {
          return el('a', {
            href: '#/ticket/' + encodeURIComponent(String(row.Key)) +
              currentHashSuffix()
          }, String(row.Key));
        }
      },
      {
        key: 'title',
        label: 'Title',
        value: function (row) { return row.Title; }
      },
      {
        key: 'status',
        label: 'Status',
        value: function (row) { return displayValue(row.Status); }
      }
    ];
    var definitions = visibleListFacetDimensions();
    for (var index = 0; index < definitions.length; index++) {
      (function (definition) {
        columns.push({
          key: 'facet-' + definition.dimension,
          label: definition.label,
          value: function (row) {
            return facetValues(row, definition.dimension)
              .map(function (facet) {
                return String(facet.DisplayValue);
              })
              .join(' · ');
          },
          render: function (row) {
            return renderTicketFacetCell(row, definition);
          }
        });
      })(definitions[index]);
    }
    return columns;
  }

  function facetValues(row, dimension) {
    return row.Facets && row.Facets[dimension]
      ? row.Facets[dimension]
      : [];
  }

  function renderTicketFacetCell(row, definition) {
    var values = facetValues(row, definition.dimension);
    var container = el('span', { class: 'ticket-facet-values' });
    for (var index = 0; index < values.length; index++) {
      if (index > 0) {
        container.appendChild(document.createTextNode(' · '));
      }
      var value = values[index];
      var button = el('button', {
        type: 'button',
        class: 'ticket-facet-value',
        'data-dimension': definition.dimension,
        'data-value-key': String(value.ValueKey)
      }, String(value.DisplayValue));
      (function (dimension, valueKey) {
        button.addEventListener('click', function () {
          selectFacet(dimension, valueKey);
        });
      })(definition.dimension, String(value.ValueKey));
      container.appendChild(button);
    }
    return container;
  }

  var Views = {
    landing: function (main) {
      appendViewHeading(main, 'Overview');
      renderChipBanner(main);
      renderReadiness(main);
      renderCorpusCoverage(main);
      var ticketKeys = buildTicketKeysSubquery([]);
      var totalRows = query(
        'SELECT COUNT(*) AS Count FROM (' + ticketKeys.sql + ')',
        ticketKeys.params).rows;
      var total = totalRows.length ? Number(totalRows[0].Count) : 0;
      renderMatchSummary(main, total, true);

      var grid = el('div', { class: 'summary-grid' });
      for (var index = 0; index < landingCrosscutOrder.length; index++) {
        var route = landingCrosscutOrder[index];
        if (isDimensionVisible(Crosscuts[route].dimension)) {
          grid.appendChild(buildCrosscutSection(route));
        }
      }
      main.appendChild(grid);
    },

    crosscut: function (main, route) {
      var config = Crosscuts[route];
      renderChipBanner(main);
      setDocumentTitle(config.pageTitle);
      appendViewHeading(main, config.pageTitle);
      var ticketKeys = buildTicketKeysSubquery([]);
      var total = Number(query(
        'SELECT COUNT(*) AS Count FROM (' + ticketKeys.sql + ')',
        ticketKeys.params).rows[0].Count);
      renderMatchSummary(main, total, false);
      if (isDimensionVisible(config.dimension)) {
        main.appendChild(buildCrosscutSection(route));
      } else {
        main.appendChild(el('p', { class: 'filter-recovery' },
          routeState.fixedFilters[config.dimension]
            ? config.columnLabel + ' is fixed by publication filters.'
            : config.dimension === 'project' &&
              routeState.exportedProjectCount === 1
              ? 'Project is hidden because this export contains one project.'
              : config.columnLabel +
                ' is filtered. Remove its filter above to choose again.'));
      }
      main.appendChild(el('p', null, el('a', {
        href: '#/' + currentHashSuffix()
      }, '← Back to overview')));
    },

    list: function (main) {
      renderChipBanner(main);
      setDocumentTitle('Ticket list');
      var ticketKeys = buildTicketKeysSubquery([]);
      var rows = readTicketListRows(ticketKeys);

      appendViewHeading(main,
        (hasActiveChips() ? 'Filtered ticket list' : 'All prepared tickets') +
        ' (' + rows.length + ')');
      if (rows.length === 0) renderNoMatch(main);
      var filterRow = el('div', { class: 'filter-row' });
      var input = el('input', {
        type: 'text',
        placeholder: 'Filter by key, title, or request summary…',
        autocomplete: 'off',
        'aria-label': 'Filter ticket list'
      });
      filterRow.appendChild(input);
      main.appendChild(filterRow);

      var count = el('span', { class: 'list-row-count' }, String(rows.length));
      main.appendChild(el('p', { class: 'muted', role: 'status' }, [
        count,
        document.createTextNode(' rows')
      ]));

      var columns = createTicketListColumns();
      var defaultSort = { key: 'key', direction: 'ascending' };
      var localState = tableState('tickets', defaultSort);
      var table = components.createSortableTable({
        rows: rows,
        initialSort: visibleSort(localState.sort, columns, defaultSort),
        onSortChanged: function (sort) { localState.sort = sort; },
        className: 'ticket-list-table',
        ariaLabel: 'Prepared tickets',
        columns: columns
      });
      var tableRegion = el('div', {
        class: 'table-overflow ticket-list-overflow',
        role: 'region',
        tabindex: '0',
        'aria-label': 'Prepared ticket table'
      });
      tableRegion.appendChild(table.element);
      main.appendChild(tableRegion);

      bindTableSearch(input, localState, function () {
        var needle = localState.search.toLowerCase();
        var filtered = rows.filter(function (row) {
          return !needle ||
            (String(row.Key || '') + '\n' +
             String(row.Title || '') + '\n' +
             String(row.SearchBody || '')).toLowerCase().indexOf(needle) >= 0;
        });
        table.setRows(filtered);
        count.textContent = needle
          ? filtered.length + ' of ' + rows.length
          : String(rows.length);
      });
    },

    topics: function (main) {
      renderChipBanner(main);
      setDocumentTitle('Topics');
      var ticketKeys = buildTicketKeysSubquery([]);
      var parameters = {};
      mergeParameters(parameters, ticketKeys.params);
      var where = hasActiveChips()
        ? ' WHERE t.RowId IN (' +
          'SELECT DISTINCT m.TopicRowId FROM topic_members m ' +
          'WHERE m.TicketKey IN (' + ticketKeys.sql + '))'
        : '';
      var rows = query(
        'SELECT t.Id, t.ShortDescription, t.LongerDescription, ' +
        't.WorkGroupDisplay, t.Specification, t.Type, t.RenderOrderHint, ' +
        '(SELECT COUNT(*) FROM topic_groups g ' +
        ' WHERE g.TopicRowId = t.RowId) AS GroupCount, ' +
        '(SELECT COUNT(*) FROM topic_members m ' +
        ' WHERE m.TopicRowId = t.RowId) AS TicketCount ' +
        'FROM topics t' + where +
        ' ORDER BY CASE WHEN t.RenderOrderHint IS NULL THEN 1 ELSE 0 END, ' +
        't.RenderOrderHint, t.RowId',
        parameters).rows;

      appendViewHeading(main,
        (hasActiveChips() ? 'Filtered topic list' : 'Topics') +
        ' (' + rows.length + ')');
      if (rows.length === 0) {
        main.appendChild(el(
          'p',
          { class: 'muted' },
          hasActiveChips()
            ? 'No topics match this filter.'
            : 'No topics in this run.'));
      }

      var filterRow = el('div', { class: 'filter-row' });
      var input = el('input', {
        type: 'text',
        placeholder: 'Filter by topic description…',
        autocomplete: 'off',
        'aria-label': 'Filter topic list'
      });
      filterRow.appendChild(input);
      main.appendChild(filterRow);
      var count = el('span', null, String(rows.length));
      main.appendChild(el('p', { class: 'muted' }, [
        count,
        document.createTextNode(' rows')
      ]));

      var localState = tableState('topics', null);
      var table = components.createSortableTable({
        rows: rows,
        initialSort: localState.sort,
        onSortChanged: function (sort) { localState.sort = sort; },
        ariaLabel: 'Discussion topics',
        columns: [
          {
            key: 'topic',
            label: 'Topic',
            value: function (row) { return row.ShortDescription; },
            render: function (row) {
              return el('a', {
                href: '#/topic/' + encodeURIComponent(String(row.Id)) +
                  currentHashSuffix()
              }, String(row.ShortDescription || ''));
            }
          },
          {
            key: 'workgroup',
            label: 'Workgroup',
            value: function (row) { return row.WorkGroupDisplay; }
          },
          {
            key: 'specification',
            label: 'Spec',
            value: function (row) { return row.Specification; }
          },
          {
            key: 'type',
            label: 'Type',
            value: function (row) { return row.Type; }
          },
          {
            key: 'groups',
            label: 'Groups',
            value: function (row) { return row.GroupCount; },
            compare: 'numeric'
          },
          {
            key: 'tickets',
            label: 'Tickets',
            value: function (row) { return row.TicketCount; },
            compare: 'numeric'
          }
        ]
      });
      main.appendChild(table.element);

      bindTableSearch(input, localState, function () {
        var needle = localState.search.toLowerCase();
        var filtered = rows.filter(function (row) {
          return !needle ||
            (String(row.ShortDescription || '') + '\n' +
             String(row.LongerDescription || ''))
              .toLowerCase().indexOf(needle) >= 0;
        });
        table.setRows(filtered);
        count.textContent = needle
          ? filtered.length + ' of ' + rows.length
          : String(rows.length);
      });
    },

    topic: function (main, topicId) {
      var rows = query(
        'SELECT * FROM topics WHERE Id = $id',
        { $id: topicId }).rows;
      if (rows.length === 0) {
        renderChipBanner(main);
        main.appendChild(el(
          'p',
          { class: 'error' },
          'No topic with id ' + topicId + '.'));
        return;
      }
      var topic = rows[0];
      setDocumentTitle(String(topic.ShortDescription || topicId));
      setBreadcrumb([
        { label: 'Topics', href: '#/topics' + currentHashSuffix() },
        {
          label: String(topic.ShortDescription || topicId),
          href: null
        }
      ]);
      renderChipBanner(main);

      var header = el('section', { class: 'topic-detail' });
      appendViewHeading(header, String(topic.ShortDescription || ''));
      var metadata = [
        topic.WorkGroupDisplay,
        topic.Specification,
        topic.Type
      ].filter(function (value) {
        return value != null && String(value).trim() !== '';
      });
      if (metadata.length > 0) {
        header.appendChild(el(
          'p',
          { class: 'topic-meta' },
          metadata.join(' · ')));
      }
      if (topic.LongerDescription) {
        header.appendChild(el(
          'p',
          { class: 'topic-longer' },
          String(topic.LongerDescription)));
      }
      main.appendChild(header);

      var groups = query(
        'SELECT RowId, Id, FirstTicketKey, Rationale, OrderInTopic ' +
        'FROM topic_groups WHERE TopicRowId = $topicRowId ' +
        'ORDER BY OrderInTopic, RowId',
        { $topicRowId: topic.RowId }).rows;
      var members = query(
        'SELECT TopicGroupRowId, TicketKey, Title, Status, Type, ' +
        'OrderInContainer FROM topic_members ' +
        'WHERE TopicRowId = $topicRowId ' +
        'ORDER BY CASE WHEN TopicGroupRowId IS NULL THEN 1 ELSE 0 END, ' +
        'TopicGroupRowId, OrderInContainer',
        { $topicRowId: topic.RowId }).rows;
      var membersByGroup = Object.create(null);
      var ungrouped = [];
      for (var memberIndex = 0;
        memberIndex < members.length;
        memberIndex++) {
        var member = members[memberIndex];
        if (member.TopicGroupRowId == null) {
          ungrouped.push(member);
        } else {
          var groupKey = String(member.TopicGroupRowId);
          if (!membersByGroup[groupKey]) membersByGroup[groupKey] = [];
          membersByGroup[groupKey].push(member);
        }
      }

      for (var groupIndex = 0; groupIndex < groups.length; groupIndex++) {
        var group = groups[groupIndex];
        var groupSection = el('section', { class: 'topic-group' });
        var groupHeading = 'Group: ' + String(group.FirstTicketKey || '');
        groupSection.appendChild(el('h3', null, groupHeading));
        if (group.Rationale) {
          groupSection.appendChild(el(
            'p',
            { class: 'muted' },
            'Rationale: ' + String(group.Rationale)));
        }
        var groupMembers = membersByGroup[String(group.RowId)] || [];
        if (groupMembers.length > 0) {
          groupSection.appendChild(
            createGroupedTicketTable(groupMembers, groupHeading));
        } else {
          groupSection.appendChild(el(
            'p',
            { class: 'muted' },
            'No tickets in this group.'));
        }
        main.appendChild(groupSection);
      }

      if (ungrouped.length > 0) {
        var ungroupedSection = el('section', { class: 'topic-group' });
        var ungroupedHeading = groups.length > 0
          ? 'Other tickets in this topic'
          : 'Tickets in this topic';
        ungroupedSection.appendChild(el('h3', null, ungroupedHeading));
        ungroupedSection.appendChild(
          createGroupedTicketTable(ungrouped, ungroupedHeading));
        main.appendChild(ungroupedSection);
      }
      if (groups.length === 0 && ungrouped.length === 0) {
        main.appendChild(el(
          'p',
          { class: 'muted' },
          'No tickets in this topic.'));
      }

      main.appendChild(el('p', { class: 'topic-back' }, el('a', {
        href: '#/topics' + currentHashSuffix()
      }, '← Back to topics')));
    },

    ticket: function (main, key) {
      var rows = query(
        'SELECT * FROM tickets WHERE Key = $key',
        { $key: key }).rows;
      if (rows.length === 0) {
        main.appendChild(el(
          'p',
          { class: 'error' },
          'No prepared ticket with key ' + key + '.'));
        main.appendChild(el('p', null, el('a', {
          href: '#/list' + currentHashSuffix()
        }, '← Back to list')));
        return;
      }
      var ticket = rows[0];
      setDocumentTitle(
        ticket.Title
          ? String(ticket.Key) + ': ' + truncate(String(ticket.Title), 60)
          : String(ticket.Key));
      setBreadcrumb([
        { label: 'List', href: '#/list' + currentHashSuffix() },
        { label: String(ticket.Key), href: null }
      ]);
      renderChipBanner(main);

      var people = query(
        'SELECT Role, DisplayName, Availability, UnavailableReason, ' +
        'OrderInRole FROM ticket_people ' +
        'WHERE TicketKey = $key ORDER BY Role, OrderInRole',
        { $key: ticket.Key }).rows;
      var reporter = null;
      var assignee = null;
      var requesters = [];
      for (var personIndex = 0; personIndex < people.length; personIndex++) {
        var person = people[personIndex];
        if (person.Role === 'reporter') reporter = person;
        else if (person.Role === 'assignee') assignee = person;
        else if (person.Role === 'in-person-requester' &&
                 person.DisplayName) {
          requesters.push(String(person.DisplayName));
        }
      }

      var header = el('section', { class: 'ticket-header' });
      appendViewHeading(header,
        String(ticket.Key) +
          (ticket.Title ? ' — ' + String(ticket.Title) : ''));
      var definitions = el('dl');
      appendDefinition(definitions, 'Key', components.createExternalAnchor(
        'https://jira.hl7.org/browse/' + encodeURIComponent(String(ticket.Key)),
        String(ticket.Key)));
      appendDefinition(definitions, 'Title', ticket.Title);
      appendDefinition(definitions, 'Workgroup', ticket.WorkGroup);
      appendDefinition(definitions, 'Status', ticket.Status);
      appendDefinition(definitions, 'Type', ticket.Type);
      appendDefinition(definitions, 'Priority', ticket.Priority);
      appendDefinition(definitions, 'Resolution', ticket.Resolution);
      appendDefinition(definitions, 'Specification', ticket.Specification);
      appendDefinition(definitions, 'Raised in', ticket.RaisedInVersion);
      appendDefinition(definitions, 'Selected ballot', ticket.SelectedBallot);
      appendDefinition(definitions, 'Change category', ticket.ChangeCategory);
      appendDefinition(definitions, 'Impact', ticket.Impact);
      appendDefinition(definitions, 'Comments', ticket.CommentCount);
      appendDefinition(
        definitions,
        'Reporter',
        renderPersonAvailability(reporter));
      appendDefinition(
        definitions,
        'Assignee',
        renderPersonAvailability(assignee));
      if (requesters.length > 0) {
        var requesterList = el('ul', { class: 'people-list' });
        for (var requesterIndex = 0;
          requesterIndex < requesters.length;
          requesterIndex++) {
          requesterList.appendChild(el('li', null, requesters[requesterIndex]));
        }
        appendDefinition(
          definitions,
          'In-person requesters',
          requesterList);
      } else {
        appendDefinition(definitions, 'In-person requesters',
          renderPersonAvailability({
            Availability: reporter && reporter.Availability,
            UnavailableReason: reporter && reporter.UnavailableReason
          }));
      }
      appendDefinition(definitions, 'Recommendation', ticket.Recommendation);
      appendDefinition(definitions, 'Saved', ticket.SavedAt);
      header.appendChild(definitions);
      if (hasMissingPublicName(reporter) || hasMissingPublicName(assignee) ||
          (requesters.length === 0 && reporter &&
           reporter.Availability === 'available')) {
        appendPeopleGuidance(header);
      }
      main.appendChild(header);

      if (ticket.RequestHtml) {
        main.appendChild(accordion(
          'Original request',
          htmlBlock(ticket.RequestHtml),
          true));
      } else if (ticket.RequestPlain) {
        main.appendChild(accordion(
          'Original request',
          el('pre', null, String(ticket.RequestPlain)),
          true));
      }
      if (ticket.ResolutionHtml) {
        main.appendChild(accordion(
          'Proposed / accepted resolution',
          htmlBlock(ticket.ResolutionHtml),
          true));
      } else if (ticket.ResolutionPlain) {
        main.appendChild(accordion(
          'Proposed / accepted resolution',
          el('pre', null, String(ticket.ResolutionPlain)),
          true));
      }

      var topicMemberships = query(
        'SELECT t.Id AS TopicId, t.ShortDescription AS Short ' +
        'FROM topic_members m INNER JOIN topics t ON t.RowId = m.TopicRowId ' +
        'WHERE m.TicketKey = $key ' +
        'ORDER BY t.ShortDescription COLLATE NOCASE, t.Id',
        { $key: ticket.Key }).rows;
      for (var topicIndex = 0;
        topicIndex < topicMemberships.length;
        topicIndex++) {
        var membership = topicMemberships[topicIndex];
        var topicBack = el('p', { class: 'topic-back' });
        topicBack.appendChild(document.createTextNode('Member of topic: '));
        topicBack.appendChild(el('a', {
          href: '#/topic/' +
            encodeURIComponent(String(membership.TopicId)) +
            currentHashSuffix()
        }, String(membership.Short || '')));
        main.appendChild(topicBack);
      }

      var summarySources = query(
        'SELECT s.SummaryKind, s.SourceKey, s.Label, s.Url, ' +
        'r.HydrationStatus, r.HydrationReason FROM summary_sources s ' +
        'LEFT JOIN related_items r ON r.TicketKey = s.TicketKey ' +
        "AND s.SummaryKind = 'related-zulip' AND r.Kind = 'zulip' " +
        'AND r.ItemKey = s.SourceKey ' +
        'WHERE s.TicketKey = $key ' +
        'ORDER BY s.SummaryKind, s.SortKey, s.Label, s.SourceKey',
        { $key: ticket.Key }).rows;
      var sourcesByKind = Object.create(null);
      for (var sourceIndex = 0;
        sourceIndex < summarySources.length;
        sourceIndex++) {
        var source = summarySources[sourceIndex];
        if (source.SummaryKind === 'related-zulip' &&
            !source.HydrationStatus) {
          source.HydrationStatus = 'unresolved';
          source.HydrationReason =
            'Zulip lookup outcome unavailable; source backing is not established.';
        }
        if (!sourcesByKind[source.SummaryKind]) {
          sourcesByKind[source.SummaryKind] = [];
        }
        sourcesByKind[source.SummaryKind].push(source);
      }

      var body = el('section', { class: 'ticket-body' });
      appendAuthoredSummary(
        body,
        'Request Summary',
        ticket.RequestSummary,
        []);
      appendAuthoredSummary(
        body,
        'Comment Summary',
        ticket.CommentSummary,
        []);
      appendAuthoredSummary(
        body,
        'Linked Ticket Summary',
        ticket.LinkedTicketSummary,
        sourcesByKind['linked-jira'] || []);
      appendAuthoredSummary(
        body,
        'Related Ticket Summary',
        ticket.RelatedTicketSummary,
        sourcesByKind['related-jira'] || []);
      appendAuthoredSummary(
        body,
        'Related Zulip Summary',
        ticket.RelatedZulipSummary,
        sourcesByKind['related-zulip'] || []);
      appendAuthoredSummary(
        body,
        'Related GitHub Summary',
        ticket.RelatedGitHubSummary,
        []);
      appendPlainSection(body, 'Existing Proposed', ticket.ExistingProposed);
      appendProposalSections(body, 'A', ticket.ProposalA,
        ticket.ProposalAJustification, ticket.ProposalAImpact);
      appendProposalSections(body, 'B', ticket.ProposalB,
        ticket.ProposalBJustification, ticket.ProposalBImpact);
      appendProposalSections(body, 'C', ticket.ProposalC,
        ticket.ProposalCJustification, null);
      appendPlainSection(body, 'Recommendation', ticket.Recommendation);
      appendPlainSection(
        body,
        'Recommendation — Justification',
        ticket.RecommendationJustification);
      main.appendChild(body);

      var relatedItems = query(
        'SELECT Kind, ItemKey, LinkType, Label, Url, Detail, Justification, ' +
        'HydrationStatus, HydrationReason FROM related_items ' +
        'WHERE TicketKey = $key ' +
        'ORDER BY CASE Kind ' +
        "WHEN 'repo' THEN 1 WHEN 'jira' THEN 2 WHEN 'jira-xref' THEN 3 " +
        "WHEN 'zulip' THEN 4 ELSE 5 END, SortKey, ItemKey, LinkTypeKey",
        { $key: ticket.Key }).rows;
      renderRelatedItems(main, relatedItems);

      main.appendChild(el('p', { class: 'muted' }, el('a', {
        href: '#/list' + currentHashSuffix()
      }, '← Back to list')));

      var copyRelatedItems = readCopyRelatedItems(ticket.Key);
      setCopyExport(function () {
        return serializeTicketMarkdown({
          ticket: ticket,
          topicMemberships: topicMemberships,
          repos: copyRelatedItems.repos,
          relatedJira: copyRelatedItems.relatedJira,
          jiraXrefs: copyRelatedItems.jiraXrefs,
          relatedZulip: copyRelatedItems.relatedZulip,
          relatedGitHub: copyRelatedItems.relatedGitHub
        });
      });
    },

    notFound: function (main, hash) {
      setDocumentTitle('Not found');
      main.appendChild(el(
        'p',
        { class: 'error' },
        'No view for route ' + hash + '.'));
      main.appendChild(el(
        'p',
        null,
        el('a', { href: '#/' + currentHashSuffix() }, '← Home')));
    }
  };

  function renderNoMatch(main) {
    main.appendChild(el('p', { class: 'empty-state' },
      Object.keys(routeState.filters).length > 0
        ? 'No tickets match these filters. Remove a browser filter above to ' +
          'recover; publication filters are fixed.'
        : 'This export contains no tickets.' +
          (hasActiveChips()
            ? ' Publication filters are fixed; generate a new site to ' +
              'change the exported corpus.'
            : '')));
  }

  function renderMatchSummary(main, total, includeTopics) {
    var summary = el('p', { class: 'summary-row' });
    summary.appendChild(el('span', {
      class: 'matching-ticket-count',
      'data-count': total,
      role: 'status'
    }, total + (total === 1
      ? ' matching prepared ticket.'
      : ' matching prepared tickets.')));
    var links = el('span', { class: 'summary-links' });
    links.appendChild(el('a', {
      href: '#/list' + currentHashSuffix(),
      class: 'show-ticket-list'
    }, 'Show Ticket List →'));
    if (includeTopics) {
      var topicCount = Number(query(
        'SELECT COUNT(*) AS Count FROM topics', null).rows[0].Count);
      links.appendChild(topicCount > 0
        ? el('a', {
          href: '#/topics' + currentHashSuffix(),
          class: 'show-topic-list'
        }, 'Show Topic List →')
        : el('span', {
          class: 'show-topic-list show-topic-list-disabled',
          title: 'No topics in this run.'
        }, 'Show Topic List →'));
    }
    summary.appendChild(links);
    main.appendChild(summary);
    if (total === 0) renderNoMatch(main);
  }

  function readCrosscutRows(dimension) {
    var ticketKeys = buildTicketKeysSubquery([]);
    var parameters = { $dimension: rendererDimensions[dimension] };
    mergeParameters(parameters, ticketKeys.params);
    return query(
      'SELECT f.ValueKey, MIN(f.DisplayValue) AS DisplayValue, ' +
      'MIN(f.SortKey) AS SortKey, MAX(f.IsUnknown) AS IsUnknown, ' +
      'COUNT(DISTINCT f.TicketKey) AS Count ' +
      'FROM ticket_facets f ' +
      'WHERE f.Dimension = $dimension ' +
      'AND f.TicketKey IN (' + ticketKeys.sql + ') ' +
      'GROUP BY f.ValueKey ' +
      'ORDER BY IsUnknown, SortKey, DisplayValue COLLATE NOCASE, ValueKey',
      parameters).rows;
  }

  function buildCrosscutSection(route) {
    var config = Crosscuts[route];
    var rows = readCrosscutRows(config.dimension);
    var section = el('section', {
      class: 'crosscut-card',
      'data-dimension': config.dimension
    });
    if (rows.length === 0) {
      section.appendChild(el('p', { class: 'muted' }, 'No matching choices.'));
    }

    var localState = tableState('crosscut:' + route, {
      key: 'category',
      direction: 'ascending'
    });
    var table = components.createSortableTable({
      rows: rows,
      initialSort: localState.sort,
      onSortChanged: function (sort) { localState.sort = sort; },
      className: 'crosscut-table',
      ariaLabel: config.pageTitle + ' crosscut',
      columns: [
        {
          key: 'category',
          label: config.columnLabel,
          value: function (row) { return row.DisplayValue; },
          unknownLast: true,
          isUnknown: function (row) {
            return Number(row.IsUnknown) === 1;
          },
          render: function (row) {
            var button = el('button', {
              type: 'button',
              class: 'crosscut-row',
              'data-dimension': config.dimension,
              'data-value-key': String(row.ValueKey)
            }, String(row.DisplayValue));
            button.addEventListener('click', function () {
              selectFacet(config.dimension, String(row.ValueKey));
            });
            return button;
          }
        },
        {
          key: 'count',
          label: 'Count',
          value: function (row) { return row.Count; },
          compare: 'numeric',
          className: 'count-column'
        }
      ]
    });
    section.appendChild(table.element);
    return section;
  }

  function createGroupedTicketTable(items, label) {
    var region = el('div', {
      class: 'table-overflow',
      role: 'region',
      tabindex: '0',
      'aria-label': label + ' ticket table'
    });
    var table = el('table', { class: 'grouped-ticket-table' });
    var colgroup = el('colgroup');
    colgroup.appendChild(el('col', { class: 'group-col-key' }));
    colgroup.appendChild(el('col', { class: 'group-col-title' }));
    colgroup.appendChild(el('col', { class: 'group-col-status' }));
    colgroup.appendChild(el('col', { class: 'group-col-type' }));
    table.appendChild(colgroup);
    var thead = el('thead');
    var headerRow = el('tr');
    ['Key', 'Title', 'Status', 'Type'].forEach(function (heading) {
      headerRow.appendChild(el('th', { scope: 'col' }, heading));
    });
    thead.appendChild(headerRow);
    table.appendChild(thead);
    var tbody = el('tbody');
    for (var index = 0; index < items.length; index++) {
      var item = items[index];
      var row = el('tr');
      row.appendChild(el('td', null, el('a', {
        href: '#/ticket/' + encodeURIComponent(String(item.TicketKey)) +
          currentHashSuffix()
      }, String(item.TicketKey))));
      row.appendChild(el('td', null, String(item.Title || '')));
      row.appendChild(el('td', null, displayValue(item.Status)));
      row.appendChild(el('td', null, displayValue(item.Type)));
      tbody.appendChild(row);
    }
    table.appendChild(tbody);
    region.appendChild(table);
    return region;
  }

  function renderReadiness(main) {
    var readiness = presentation.readiness;
    if (!readiness || readiness.isReady) return;
    var notice = el('section', {
      class: 'readiness-notice',
      role: 'status'
    });
    notice.appendChild(el(
      'h2',
      null,
      'Publication data is degraded'));
    var list = el('ul');
    for (var index = 0; index < readiness.reasons.length; index++) {
      var reason = readiness.reasons[index];
      list.appendChild(el(
        'li',
        null,
        String(reason.message || reason.code || 'Unavailable evidence')));
    }
    notice.appendChild(list);
    main.appendChild(notice);
  }

  function appendPeopleGuidance(parent) {
    parent.appendChild(el('p', { class: 'people-guidance muted' },
      'Populate available policy-safe display names in the Jira source, then ' +
      'refresh publication metadata and generate a new site. A missing ' +
      'Assignee display name does not establish that the ticket is unassigned.'));
  }

  function renderCorpusCoverage(main) {
    var corpus = presentation.corpusSummary;
    var total = corpus.ticketCount;
    var section = el('section', {
      class: 'corpus-coverage',
      'aria-label': 'Exported corpus coverage'
    });
    section.appendChild(el('h2', null, 'Exported corpus coverage'));
    section.appendChild(el('p', null,
      'Frozen at publication: ' + total + ' tickets across ' +
      corpus.exportedProjectCount + ' projects. Browser filters do not ' +
      'change these coverage facts or the site title.'));
    var list = el('ul');
    list.appendChild(el('li', { class: 'date-coverage' },
      'Jira update dates: ' + corpus.validJiraUpdatedAtCount + ' of ' +
      total + ' tickets (' + corpus.dateCoverage + ' coverage).' +
      (corpus.maxJiraUpdatedAt
        ? ' Latest known ticket update: ' + corpus.maxJiraUpdatedAt + '.'
        : ' No ticket update date is available.')));
    list.appendChild(el('li', { class: 'people-coverage' },
      'Public display names: Reporter ' + corpus.ticketsWithPublicReporter +
      ' of ' + total + '; Assignee ' + corpus.ticketsWithPublicAssignee +
      ' of ' + total + '; in-person requesters ' +
      corpus.ticketsWithPublicRequester + ' of ' + total + ' tickets.'));
    corpus.linksByKind.forEach(function (coverage) {
      list.appendChild(el('li', { class: 'link-coverage' },
        coverage.kind + ': ' + coverage.totalRows + ' related rows; ' +
        coverage.resolvedSafeLinks + ' resolved safe links; ' +
        coverage.unresolvedWithRetainedSafeLinks +
        ' retained safe links with unresolved lookups; ' +
        coverage.withoutUsableUrl + ' without a usable URL.'));
    });
    section.appendChild(list);
    if (corpus.ticketsWithPublicReporter < total ||
        corpus.ticketsWithPublicAssignee < total ||
        corpus.ticketsWithPublicRequester < total) {
      appendPeopleGuidance(section);
    }
    main.appendChild(section);
  }

  function readinessReasonMessage(code) {
    var reasons = presentation.readiness &&
      presentation.readiness.reasons || [];
    for (var index = 0; index < reasons.length; index++) {
      if (String(reasons[index].code) === String(code)) {
        return String(reasons[index].message || reasons[index].code);
      }
    }
    return 'Publication data is unavailable (' + String(code || 'unknown') +
      ').';
  }

  function renderPersonAvailability(person) {
    if (!person || person.Availability !== 'available') {
      return readinessReasonMessage(
        person && person.UnavailableReason);
    }
    return hasMissingPublicName(person)
      ? 'No public display name available'
      : String(person.DisplayName);
  }

  function hasMissingPublicName(person) {
    return person && person.Availability === 'available' &&
      (person.DisplayName == null || String(person.DisplayName).trim() === '');
  }

  function appendDefinition(list, label, value) {
    list.appendChild(el('dt', null, label));
    var definition = el('dd');
    if (value instanceof Node) definition.appendChild(value);
    else definition.textContent =
      value == null || String(value).trim() === '' ? '—' : String(value);
    list.appendChild(definition);
  }

  function appendAuthoredSummary(body, title, value, sources) {
    if (value == null || String(value) === '') return;
    body.appendChild(
      components.createSummarySection(
        title, String(value), sources, inCorpusJiraHref));
  }

  function appendPlainSection(body, title, value) {
    if (value == null || String(value) === '') return;
    body.appendChild(accordion(
      title,
      el('pre', null, String(value)),
      false));
  }

  function appendProposalSections(
    body,
    proposalName,
    proposal,
    justification,
    impact) {
    var label = components.proposalLabel(proposalName);
    appendPlainSection(body, label, proposal);
    appendPlainSection(
      body,
      label + ' — Justification',
      justification);
    appendPlainSection(
      body,
      label + ' — Impact',
      impact);
  }

  function renderRelatedItems(main, rows) {
    var sidebar = el('section', { class: 'related-sidebar' });
    sidebar.appendChild(el('h2', null, 'Related items'));
    var groups = [
      { kind: 'repo', label: 'Repos' },
      { kind: 'jira', label: 'Related Jira tickets' },
      { kind: 'jira-xref', label: 'Other Jira-declared links' },
      { kind: 'zulip', label: 'Related Zulip threads' },
      { kind: 'github', label: 'Related GitHub items' }
    ];
    for (var groupIndex = 0; groupIndex < groups.length; groupIndex++) {
      var group = groups[groupIndex];
      var items = rows.filter(function (row) {
        return row.Kind === group.kind;
      });
      if (items.length === 0) continue;
      sidebar.appendChild(el('h3', null, group.label));
      var list = el('ul');
      for (var index = 0; index < items.length; index++) {
        list.appendChild(renderRelatedItem(items[index]));
      }
      sidebar.appendChild(list);
    }
    main.appendChild(sidebar);
  }

  function renderRelatedItem(item) {
    var listItem = el('li', {
      'data-kind': item.Kind,
      'data-item-key': item.ItemKey
    });
    var label = String(item.Label || item.ItemKey || '');
    var internalHref = (item.Kind === 'jira' || item.Kind === 'jira-xref') &&
      inCorpusJiraHref(item.ItemKey);
    var link = internalHref
      ? el('a', { href: internalHref }, label)
      : components.createExternalAnchor(item.Url, label);
    listItem.appendChild(link || document.createTextNode(label));
    if (item.LinkType) {
      listItem.appendChild(document.createTextNode(
        ' (' + String(item.LinkType) + ')'));
    }
    if (item.Detail) {
      listItem.appendChild(document.createTextNode(
        ' · ' + String(item.Detail)));
    }
    components.appendLinkOutcome(listItem, item, Boolean(link));
    if (item.Justification) {
      listItem.appendChild(document.createElement('br'));
      listItem.appendChild(el(
        'span',
        { class: 'muted' },
        String(item.Justification)));
    }
    return listItem;
  }

  function inCorpusJiraHref(key) {
    return inRunKeys[String(key).toLowerCase()]
      ? '#/ticket/' + encodeURIComponent(String(key)) + currentHashSuffix()
      : null;
  }

  function query(sql, params) {
    var statement = db.prepare(sql);
    try {
      if (params && Object.keys(params).length > 0) statement.bind(params);
      var rows = [];
      while (statement.step()) rows.push(statement.getAsObject());
      return { columns: statement.getColumnNames(), rows: rows };
    } finally {
      statement.free();
    }
  }

  function clearChildren(element) {
    while (element.firstChild) element.removeChild(element.firstChild);
  }

  function el(tag, attributes, children) {
    var node = document.createElement(tag);
    if (attributes) {
      for (var key in attributes) {
        if (key === 'class') node.className = attributes[key];
        else if (key === 'id') node.id = attributes[key];
        else node.setAttribute(key, attributes[key]);
      }
    }
    if (children != null) {
      if (Array.isArray(children)) {
        for (var index = 0; index < children.length; index++) {
          appendChild(node, children[index]);
        }
      } else {
        appendChild(node, children);
      }
    }
    return node;
  }

  function appendChild(parent, child) {
    if (child == null) return;
    if (child instanceof Node) parent.appendChild(child);
    else parent.appendChild(document.createTextNode(String(child)));
  }

  function renderError(main, message) {
    clearChildren(main);
    main.appendChild(el('p', { class: 'error' }, message));
  }

  function htmlBlock(rawHtml) {
    var block = el('div', { class: 'md' });
    if (rawHtml == null || rawHtml === '') return block;
    if (typeof DOMPurify === 'undefined') {
      block.textContent = String(rawHtml);
      return block;
    }
    block.innerHTML = DOMPurify.sanitize(String(rawHtml));
    return block;
  }

  function accordion(label, bodyNode, open) {
    var details = el(
      'details',
      open ? { class: 'accordion', open: '' } : { class: 'accordion' });
    details.appendChild(el('summary', null, el('h3', null, label)));
    details.appendChild(el(
      'div',
      { class: 'accordion-body' },
      bodyNode));
    return details;
  }

  function setBreadcrumb(tail) {
    var breadcrumb = document.getElementById('breadcrumb');
    if (!breadcrumb) return;
    clearChildren(breadcrumb);
    var hasTail = Array.isArray(tail) && tail.length > 0;
    var parts = [
      { label: 'Chooser', href: '../index.html' },
      {
        label: presentation.siteName,
        href: hasTail ? '#/' + currentHashSuffix() : null
      }
    ];
    if (hasTail) {
      for (var index = 0; index < tail.length; index++) {
        parts.push(tail[index]);
      }
    }
    for (var partIndex = 0; partIndex < parts.length; partIndex++) {
      if (partIndex > 0) {
        breadcrumb.appendChild(document.createTextNode(' › '));
      }
      var part = parts[partIndex];
      breadcrumb.appendChild(
        part.href
          ? el('a', { href: part.href }, part.label)
          : document.createTextNode(part.label));
    }
  }

  function setDocumentTitle(subject) {
    document.title = subject
      ? subject + ' — ' + presentation.siteName
      : presentation.siteName;
  }

  function displayValue(value) {
    return value == null || String(value).trim() === ''
      ? '(unknown)'
      : String(value);
  }

  function truncate(value, maximumLength) {
    if (!value || value.length <= maximumLength) return value;
    return value.slice(0, maximumLength - 1) + '…';
  }

  function installCopyButton() {
    if (document.querySelector('.copy-ai')) return;
    var header = document.querySelector('header');
    if (!header) return;
    var button = el('button', {
      type: 'button',
      class: 'copy-ai',
      hidden: ''
    }, '📋 Copy for AI');
    var status = el('span', {
      class: 'copy-ai-status',
      role: 'status'
    });
    button.addEventListener('click', function () {
      try {
        var markdown = currentExport && currentExport();
        if (markdown) copyForAi(markdown);
        else setCopyStatus('Nothing to copy');
      } catch (error) {
        setCopyStatus('Copy failed');
      }
    });
    header.appendChild(button);
    header.appendChild(status);
  }

  function setCopyStatus(message) {
    var status = document.querySelector('.copy-ai-status');
    if (status) status.textContent = message;
  }

  function setCopyExport(exporter) {
    currentExport = exporter;
    var button = document.querySelector('.copy-ai');
    if (button) button.hidden = false;
    setCopyStatus('');
  }

  function clearCopyExport() {
    currentExport = null;
    var button = document.querySelector('.copy-ai');
    if (button) button.hidden = true;
    setCopyStatus('');
  }

  function copyForAi(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(
        function () { setCopyStatus('Copied!'); },
        function () {
          setCopyStatus(
            copyViaTextarea(text) ? 'Copied!' : 'Copy failed');
        });
    } else {
      setCopyStatus(copyViaTextarea(text) ? 'Copied!' : 'Copy failed');
    }
  }

  function copyViaTextarea(text) {
    var textarea = document.createElement('textarea');
    textarea.value = text;
    textarea.style.position = 'fixed';
    textarea.style.left = '-9999px';
    textarea.style.top = '0';
    textarea.style.opacity = '0';
    document.body.appendChild(textarea);
    textarea.focus();
    textarea.select();
    if (textarea.setSelectionRange) {
      textarea.setSelectionRange(0, text.length);
    }
    var copied = false;
    try {
      copied = document.execCommand('copy');
    } catch (error) {
      copied = false;
    }
    document.body.removeChild(textarea);
    return copied;
  }

  function markdownCell(value) {
    return value == null
      ? ''
      : String(value).replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');
  }

  function markdownTable(headers, rows) {
    var output = '| ' + headers.map(markdownCell).join(' | ') + ' |\n';
    output += '| ' + headers.map(function () {
      return '---';
    }).join(' | ') + ' |\n';
    for (var index = 0; index < rows.length; index++) {
      output += '| ' + rows[index].map(markdownCell).join(' | ') + ' |\n';
    }
    return output;
  }

  function markdownProseSection(title, value) {
    if (value == null || String(value) === '') return '';
    return '## ' + title + '\n\n' + String(value) + '\n\n';
  }

  function readCopyRelatedItems(ticketKey) {
    var parameters = { $key: ticketKey };
    var columns =
      'SELECT ItemKey, LinkType, Label, Detail, Justification, ' +
      'HydrationStatus, HydrationReason FROM related_items ';
    return {
      repos: query(
        columns +
        "WHERE TicketKey = $key AND Kind = 'repo' " +
        'ORDER BY ItemKey, LinkTypeKey',
        parameters).rows,
      relatedJira: query(
        columns +
        "WHERE TicketKey = $key AND Kind = 'jira' " +
        'ORDER BY ItemKey, LinkTypeKey',
        parameters).rows,
      jiraXrefs: query(
        columns +
        "WHERE TicketKey = $key AND Kind = 'jira-xref' " +
        'ORDER BY LinkType, ItemKey',
        parameters).rows,
      relatedZulip: query(
        columns +
        "WHERE TicketKey = $key AND Kind = 'zulip' " +
        'ORDER BY ItemKey',
        parameters).rows,
      relatedGitHub: query(
        columns +
        "WHERE TicketKey = $key AND Kind = 'github' " +
        'ORDER BY ItemKey',
        parameters).rows
    };
  }

  function serializeTicketMarkdown(context) {
    var ticket = context.ticket || {};
    var output = '# ' + String(ticket.Key || '') +
      (ticket.Title ? ' — ' + String(ticket.Title) : '') + '\n\n';
    output += markdownTable(['Field', 'Value'], [
      ['Key', ticket.Key],
      ['Title', ticket.Title],
      ['Workgroup', ticket.WorkGroup],
      ['Status', ticket.Status],
      ['Type', ticket.Type],
      ['Priority', ticket.Priority],
      ['Resolution', ticket.Resolution],
      ['Specification', ticket.Specification],
      ['Raised in', ticket.RaisedInVersion],
      ['Selected ballot', ticket.SelectedBallot],
      ['Change category', ticket.ChangeCategory],
      ['Impact', ticket.Impact],
      ['Comments', ticket.CommentCount],
      ['Recommendation', ticket.Recommendation],
      ['Saved', ticket.SavedAt]
    ]) + '\n';

    var memberships = context.topicMemberships || [];
    for (var membershipIndex = 0;
      membershipIndex < memberships.length;
      membershipIndex++) {
      output += 'Member of topic: ' +
        String(memberships[membershipIndex].Short || '') + '\n';
    }
    if (memberships.length > 0) output += '\n';

    output += markdownProseSection(
      'Original request',
      ticket.RequestPlain);
    output += markdownProseSection(
      'Proposed / accepted resolution',
      ticket.ResolutionPlain);
    [
      ['Request Summary', ticket.RequestSummary],
      ['Comment Summary', ticket.CommentSummary],
      ['Linked Ticket Summary', ticket.LinkedTicketSummary],
      ['Related Ticket Summary', ticket.RelatedTicketSummary],
      ['Related Zulip Summary', ticket.RelatedZulipSummary],
      ['Related GitHub Summary', ticket.RelatedGitHubSummary],
      ['Existing Proposed', ticket.ExistingProposed]
    ].forEach(function (section) {
      output += markdownProseSection(section[0], section[1]);
    });

    if (ticket.ProposalA ||
        ticket.ProposalAJustification ||
        ticket.ProposalAImpact) {
      output += markdownProseSection(
        components.proposalLabel('A'), ticket.ProposalA);
      output += markdownProseSection(
        components.proposalLabel('A') + ' — Justification',
        ticket.ProposalAJustification);
      output += markdownProseSection(
        components.proposalLabel('A') + ' — Impact',
        ticket.ProposalAImpact);
    }
    if (ticket.ProposalB ||
        ticket.ProposalBJustification ||
        ticket.ProposalBImpact) {
      output += markdownProseSection(
        components.proposalLabel('B'), ticket.ProposalB);
      output += markdownProseSection(
        components.proposalLabel('B') + ' — Justification',
        ticket.ProposalBJustification);
      output += markdownProseSection(
        components.proposalLabel('B') + ' — Impact',
        ticket.ProposalBImpact);
    }
    if (ticket.ProposalC || ticket.ProposalCJustification) {
      output += markdownProseSection(
        components.proposalLabel('C'), ticket.ProposalC);
      output += markdownProseSection(
        components.proposalLabel('C') + ' — Justification',
        ticket.ProposalCJustification);
    }
    if (ticket.Recommendation || ticket.RecommendationJustification) {
      output += markdownProseSection(
        'Recommendation',
        ticket.Recommendation);
      output += markdownProseSection(
        'Recommendation — Justification',
        ticket.RecommendationJustification);
    }

    output += '## Related items\n\n';
    output += serializeRepoItemsMarkdown(context.repos || []);
    output += serializeJiraItemsMarkdown(context.relatedJira || []);
    output += serializeJiraXrefItemsMarkdown(context.jiraXrefs || []);
    output += serializeZulipItemsMarkdown(context.relatedZulip || []);
    output += serializeGitHubItemsMarkdown(context.relatedGitHub || []);
    return output;
  }

  function serializeRepoItemsMarkdown(items) {
    return serializeRequiredRelatedItemsMarkdown(
      'Repos',
      ['Repo', 'Category', 'Detail', 'Justification'],
      items,
      function (item) {
        return [
          String(item.ItemKey || ''),
          String(item.LinkType || ''),
          copyHydrationDetail(item),
          String(item.Justification || '')
        ];
      });
  }

  function serializeJiraItemsMarkdown(items) {
    return serializeRequiredRelatedItemsMarkdown(
      'Related Jira tickets',
      ['Key', 'Link type', 'Detail', 'Justification'],
      items,
      function (item) {
        return [
          String(item.ItemKey || ''),
          String(item.LinkType || ''),
          copyHydrationDetail(item),
          String(item.Justification || '')
        ];
      });
  }

  function serializeJiraXrefItemsMarkdown(items) {
    if (items.length === 0) return '';
    return '### Other Jira-declared links (' + items.length + ')\n\n' +
      markdownTable(
        ['Source', 'Key', 'Detail'],
        items.map(function (item) {
          return [
            String(item.LinkType || ''),
            String(item.ItemKey || ''),
            copyHydrationDetail(item)
          ];
        })) + '\n';
  }

  function serializeZulipItemsMarkdown(items) {
    return serializeRequiredRelatedItemsMarkdown(
      'Related Zulip threads',
      ['Thread', 'Detail', 'Justification'],
      items,
      function (item) {
        return [
          String(item.ItemKey || ''),
          copyZulipDetail(item),
          String(item.Justification || '')
        ];
      });
  }

  function serializeGitHubItemsMarkdown(items) {
    return serializeRequiredRelatedItemsMarkdown(
      'Related GitHub items',
      ['Item', 'Detail', 'Justification'],
      items,
      function (item) {
        return [
          String(item.ItemKey || ''),
          copyHydrationDetail(item),
          String(item.Justification || '')
        ];
      });
  }

  function serializeRequiredRelatedItemsMarkdown(
    heading,
    headers,
    items,
    projectRow) {
    var output = '### ' + heading + ' (' + items.length + ')\n\n';
    if (items.length === 0) return output + '_None._\n\n';
    return output + markdownTable(headers, items.map(projectRow)) + '\n';
  }

  function copyHydrationDetail(item) {
    if (!item) return '';
    if (item.HydrationStatus === 'unresolved') {
      return (item.Detail ? String(item.Detail) + ' · ' : '') +
        'unresolved: ' + String(item.HydrationReason || '');
    }
    if (item.HydrationStatus !== 'resolved') return '';
    return [item.Detail, item.HydrationReason].filter(function (value) {
      return value != null && String(value) !== '';
    }).map(String).join(' · ');
  }

  function copyZulipDetail(item) {
    var detail = copyHydrationDetail(item);
    if (!item || item.Label == null) return detail;

    var label = String(item.Label).trim();
    var itemKey = String(item.ItemKey || '').trim();
    if (!label ||
        label.toLowerCase() === itemKey.toLowerCase()) {
      return detail;
    }
    if (!detail) return label;

    var normalizedLabel = label.toLowerCase();
    var normalizedDetail = detail.toLowerCase();
    if (normalizedDetail === normalizedLabel ||
        normalizedDetail.indexOf(normalizedLabel + ' · ') === 0) {
      return detail;
    }
    return label + ' · ' + detail;
  }

  function exposeRuntimeTestHook() {
    if (typeof window.__DISCUSSION_TEST_HOOK__ !== 'function') return;
    window.__DISCUSSION_TEST_HOOK__(Object.freeze({
      query: query,
      readCrosscutRows: readCrosscutRows,
      visibleListFacetDimensions: visibleListFacetDimensions,
      renderPersonAvailability: renderPersonAvailability,
      readTicketListRows: function () {
        return readTicketListRows(buildTicketKeysSubquery([]));
      },
      ticketListColumnKeys: function () {
        return createTicketListColumns().map(function (column) {
          return column.key;
        });
      }
    }));
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', App.init);
  } else {
    App.init();
  }
})();
