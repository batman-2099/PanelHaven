// Run in the browser console on an idle Library page as an administrator.
// Requires at least one missing-issue report row. Does not write catalog data.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (value, message) => { if (!value) throw new Error(message); };
  const waitFor = async predicate => {
    const deadline = Date.now() + 15000;
    while (!predicate()) {
      if (Date.now() > deadline) throw new Error("Report navigation timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  assert(!$('library-view').hidden && !document.querySelector('dialog[open]'), "Start on an idle Library page.");
  const response = await fetch('/api/maintenance?kind=missing-issues');
  assert(response.ok, "Administrator access is required.");
  const report = await response.json();
  assert(report.items.length > 0, "A missing-issue report row is required.");
  const originalFetch = window.fetch;
  let catalogQuery;
  window.fetch = (input, options) => {
    const url = new URL(input instanceof Request ? input.url : input, location.href);
    if (url.pathname === '/api/books') catalogQuery = url.searchParams;
    return originalFetch(input, options);
  };
  try {
  const hash = $('library-location').value === 'unsorted' ? '#unsorted' : '#library';
  if (location.hash !== hash) location.hash = hash;
  await waitFor(() => $('books').getAttribute('aria-busy') !== 'true' && history.state?.catalog?.hash === hash);
  for (const entry of report.items.filter((row, index, rows) => rows.findIndex(other => other.location === row.location) === index)) {
    $('clear-filters').click();
    await waitFor(() => $('books').getAttribute('aria-busy') !== 'true');
    $('edit-advanced-filters').click();
    await waitFor(() => !$('smart-list-controls').disabled);
    const rule = $('list-rules').querySelector('.list-rule');
    for (const [selector, value] of [['.rule-field', 'PageCount'], ['.rule-operator', 'greaterThan'], ['.rule-value', '1000000']]) {
      const control = rule.querySelector(selector);
      control.value = value;
      control.dispatchEvent(new Event('change', { bubbles: true }));
    }
    $('list-form').requestSubmit();
    await waitFor(() => {
      const rules = history.state?.catalog?.advancedFilter?.rules;
      return !$('list-dialog').open && $('books').getAttribute('aria-busy') !== 'true' &&
        rules?.length === 1 && rules[0].field === 'PageCount' && rules[0].operator === 'greaterThan' &&
        rules[0].value === '1000000';
    });
    assert(history.state?.catalog?.advancedFilter && !$('books').querySelector('.book-selection'),
      "An applied restrictive advanced filter is required before report navigation.");
    const previousRows = $('maintenance-items').firstElementChild;
    $('maintenance').click();
    $('maintenance-kind').value = 'missing-issues';
    $('maintenance-kind').dispatchEvent(new Event('change', { bubbles: true }));
    await waitFor(() => !previousRows?.isConnected &&
      [...$('maintenance-items').children].some(row => row.querySelector('h3')?.textContent === entry.series
        && row.querySelector('p.muted')?.textContent.toLowerCase() === entry.location));
    const row = [...$('maintenance-items').children].find(row => row.querySelector('h3')?.textContent === entry.series
      && row.querySelector('p.muted')?.textContent.toLowerCase() === entry.location);
    assert(row, "Expected report row is absent.");
    $('search').value = 'must not constrain report navigation';
    $('book-status').value = 'completed';
    $('book-sort').value = 'title';
    $('group-by').value = 'publisher';
    $('stack-by').value = 'folder';
    const previousCatalog = history.state?.catalog;
    const view = row.querySelector('button');
    view.click();
    await waitFor(() => !view.disabled && !$('library-view').hidden && !$('maintenance-dialog').open &&
      $('books').getAttribute('aria-busy') !== 'true' && history.state?.catalog &&
      history.state.catalog !== previousCatalog && history.state.catalog.groupKey === entry.series &&
      history.state.catalog.location === entry.location);
    assert($('library-location').value === entry.location && $('group-label').textContent === entry.series
      && !$('group-navigation').hidden, "Report opened the wrong series or location.");
    assert($('search').value === '' && $('book-status').value === 'all' && $('book-sort').value === 'number'
      && $('reading-list').value === '' && $('sort-direction').value === 'asc'
      && $('group-by').value === 'none' && $('stack-by').value === 'series', "Old filters hid the report series.");
    assert(catalogQuery && !catalogQuery.has('filter') && !history.state?.catalog?.advancedFilter,
      "Report navigation retained the advanced rule in its request or saved catalog state.");
    assert($('books').querySelector('.book-selection') && document.activeElement === $('library-status'),
      "Series results or navigation focus are missing.");
  }
  } finally { window.fetch = originalFetch; }
  console.log('PASS: report buttons open exact series/locations, clear conflicting filters, and focus results.');
})();
