(function () {
  'use strict';

  var textCollator = new Intl.Collator('en', {
    sensitivity: 'base',
    usage: 'sort'
  });
  var numericCollator = new Intl.Collator('en', {
    numeric: true,
    sensitivity: 'base',
    usage: 'sort'
  });

  function asText(value) {
    return value == null ? '' : String(value);
  }

  function compareText(left, right) {
    return textCollator.compare(asText(left), asText(right));
  }

  function compareNumeric(left, right) {
    var leftNumber = Number(left);
    var rightNumber = Number(right);
    var leftFinite = Number.isFinite(leftNumber);
    var rightFinite = Number.isFinite(rightNumber);
    if (leftFinite && rightFinite) return leftNumber - rightNumber;
    if (leftFinite) return -1;
    if (rightFinite) return 1;
    return compareText(left, right);
  }

  function compareNatural(left, right) {
    return numericCollator.compare(asText(left), asText(right));
  }

  function appendContent(parent, content) {
    if (content == null) return;
    if (content instanceof Node) {
      parent.appendChild(content);
      return;
    }
    parent.appendChild(document.createTextNode(String(content)));
  }

  function createSortableTable(options) {
    var columns = options.columns || [];
    var sourceRows = (options.rows || []).map(function (row, index) {
      return { row: row, originalIndex: index };
    });
    var sortKey = options.initialSort ? options.initialSort.key : null;
    var sortDirection = options.initialSort
      ? options.initialSort.direction || 'ascending'
      : 'ascending';

    var table = document.createElement('table');
    if (options.className) table.className = options.className;
    if (options.ariaLabel) table.setAttribute('aria-label', options.ariaLabel);

    var thead = document.createElement('thead');
    var headerRow = document.createElement('tr');
    var headers = [];
    for (var index = 0; index < columns.length; index++) {
      var column = columns[index];
      var th = document.createElement('th');
      th.scope = 'col';
      th.className = 'sortable' +
        (column.className ? ' ' + column.className : '');
      th.setAttribute('aria-sort', 'none');

      var button = document.createElement('button');
      button.type = 'button';
      button.className = 'sort-button';
      button.appendChild(document.createTextNode(column.label));
      var indicator = document.createElement('span');
      indicator.className = 'sort-indicator';
      indicator.setAttribute('aria-hidden', 'true');
      button.appendChild(indicator);

      (function (capturedColumn) {
        button.addEventListener('click', function () {
          if (sortKey === capturedColumn.key) {
            sortDirection = sortDirection === 'ascending'
              ? 'descending'
              : 'ascending';
          } else {
            sortKey = capturedColumn.key;
            sortDirection = 'ascending';
          }
          render();
        });
      })(column);

      th.appendChild(button);
      headerRow.appendChild(th);
      headers.push({ column: column, th: th, button: button, indicator: indicator });
    }
    thead.appendChild(headerRow);
    table.appendChild(thead);

    var tbody = document.createElement('tbody');
    table.appendChild(tbody);

    function columnComparator(column, left, right) {
      var leftValue = column.value(left.row);
      var rightValue = column.value(right.row);
      if (column.unknownLast) {
        var leftUnknown = Boolean(column.isUnknown && column.isUnknown(left.row));
        var rightUnknown = Boolean(column.isUnknown && column.isUnknown(right.row));
        if (leftUnknown !== rightUnknown) return leftUnknown ? 1 : -1;
      }

      var result;
      if (typeof column.compare === 'function') {
        result = column.compare(leftValue, rightValue, left.row, right.row);
      } else if (column.compare === 'numeric') {
        result = compareNumeric(leftValue, rightValue);
      } else if (column.compare === 'natural') {
        result = compareNatural(leftValue, rightValue);
      } else {
        result = compareText(leftValue, rightValue);
      }
      if (result !== 0) {
        return sortDirection === 'descending' ? -result : result;
      }
      return left.originalIndex - right.originalIndex;
    }

    function updateHeaders() {
      for (var index = 0; index < headers.length; index++) {
        var header = headers[index];
        var active = header.column.key === sortKey;
        header.th.setAttribute('aria-sort', active ? sortDirection : 'none');
        header.indicator.textContent = active
          ? (sortDirection === 'ascending' ? ' ▲' : ' ▼')
          : '';
        header.button.setAttribute(
          'aria-label',
          active
            ? 'Sort by ' + header.column.label + ', currently ' + sortDirection
            : 'Sort by ' + header.column.label);
      }
    }

    function render() {
      while (tbody.firstChild) tbody.removeChild(tbody.firstChild);
      var renderedRows = sourceRows.slice();
      if (sortKey != null) {
        var activeColumn = null;
        for (var index = 0; index < columns.length; index++) {
          if (columns[index].key === sortKey) {
            activeColumn = columns[index];
            break;
          }
        }
        if (activeColumn) {
          renderedRows.sort(function (left, right) {
            return columnComparator(activeColumn, left, right);
          });
        }
      }

      for (var rowIndex = 0; rowIndex < renderedRows.length; rowIndex++) {
        var row = renderedRows[rowIndex].row;
        var tr = document.createElement('tr');
        for (var columnIndex = 0; columnIndex < columns.length; columnIndex++) {
          var column = columns[columnIndex];
          var td = document.createElement('td');
          if (column.className) td.className = column.className;
          appendContent(
            td,
            column.render
              ? column.render(row)
              : column.value(row));
          tr.appendChild(td);
        }
        tbody.appendChild(tr);
      }
      updateHeaders();
    }

    function setRows(rows) {
      sourceRows = (rows || []).map(function (row, index) {
        return { row: row, originalIndex: index };
      });
      render();
    }

    render();
    return {
      element: table,
      setRows: setRows
    };
  }

  function isJiraKeyCharacter(character) {
    return Boolean(character && /[A-Za-z0-9_-]/.test(character));
  }

  function appendJiraLinkedText(parent, value) {
    var text = asText(value);
    var pattern = /[A-Za-z][A-Za-z0-9]*-[0-9]+/g;
    var cursor = 0;
    var match;
    while ((match = pattern.exec(text)) !== null) {
      var start = match.index;
      var end = start + match[0].length;
      if (isJiraKeyCharacter(text[start - 1]) ||
          isJiraKeyCharacter(text[end])) {
        continue;
      }
      if (start > cursor) {
        parent.appendChild(document.createTextNode(text.slice(cursor, start)));
      }
      var anchor = document.createElement('a');
      var canonicalKey = match[0].toUpperCase();
      anchor.href = 'https://jira.hl7.org/browse/' +
        encodeURIComponent(canonicalKey);
      anchor.target = '_blank';
      anchor.rel = 'noopener noreferrer';
      anchor.textContent = match[0];
      parent.appendChild(anchor);
      cursor = end;
    }
    if (cursor < text.length) {
      parent.appendChild(document.createTextNode(text.slice(cursor)));
    }
  }

  function appendSummarySources(parent, sources) {
    if (!sources || sources.length === 0) return;
    var list = document.createElement('ul');
    list.className = 'summary-source-list';
    for (var index = 0; index < sources.length; index++) {
      var source = sources[index];
      var item = document.createElement('li');
      if (source.Url) {
        var link = document.createElement('a');
        link.href = String(source.Url);
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.textContent = String(source.Label || source.SourceKey || '');
        item.appendChild(link);
      } else {
        item.appendChild(document.createTextNode(
          String(source.Label || source.SourceKey || '') +
          ' — URL unavailable'));
      }
      list.appendChild(item);
    }
    parent.appendChild(list);
  }

  function createSummarySection(title, value, sources) {
    var details = document.createElement('details');
    details.className = 'accordion';
    var summary = document.createElement('summary');
    var heading = document.createElement('h3');
    heading.textContent = title;
    summary.appendChild(heading);
    details.appendChild(summary);

    var body = document.createElement('div');
    body.className = 'accordion-body';
    var prose = document.createElement('pre');
    prose.className = 'summary-prose';
    appendJiraLinkedText(prose, value);
    body.appendChild(prose);
    appendSummarySources(body, sources);
    details.appendChild(body);
    return details;
  }

  window.DiscussionComponents = Object.freeze({
    createSortableTable: createSortableTable,
    createSummarySection: createSummarySection,
    appendJiraLinkedText: appendJiraLinkedText
  });
})();
