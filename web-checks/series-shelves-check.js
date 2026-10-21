// Run in the browser console on an idle, unfiltered DISPOSABLE Library page.
// Requires 25+ recently added named series, each with incomplete reading activity.
// Read-only: real API results; only error/delayed-response scenarios are simulated.
// window.panelHavenSeriesViewsCheck = { disposable: true };
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (value, message) => { if (!value) throw new Error(message); };
  const waitFor = async predicate => {
    const deadline = Date.now() + 15000;
    while (!predicate()) {
      if (Date.now() > deadline) throw new Error('Series navigation timed out.');
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const idle = () => $('books').getAttribute('aria-busy') !== 'true';
  const change = async (id, value) => {
    $(id).value = value;
    $(id).dispatchEvent(new Event('change', { bubbles: true }));
    await new Promise(resolve => setTimeout(resolve, 0));
    await waitFor(idle);
  };
  const kinds = ['on-deck', 'recently-updated', 'newly-added'];
  const cards = () => [...$('books').querySelectorAll('[data-library-focus]')];
  const checkPage = async page => {
    const size = Number($('page-size').value), kind = $('book-status').value;
    const response = await fetch(`/api/series-shelf?kind=${kind}&page=${page}&pageSize=${size}`);
    assert(response.ok, `Could not read ${kind}.`);
    const result = await response.json();
    assert(result.page === page && result.pageSize === size, 'API page or page size differs from the selected page.');
    assert(JSON.stringify(cards().map(button => button.dataset.libraryFocus)) ===
      JSON.stringify(result.items.map(item => `group:${item.key}`)), `${kind}: main-grid order or exact keys differ from the API.`);
    assert($('library-page').textContent === `Page ${page} of ${Math.max(1, Math.ceil(result.total / size))}` &&
      $('library-page-bottom').textContent === $('library-page').textContent, 'Common pagers disagree with the API.');
    assert(cards().every((button, index) => button.closest('.group-card').querySelector('progress')?.value === result.items[index].completionPercent),
      'Series progress differs from the private API result.');
    return result;
  };
  assert(window.panelHavenSeriesViewsCheck?.disposable === true, 'Disposable server required.');
  assert(!$('library-view').hidden && $('library-location').value === 'library' && $('book-status').value === 'all' && !document.querySelector('dialog[open]'),
    'Start on an idle, unfiltered Library page.');
  await waitFor(idle);
  assert(kinds.every(kind => $('book-status').querySelector(`option[value="${kind}"][data-series-view]`)) && $('reading-list').size > 1,
    'Three series status options and expanded reading lists are required.');
  await change('page-size', '24');
  await change('group-by', 'none');
  await change('stack-by', 'none');
  const selection = $('books').querySelector('[data-book-id]');
  assert(selection, 'A Library comicbook is required.');
  selection.checked = true;
  selection.dispatchEvent(new Event('change', { bubbles: true }));
  for (const kind of kinds) {
    await change('book-status', kind);
    assert($('group-by').value === 'none' && $('stack-by').value === 'series' && !$('reading-status-hint').hidden, 'Series mode did not explain its stacking and filtering.');
    assert($('selection-count').textContent.startsWith('0 '), 'Series navigation retained selected books.');
    for (const id of ['search', 'group-by', 'stack-by', 'book-sort', 'sort-direction', 'book-rating', 'publisher', 'book-availability'])
      assert($(id).disabled, `${id} misleadingly remains available in a series view.`);
    assert($('book-sort').closest('label').hidden && $('sort-direction').closest('label').hidden,
      'Fixed-order series root exposes sort controls.');
    assert(!$('page-size').disabled && !$('refresh-library').disabled && !$('reading-list').disabled,
      'Series mode disabled paging, refresh, or reading-list navigation.');
    const first = await checkPage(1);
    assert(first.total > 24 && !$('next-books').disabled, `${kind} needs at least 25 eligible fixture series.`);
    $('next-books').click();
    await waitFor(idle);
    await checkPage(2);
    const cover = cards()[0], focusKey = cover.dataset.libraryFocus;
    const key = focusKey.slice('group:'.length), savedPage = $('library-page').textContent;
    cover.focus(); cover.click();
    await waitFor(() => !$('group-navigation').hidden && idle());
    assert($('group-label').textContent === key && $('book-status').value === kind && $('group-by').value === 'none' && $('stack-by').value === 'series',
      'Series view did not open the exact group or retain its mode.');
    assert($('book-sort').value === 'number' && $('sort-direction').value === 'asc', 'Series issues did not use ascending issue order.');
    assert(!$('book-sort').disabled && !$('sort-direction').disabled, 'Opened series did not enable Arrange controls.');
    const response = await fetch(`/api/books?${new URLSearchParams({ location: 'library', groupBy: 'series', groupKey: key, status: 'all', sort: 'number', direction: 'asc', page: '1', pageSize: $('page-size').value })}`);
    assert(response.ok, 'Could not read the opened series.');
    const books = await response.json();
    assert(JSON.stringify(cards().map(button => button.dataset.libraryFocus)) === JSON.stringify(books.items.map(book => `book:${book.id}`)),
      'Opened series order or membership differs from the real books API.');
    history.back();
    await waitFor(() => idle() && $('group-navigation').hidden && document.activeElement?.dataset.libraryFocus === focusKey);
    assert($('book-status').value === kind && $('library-page').textContent === savedPage,
      'Browser Back lost the originating series view or page.');
    await checkPage(2);
    history.forward();
    await waitFor(() => idle() && !$('group-navigation').hidden && $('group-label').textContent === key);
    assert($('book-status').value === kind && JSON.stringify(cards().map(button => button.dataset.libraryFocus)) ===
      JSON.stringify(books.items.map(book => `book:${book.id}`)), 'Browser Forward did not reopen the same series.');
    $('back-to-groups').click();
    await waitFor(() => idle() && $('group-navigation').hidden && document.activeElement?.dataset.libraryFocus === focusKey);
    assert($('library-page').textContent === savedPage, 'Back to groups lost the saved series page.');
    await checkPage(2);
    await change('page-size', '48');
    await checkPage(1);
    await change('page-size', '24');
    await checkPage(1);
  }
  await change('book-status', 'unread');
  assert($('reading-status-hint').hidden && !$('search').disabled && !$('book-rating').disabled, 'Book status did not restore ordinary filtering.');
  await change('book-rating', '1');
  await change('book-availability', 'available');
  $('search').value = 'series-view-clears-this-query';
  $('search-form').requestSubmit();
  await waitFor(idle);
  await change('book-status', 'newly-added');
  assert($('search').value === '' && $('book-rating').value === '' && $('publisher').value === '' && $('book-availability').value === 'all',
    'Series mode retained conflicting book filters.');
  await checkPage(1);
  for (const area of ['archived', 'unsorted']) {
    await change('library-location', area);
    await waitFor(() => location.hash === (area === 'unsorted' ? '#unsorted' : '#library') && idle());
    assert($('book-status').value === 'all' && kinds.every(kind => $('book-status').querySelector(`option[value="${kind}"]`).disabled),
      'Leaving Library retained a series mode or enabled unsupported options.');
    await change('library-location', 'library');
    await waitFor(() => location.hash === '#library' && idle());
    assert(kinds.every(kind => !$('book-status').querySelector(`option[value="${kind}"]`).disabled), 'Returning to Library did not enable series modes.');
    await change('book-status', 'newly-added');
  }
  const originalFetch = window.fetch;
  let release;
  try {
    window.fetch = (input, options) => new URL(input instanceof Request ? input.url : input, location.href).pathname === '/api/series-shelf'
      ? Promise.resolve(new Response('Simulated series failure', { status: 503 })) : originalFetch(input, options);
    $('refresh-library').click();
    await waitFor(idle);
    assert(!cards().length && /Refresh/i.test($('library-status').textContent) && !$('refresh-library').disabled,
      'Failed series load retained stale cards or offered no retry.');
    window.fetch = originalFetch;
    $('refresh-library').click();
    await waitFor(idle);
    await checkPage(1);
    const gate = new Promise(resolve => { release = resolve; });
    let responseReady = false, delivered = false;
    window.fetch = async (input, options) => {
      if (new URL(input instanceof Request ? input.url : input, location.href).pathname !== '/api/series-shelf') return originalFetch(input, options);
      const response = await originalFetch(input, options);
      const body = await response.text();
      responseReady = true;
      await gate;
      delivered = true;
      return new Response(body, { status: response.status, headers: response.headers });
    };
    $('refresh-library').click();
    await waitFor(() => responseReady);
    assert(!idle() && $('books').inert && $('next-books').disabled && $('previous-books').disabled,
      'Loading series results left stale cards or paging actionable.');
    await change('book-status', 'all');
    await change('stack-by', 'none');
    const currentCards = JSON.stringify(cards().map(button => button.dataset.libraryFocus));
    const currentStatus = $('library-status').textContent;
    release();
    await waitFor(() => delivered);
    await new Promise(resolve => setTimeout(resolve, 100));
    assert(idle() && $('book-status').value === 'all' && JSON.stringify(cards().map(button => button.dataset.libraryFocus)) === currentCards && $('library-status').textContent === currentStatus,
      'A late series response overwrote the newly selected book view.');
  } finally {
    release?.();
    window.fetch = originalFetch;
  }
  console.log('PASS: three API-matched main-grid series modes, common paging/page size, exact-series order, saved-page return focus, filter reset, location restrictions, simulated error/retry/loading/stale response.');
})();
