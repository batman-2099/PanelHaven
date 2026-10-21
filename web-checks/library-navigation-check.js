// Run in the browser console on an idle, unfiltered DISPOSABLE Library page as administrator.
// Requires a readable comicbook, a named series, and a manual list with 2–96 Library books.
// Reader progress and list-order writes are intercepted; reload afterward to discard simulated state.
// window.panelHavenLibraryNavigationCheck = { disposable: true };
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const waitFor = async predicate => {
    const deadline = Date.now() + 30000;
    while (!predicate()) {
      if (Date.now() > deadline) throw new Error('Library navigation check timed out.');
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
  const json = value => new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } });
  const originalFetch = window.fetch;
  let catalogRequests = 0, list = null, hideBook = null, orderWrites = 0, scanRevision = null;
  assert(window.panelHavenLibraryNavigationCheck?.disposable === true, 'Disposable server required.');
  assert(!$('library-view').hidden && $('library-location').value === 'library' && $('book-status').value === 'all' && !document.querySelector('dialog[open]'),
    'Start on an unfiltered Library page.');
  await waitFor(idle);
  await change('group-by', 'none');
  window.fetch = async (input, options = {}) => {
    const url = new URL(input instanceof Request ? input.url : input, location.href);
    const method = options.method || (input instanceof Request ? input.method : 'GET');
    if (url.pathname === '/api/books' || url.pathname === '/api/book-groups') catalogRequests++;
    if (method !== 'GET' && (url.pathname.endsWith('/progress') || url.pathname === '/api/reader-preferences')) return new Response(null, { status: 204 });
    if (list && url.pathname === `/api/lists/${encodeURIComponent(list.id)}`) return json(list);
    if (list && url.pathname === `/api/lists/${encodeURIComponent(list.id)}/order`) {
      orderWrites++;
      const { bookId, beforeId } = JSON.parse(options.body);
      list.bookIds = list.bookIds.filter(id => id !== bookId);
      list.bookIds.splice(beforeId === null ? list.bookIds.length : list.bookIds.indexOf(beforeId), 0, bookId);
      return new Response(null, { status: 204 });
    }
    const response = await originalFetch(input, options);
    if (url.pathname === '/api/scan' && scanRevision !== null) return json({ ...await response.json(), revision: scanRevision, state: 'completed' });
    if (url.pathname === '/api/books' && (hideBook || (list && url.searchParams.get('listId') === list.id))) {
      const data = await response.json();
      if (list && url.searchParams.get('listId') === list.id) data.items.sort((a, b) => list.bookIds.indexOf(a.id) - list.bookIds.indexOf(b.id));
      data.items = data.items.filter(book => book.id !== hideBook);
      return json(data);
    }
    return response;
  };
  try {
    await change('book-sort', $('book-sort').value === 'title' ? 'series' : 'title');
    await change('page-size', '24');
    if (!$('next-books').disabled) {
      $('next-books').click(); await waitFor(idle);
      $('previous-books').click(); await waitFor(idle);
    }
    $('refresh-library').click(); await waitFor(idle);
    await change('stack-by', 'series');
    const group = $('books').querySelector('[data-library-focus]');
    assert(group, 'A catalog group is required.');
    const groupKey = group.dataset.libraryFocus;
    const originSort = $('book-sort').value;
    group.focus(); group.click(); await waitFor(idle);
    await change('book-sort', 'number');
    history.back();
    await waitFor(() => idle() && $('group-navigation').hidden && document.activeElement.dataset.libraryFocus === groupKey);
    assert($('book-sort').value === originSort, 'Browser Back retained the series sort instead of the originating view.');
    history.forward();
    await waitFor(() => idle() && !$('group-navigation').hidden);
    assert($('book-sort').value === 'number' && $('group-label').textContent === groupKey.slice('group:'.length),
      'Browser Forward lost the series or its changed sort.');
    $('back-to-groups').click();
    await waitFor(() => idle() && document.activeElement.dataset.libraryFocus === groupKey);
    $('account-menu').open = true;
    $('server-settings').click();
    await waitFor(() => !$('settings-view').hidden && !$('reload-server-settings').disabled);
    $('settings-catalog-back').click();
    await waitFor(() => idle() && !$('library-view').hidden && document.activeElement.dataset.libraryFocus === groupKey);
    await change('stack-by', 'none');
    const readable = [...$('books').querySelectorAll('[data-library-focus]')].find(button => !button.closest('.book-card').querySelector('.book-availability'));
    assert(readable, 'A readable Library comicbook is required.');
    const bookKey = readable.dataset.libraryFocus;
    readable.focus(); readable.click();
    await waitFor(() => !$('reader-view').hidden && !$('reader-details').disabled);
    $('close-reader').click();
    await waitFor(() => idle() && !$('library-view').hidden && document.activeElement.dataset.libraryFocus === bookKey);
    document.activeElement.click();
    await waitFor(() => !$('reader-view').hidden && !$('reader-details').disabled);
    hideBook = bookKey.slice('book:'.length);
    $('close-reader').click();
    await waitFor(() => idle() && !$('library-view').hidden && document.activeElement === $('library-status'));
    hideBook = null;
    $('search').focus();
    const scan = await originalFetch('/api/scan').then(response => response.json());
    scanRevision = Number(scan.revision) + 1;
    const count = catalogRequests;
    $('retry-scan-status').click();
    await waitFor(() => catalogRequests > count && idle());
    assert(document.activeElement === $('search'), 'Background scan refresh stole focus.');
    const lists = await originalFetch('/api/lists').then(response => response.json());
    for (const candidate of lists.filter(item => item.kind === 'manual')) {
      const detail = await originalFetch(`/api/lists/${encodeURIComponent(candidate.id)}`).then(response => response.json());
      if (detail.bookIds.length >= 2 && detail.bookIds.length <= 96) { list = detail; break; }
    }
    assert(list, 'A manual list with 2–96 Library comicbooks is required.');
    await change('page-size', '96');
    await change('reading-list', list.id);
    await change('book-status', 'newly-added');
    assert($('reading-list').value === '', 'Choosing a series view did not clear the reading list.');
    await change('reading-list', list.id);
    assert($('book-status').value === 'all', 'Choosing a reading list did not exit the series view.');
    await change('stack-by', 'none');
    const moves = [...$('books').querySelectorAll('[data-move="1"]')];
    assert(moves.length >= 2, 'The manual list needs at least two visible Library comicbooks.');
    const movingId = moves[0].closest('.book-card').querySelector('[data-book-id]').dataset.bookId;
    moves[0].focus(); moves[0].click();
    await waitFor(() => orderWrites === 1 && idle() && !document.activeElement.disabled && document.activeElement !== document.body);
    assert(document.activeElement.dataset.move === '1' && document.activeElement.closest('.book-card')?.querySelector('[data-book-id]').dataset.bookId === movingId,
      'Reorder did not restore the same comicbook move control after replacing cards.');
    const up = document.activeElement.closest('.book-card').querySelector('[data-move="-1"]');
    hideBook = movingId;
    up.focus(); up.click();
    await waitFor(() => idle() && document.activeElement === $('management-status'));
    console.log('PASS: group/settings/reader return focus, absent-book fallback, background focus, series-to-list transition, simulated reorder and off-page fallback.');
  } finally {
    window.fetch = originalFetch;
  }
})();
