// Run in the browser console on an idle Library root with 1–96 fixture books.
// REQUIRED: window.panelHavenAdvancedFilterDisposable = true; use a disposable account/data directory.
// Saves one uniquely named smart list through the real UI. No book/source edits. Transient failures are simulated.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const waitFor = async predicate => {
    const deadline = Date.now() + 15000;
    while (!predicate()) {
      if (Date.now() > deadline) throw new Error('Advanced filter check timed out.');
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const idle = () => {
    const catalog = history.state?.catalog;
    return $('books').getAttribute('aria-busy') !== 'true' && catalog?.hash === location.hash &&
      ['page-size', 'group-by', 'stack-by', 'book-rating', 'book-status'].every(id => catalog.filters[id] === $(id).value);
  };
  const tick = () => new Promise(resolve => setTimeout(resolve, 0));
  const change = (control, value) => {
    control.value = value;
    control.dispatchEvent(new Event('change', { bubbles: true }));
  };
  const open = async () => {
    $('edit-advanced-filters').focus();
    $('edit-advanced-filters').click();
    await waitFor(() => !$('smart-list-controls').disabled);
  };
  const rule = (field, operator, value, row = $('list-rules').querySelector('.list-rule')) => {
    change(row.querySelector('.rule-field'), field);
    change(row.querySelector('.rule-operator'), operator);
    if (value !== undefined) change(row.querySelector('.rule-value'), value);
    return row;
  };
  const apply = async () => {
    $('list-form').requestSubmit();
    await waitFor(() => !$('list-dialog').open && idle() && history.state.catalog.advancedFilter);
  };
  const clear = async () => {
    $('clear-filters').click();
    await waitFor(() => idle() && !history.state.catalog.advancedFilter);
  };
  const originalFetch = window.fetch;
  const originalConfirm = window.confirm;
  const requests = [];
  let failFields = false, failPreview = false, failSave = false;
  assert(window.panelHavenAdvancedFilterDisposable === true, 'Set panelHavenAdvancedFilterDisposable=true only in a disposable fixture; this check saves a real smart list.');
  assert(!$('library-view').hidden && $('library-location').value === 'library' && $('group-navigation').hidden && !document.querySelector('dialog[open]'), 'Start at an idle Library root.');
  window.fetch = async (input, options) => {
    const url = String(input);
    requests.push({ url, method: options?.method || 'GET', body: options?.body });
    if ((failFields && url === '/api/list-rule-fields') || (failPreview && url.startsWith('/api/lists/preview')) || (failSave && url === '/api/lists' && options?.method === 'POST'))
      return new Response(JSON.stringify({ error: 'Simulated advanced-filter retry failure' }), { status: 503, headers: { 'Content-Type': 'application/json' } });
    return originalFetch.call(window, input, options);
  };
  try {
    if (location.hash !== '#library') location.hash = '#library';
    await waitFor(idle);
    await clear();
    change($('page-size'), '96'); await waitFor(idle);
    change($('group-by'), 'none'); await waitFor(idle);
    change($('stack-by'), 'none'); await waitFor(idle);
    const baselineResponse = await originalFetch('/api/books?location=library&pageSize=96&sort=number');
    assert(baselineResponse.ok, 'Could not load fixture baseline.');
    const baseline = await baselineResponse.json();
    assert(baseline.total > 0 && baseline.total <= 96, 'Use 1–96 fixture books.');
    $('advanced-filters').open = true;
    failFields = true;
    $('edit-advanced-filters').click();
    await waitFor(() => !$('retry-list-rules').hidden);
    assert($('save-list').disabled && $('list-rule-status').textContent.includes('Retry'), 'Loading failure must disable Apply and offer retry.');
    failFields = false;
    $('retry-list-rules').click();
    await waitFor(() => !$('smart-list-controls').disabled);
    for (const [field, type] of [['AddedDate', 'date'], ['PublicationDate', 'date'], ['DaysSinceRead', 'number'], ['FileSizeMB', 'number'], ['Number', 'text']]) {
      const row = rule(field, 'greaterOrEqual', field.endsWith('Date') ? '2020-01-01' : '2');
      assert(row.querySelector('.rule-value').type === type, `${field} needs its native input type.`);
    }
    rule('AddedDate', 'isEmpty');
    assert($('list-rules').querySelector('.rule-value').disabled, 'Unknown AddedDate must not demand a value.');
    const manualRow = rule('ManualList', 'isEmpty');
    assert(manualRow.querySelector('.rule-value').disabled, 'No-manual-list membership must not demand a list.');
    const lists = await originalFetch('/api/lists').then(response => response.json());
    const manualIds = [...manualRow.querySelector('.rule-value').options].map(option => option.value).filter(Boolean);
    assert(JSON.stringify(manualIds) === JSON.stringify(lists.filter(list => list.kind === 'manual').map(list => list.id)), 'Membership dropdown must contain only current-user manual lists.');
    const boolean = rule('HasMissingIssues', 'equals', 'yes');
    assert([...boolean.querySelector('.rule-value').options].map(option => option.value).join(',') === 'yes,no', 'Missing-issue choices must be Yes/No.');
    const invalid = rule('DaysSinceRead', 'greaterThan', '');
    $('list-form').requestSubmit();
    assert($('list-dialog').open && invalid.querySelector('.rule-value').getAttribute('aria-invalid') === 'true' && document.activeElement === invalid.querySelector('.rule-value'), 'Invalid numeric value must retain draft, identify its field and focus it.');
    rule('Title', 'contains', '界'.repeat(1000));
    $('list-form').requestSubmit();
    assert($('list-dialog').open && $('list-status').textContent.includes('6,000'), 'Oversized encoded filter must fail locally with a retained draft.');
    rule('PageCount', 'greaterOrEqual', '0');
    $('add-list-group').click();
    const group = $('list-rules').querySelector('.rule-group');
    change(group.querySelector('.group-match'), 'any');
    rule('Title', 'notContains', 'never-match-' + Date.now(), group.querySelector('.list-rule'));
    const draftCount = $('list-rules').querySelectorAll('.list-rule').length;
    failPreview = true;
    $('list-form').requestSubmit();
    await waitFor(() => !$('preview-list').disabled);
    assert($('list-dialog').open && $('list-rules').querySelectorAll('.list-rule').length === draftCount && !$('save-list').disabled
      && $('list-preview-status').textContent.includes('Simulated advanced-filter retry failure'), 'Apply failure must report the error, keep its draft and permit retry.');
    failPreview = false;
    await apply();
    const applied = history.state.catalog.advancedFilter;
    assert(applied.rules[1].match === 'any' && $('active-filter-count').textContent.includes('2 active'), 'Nested rules and active count did not apply.');
    const request = requests.findLast(item => item.url.startsWith('/api/books?'));
    assert(JSON.stringify(JSON.parse(new URL(request.url, location.origin).searchParams.get('filter'))) === JSON.stringify(applied), 'Books request lost the advanced filter.');
    assert($('books').querySelectorAll('.book-card').length === baseline.total, 'All-match advanced rules changed fixture membership.');
    change($('stack-by'), 'series'); await waitFor(idle);
    assert(new URL(requests.findLast(item => item.url.startsWith('/api/book-groups?')).url, location.origin).searchParams.has('filter'), 'Stack aggregation request lost advanced rules.');
    const stack = $('books').querySelector('.group-card .cover-button');
    if (stack) {
      stack.click(); await waitFor(() => idle() && history.state.catalog.groupKey !== null);
      assert(JSON.stringify(history.state.catalog.advancedFilter) === JSON.stringify(applied), 'Opening a stack lost filters.');
      $('back-to-groups').click(); await waitFor(() => idle() && history.state.catalog.groupKey === null);
      assert(JSON.stringify(history.state.catalog.advancedFilter) === JSON.stringify(applied), 'Returning to stacks lost filters.');
    }
    const arrangement = ['library-location', 'group-by', 'stack-by', 'book-sort', 'sort-direction', 'page-size'].map(id => [id, $(id).value]);
    change($('book-rating'), '0'); await waitFor(idle);
    await clear();
    assert(!history.state.catalog.advancedFilter && $('book-rating').value === '' && $('book-status').value === 'all' && !$('search').value && !$('reading-list').value, 'Clear did not clear actual catalog filters.');
    assert(arrangement.every(([id, value]) => $(id).value === value), 'Clear changed location or display choices.');
    history.back();
    await waitFor(() => history.state?.catalog?.advancedFilter && $('book-rating').value === '0' && !$('library-view').hidden && idle());
    assert(JSON.stringify(history.state.catalog.advancedFilter) === JSON.stringify(applied) && $('book-rating').value === '0', 'Browser Back did not restore advanced and basic filters.');
    await open();
    $('save-filter-list').click();
    assert(!$('list-name').disabled && !$('list-folder').disabled, 'Save flow must enable name and folder.');
    const name = `Advanced filter fixture ${Date.now()}`;
    change($('list-name'), name);
    const folder = [...$('list-folder').options].find(option => option.value)?.value || '';
    change($('list-folder'), folder);
    $('preview-list').click();
    await waitFor(() => !$('save-list').disabled);
    failSave = true;
    $('list-form').requestSubmit();
    await waitFor(() => !$('list-controls').disabled);
    assert($('list-name').value === name && $('list-folder').value === folder && $('list-rules').querySelectorAll('.list-rule').length === draftCount
      && $('list-status').textContent.includes('Simulated advanced-filter retry failure'), 'Save error must be reported without discarding name, folder or rules.');
    failSave = false;
    $('list-form').requestSubmit();
    await waitFor(() => !$('list-dialog').open);
    await waitFor(idle);
    const savedLists = await originalFetch('/api/lists').then(response => response.json());
    const saved = savedLists.find(list => list.name === name);
    assert(saved?.kind === 'smart' && (saved.folderId || '') === folder && JSON.stringify(saved.rules) === JSON.stringify(applied.rules), 'Saved smart list differs from the advanced-rule draft.');
    assert($('book-rating').value === '0' && !$('reading-list').value && JSON.stringify(history.state.catalog.advancedFilter) === JSON.stringify(applied), 'Save-as-list changed current catalog filters.');
    change($('book-status'), 'on-deck'); await waitFor(idle);
    assert($('edit-advanced-filters').disabled && !history.state.catalog.advancedFilter && $('reading-status-hint').textContent.includes('clears'), 'Discovery feed silently retained unsupported filters.');
    await clear();
    await open();
    const cancel = new Event('cancel', { cancelable: true });
    $('list-dialog').dispatchEvent(cancel);
    assert(!cancel.defaultPrevented, 'Unchanged filter dialog must allow native Escape.');
    $('list-dialog').close(); await tick();
    assert(document.activeElement === $('edit-advanced-filters'), 'Closing filter editor did not restore trigger focus.');
    console.log('PASS: real apply/clear/nesting/stack/history/save-as-list, native types and ownership choices; simulated schema/preview/save failures retain drafts. Disposable smart list:', name);
  } finally {
    window.fetch = originalFetch;
    window.confirm = originalConfirm;
  }
})();
