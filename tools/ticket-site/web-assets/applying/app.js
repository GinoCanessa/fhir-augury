// Phase 5 placeholder for the applying sub-site. Phase 6 will replace
// this with the real planner SPA (landing crosscut + #/list + #/ticket
// + #/topics + #/topic views). For now we just confirm sql.js loaded and
// emit a one-line summary using the inlined planner DB.
(async function () {
  const app = document.getElementById('app');
  if (!app) return;

  try {
    const SQL = await initSqlJs({ locateFile: file => 'assets/' + file });
    const b64 = window.__DB__ || '';
    if (!b64) {
      app.textContent = 'No database inlined.';
      return;
    }
    const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
    const db = new SQL.Database(bytes);
    const row = db.exec('SELECT count(*) FROM planned_tickets');
    const count = row[0]?.values?.[0]?.[0] ?? 0;
    app.innerHTML = '<p>Applying sub-site placeholder. Planned tickets in DB: <strong>' + count + '</strong>.</p>';
  } catch (e) {
    app.textContent = 'Failed to load planner DB: ' + e.message;
  }
})();
