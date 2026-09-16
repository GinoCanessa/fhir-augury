(function () {
  'use strict';

  function contains(values, candidate) {
    var normalized = String(candidate).toLowerCase();
    return values.some(function (value) {
      return String(value).toLowerCase() === normalized;
    });
  }

  function mergeFilters(left, right) {
    var result = Object.create(null);
    [left, right].forEach(function (filters) {
      Object.keys(filters || {}).forEach(function (dimension) {
        var values = result[dimension] || [];
        (filters[dimension] || []).forEach(function (value) {
          if (!contains(values, value)) values.push(value);
        });
        if (values.length) result[dimension] = values;
      });
    });
    Object.keys(result).forEach(function (dimension) {
      Object.freeze(result[dimension]);
    });
    return Object.freeze(result);
  }

  function create(route, filters, fixedFilters, exportedProjectCount) {
    var fixed = mergeFilters(fixedFilters);
    var removable = Object.create(null);
    Object.keys(filters || {}).forEach(function (dimension) {
      removable[dimension] = (filters[dimension] || []).filter(function (value) {
        return !contains(fixed[dimension] || [], value);
      });
    });
    return Object.freeze({
      route: route,
      filters: mergeFilters(removable),
      fixedFilters: fixed,
      exportedProjectCount: exportedProjectCount
    });
  }

  function activeFilters(state) {
    return mergeFilters(state.fixedFilters, state.filters);
  }

  function select(state, dimension, valueKey) {
    var selection = Object.create(null);
    selection[dimension] = [valueKey];
    return create(
      state.route,
      mergeFilters(state.filters, selection),
      state.fixedFilters,
      state.exportedProjectCount);
  }

  function remove(state, dimension, valueKey) {
    var filters = Object.create(null);
    Object.keys(state.filters).forEach(function (key) {
      filters[key] = state.filters[key].filter(function (value) {
        return key !== dimension || !contains([valueKey], value);
      });
    });
    return create(
      state.route, filters, state.fixedFilters, state.exportedProjectCount);
  }

  function navigate(state, route) {
    return create(
      route, state.filters, state.fixedFilters, state.exportedProjectCount);
  }

  function isDimensionVisible(state, dimension) {
    return !(dimension === 'project' && state.exportedProjectCount === 1) &&
      !(state.filters[dimension] || []).length &&
      !(state.fixedFilters[dimension] || []).length;
  }

  function parseHash(hash) {
    var stripped = String(hash || '#/').replace(/^#\/?/, '');
    var queryIndex = stripped.indexOf('?');
    return {
      route: queryIndex >= 0 ? stripped.slice(0, queryIndex) : stripped,
      query: queryIndex >= 0 ? stripped.slice(queryIndex + 1) : ''
    };
  }

  function querySuffix(filters, dimensions) {
    var parameters = new URLSearchParams();
    dimensions.forEach(function (dimension) {
      (filters[dimension] || []).forEach(function (value) {
        parameters.append(dimension + 'Key', value);
      });
    });
    var query = parameters.toString();
    return query ? '?' + query : '';
  }

  function toHash(state, dimensions) {
    return '#/' + state.route + querySuffix(state.filters, dimensions);
  }

  window.DiscussionState = Object.freeze({
    create: create,
    activeFilters: activeFilters,
    select: select,
    remove: remove,
    navigate: navigate,
    isDimensionVisible: isDimensionVisible,
    parseHash: parseHash,
    querySuffix: querySuffix,
    toHash: toHash
  });
})();
