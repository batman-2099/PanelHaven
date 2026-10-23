// Run in the browser console on an authenticated, idle Library page as an ADMIN of a DISPOSABLE server.
// window.panelHavenNextIssueCheck = { disposable: true, bookIds: ['issue1-id', 'issue2-id', 'issue3-id'] };
// Fixtures: exact same series/location, numbers 1/2/3, three portrait pages in ordinary order,
// no later issue. Writes real progress/preferences and temporarily edits issue 1 metadata; recreate fixtures after failure.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
  const waitFor = async (predicate, message) => {
    const deadline = Date.now() + 15000;
    while (!predicate()) {
      if (Date.now() > deadline) throw new Error(message);
      await pause(25);
    }
  };
  const config = window.panelHavenNextIssueCheck;
  assert(config?.disposable === true, 'Explicit disposable server marker required before any writes.');
  assert(config.bookIds?.length === 3 && new Set(config.bookIds).size === 3, 'Supply issue 1/2/3 bookIds.');
  assert(!$('library-view').hidden && !document.querySelector('dialog[open]'), 'Start on an idle Library page.');
  const originalFetch = window.fetch;
  const get = async path => {
    const response = await originalFetch(path);
    assert(response.ok, `Fixture request failed: ${path}`);
    return response.json();
  };
  assert((await get('/api/session')).role === 'admin', 'An administrator is required for identity mutation checks.');
  const ids = config.bookIds;
  const path = id => `/api/books/${encodeURIComponent(id)}`;
  const books = await Promise.all(ids.map(id => get(path(id))));
  assert(books[0].series?.trim() && books.every((book, index) => book.series === books[0].series &&
    String(book.number) === String(index + 1) &&
    book.pageCount === 3 && book.availability === 'available'), 'Fixtures must be available same-series issues 1/2/3 with three pages.');
  for (const [index, id] of ids.entries()) {
    const order = await get(`${path(id)}/page-order`);
    assert(JSON.stringify(order.pages) === '[0,1,2]', 'Fixtures require ordinary page order.');
    assert((await get(`${path(id)}/next-issue`)).id === (ids[index + 1] ?? null), 'Fixture successor chain must be exactly 1 → 2 → 3 → none.');
  }
  const onBook = id => location.hash.split('?')[0] === `#reader/${encodeURIComponent(id)}` &&
    !$('reader-view').hidden && !$('reader-details').disabled;
  const imageReady = () => [...$('reader-pages').querySelectorAll('img')].some(image => image.complete && image.naturalWidth);
  const open = async id => {
    const previous = $('reader-pages').firstElementChild;
    location.hash = `#reader/${encodeURIComponent(id)}`;
    await waitFor(() => !previous?.isConnected && onBook(id) && imageReady(), 'Reader did not open fixture.');
  };
  const advance = async id => {
    const previous = $('reader-pages').firstElementChild;
    $('next-page').click();
    await waitFor(() => !previous?.isConnected && onBook(id) && imageReady(), 'Next issue did not open the expected fixture.');
  };
  const go = async position => {
    $('page-number').value = String(position);
    $('page-form').requestSubmit();
    await waitFor(() => {
      const image = $('reader-pages').querySelector(`[data-page="${position - 1}"] img`);
      return Number($('page-number').value) === position && image?.complete && image.naturalWidth;
    }, 'Page navigation did not settle.');
    // Allow the reader's visibility debounce to record the actual displayed page/spread.
    await pause(300);
  };
  const mode = value => {
    $('reader-mode').value = value;
    $('reader-mode').dispatchEvent(new Event('change', { bubbles: true }));
  };
  const nextReady = async () => {
    await waitFor(() => !$('next-page').disabled && $('next-page').getAttribute('aria-label') === 'Next issue', 'Next issue did not become available.');
    assert(/next issue/i.test($('next-page').textContent), 'Next issue text is missing.');
  };
  const noMore = () => $('next-page').disabled && $('next-page').textContent.trim() === 'No more issues' &&
    $('next-page').getAttribute('aria-label') === 'No more issues';
  const editIdentity = async (field, value) => {
    $('reader-details').click();
    await waitFor(() => !$('edit-metadata').hidden, 'Administrator metadata action did not load.');
    $('edit-metadata').click();
    await waitFor(() => !$('metadata-view').hidden && !$('metadata-controls').disabled, 'Metadata editor did not load.');
    const row = $('metadata-fields').querySelector(`[data-field="${field}"]`);
    const apply = row.querySelector('.apply-change');
    apply.click();
    row.querySelector('.metadata-value').value = value;
    row.querySelector('.metadata-value').dispatchEvent(new Event('input', { bubbles: true }));
    $('metadata-form').requestSubmit($('save-metadata'));
    await waitFor(() => !$('metadata-controls').disabled && !apply.checked, 'Metadata save did not complete.');
    $('metadata-back').click();
    await waitFor(() => !$('reader-view').hidden && $('details-dialog').open && !$('edit-metadata').hidden, 'Metadata return did not restore reader details.');
    document.querySelector('[data-close="details-dialog"]').click();
  };
  let savedProgress = null;
  let failLookup = false, failSave = false, holdLookup = false, release, held = false;
  window.fetch = async (input, options = {}) => {
    const url = new URL(input instanceof Request ? input.url : input, location.href);
    const method = options.method || (input instanceof Request ? input.method : 'GET');
    if (url.pathname === `${path(ids[0])}/next-issue`) {
      if (failLookup) { failLookup = false; return new Response('Injected next issue lookup failure', { status: 503 }); }
      if (holdLookup) {
        holdLookup = false;
        const response = await originalFetch(input, options);
        held = true;
        await new Promise(resolve => { release = resolve; });
        return response;
      }
    }
    if (failSave && method === 'POST' && url.pathname === `${path(ids[0])}/progress`)
      return new Response('Injected progress save failure', { status: 503 });
    if (method === 'POST' && url.pathname === `${path(ids[0])}/progress`) {
      const response = await originalFetch(input, options);
      if (response.ok) savedProgress = JSON.parse(options.body);
      return response;
    }
    return originalFetch(input, options);
  };
  try {
    await open(ids[0]);
    $('rtl').checked = false;
    $('cover-alone').checked = true;
    $('rotation').value = '0';
    $('fit').value = 'page';
    $('zoom').value = '100';
    for (const input of $('skip-type-options').querySelectorAll('input')) input.checked = false;
    mode('single');
    await go(1);
    assert(!$('next-page').disabled && $('next-page').getAttribute('aria-label') === 'Next page', 'Ordinary Next page was replaced by issue navigation.');
    $('next-page').click();
    await waitFor(() => Number($('page-number').value) === 2, 'Normal Next did not advance one page.');
    assert(onBook(ids[0]), 'Normal Next left the comicbook.');
    await go(3);
    await nextReady();
    await advance(ids[1]);
    const saved = await get(path(ids[0]));
    assert(saved.currentPage === 2 && saved.completed, 'Outgoing final-page progress was not saved before issue navigation.');

    mode('spread');
    await go(1);
    await waitFor(() => !$('next-page').disabled, 'Cover spread did not settle.');
    assert($('next-page').getAttribute('aria-label') === 'Next page', 'Cover alone incorrectly became the final spread.');
    $('next-page').click();
    await nextReady();
    assert($('reader-pages').querySelector('[data-page="1"] img') && $('reader-pages').querySelector('[data-page="2"] img'), 'Final spread must include pages 2 and 3.');
    await advance(ids[2]);
    mode('single');
    await go(3);
    await waitFor(noMore, 'Last issue did not show disabled No more issues.');

    await open(ids[1]);
    mode('continuous');
    await go(1);
    assert($('next-page').getAttribute('aria-label') === 'Next page', 'Continuous first page incorrectly offered another issue.');
    await go(3);
    await nextReady();
    await advance(ids[2]);
    mode('single');

    failLookup = true;
    await open(ids[0]);
    await go(3);
    await waitFor(() => !$('reader-error').hidden && !$('next-page').disabled && /retry next issue/i.test($('next-page').textContent), 'Lookup failure did not expose an error and retry.');
    $('next-page').click();
    await nextReady();
    assert(onBook(ids[0]), 'Lookup retry navigated before the successor was confirmed.');
    await advance(ids[1]);

    holdLookup = true;
    await open(ids[0]);
    await go(3);
    await waitFor(() => held, 'Stale-response scenario did not capture lookup.');
    await open(ids[2]);
    await go(3);
    await waitFor(noMore, 'New reader did not finish its own lookup.');
    release(); release = null;
    await pause(100);
    assert(onBook(ids[2]) && noMore() && $('reader-error').hidden, 'Stale lookup overwrote the new reader.');

    await open(ids[0]);
    await go(3);
    await nextReady();
    await editIdentity('Number', '2.5');
    await nextReady();
    await advance(ids[2]);
    await open(ids[0]);
    await editIdentity('Number', books[0].number);
    await nextReady();
    await advance(ids[1]);

    held = false;
    holdLookup = true;
    await open(ids[0]);
    await go(3);
    await waitFor(() => held, 'Identity-change scenario did not capture the old lookup.');
    await editIdentity('Series', `Next-issue isolated fixture ${Date.now()}`);
    await waitFor(noMore, 'Changing Series did not invalidate the pending old-series successor.');
    release(); release = null;
    await pause(100);
    assert(noMore(), 'The old identity lookup overwrote the current reader successor.');
    await editIdentity('Series', books[0].series);
    await nextReady();
    await advance(ids[1]);

    await open(ids[0]);
    await go(2);
    await waitFor(() => savedProgress?.page === 1, 'Pre-failure progress did not save.');
    failSave = true;
    savedProgress = null;
    await go(3);
    await nextReady();
    await waitFor(() => !$('retry-save').hidden, 'Progress failure was not surfaced.');
    $('next-page').click();
    await waitFor(() => onBook(ids[0]) && !$('reader-error').hidden && /before leaving/i.test($('reader-error').textContent), 'Failed progress save did not block issue navigation.');
    failSave = false;
    $('retry-save').click();
    await waitFor(() => $('retry-save').hidden && savedProgress?.page === 2, 'Real progress retry did not recover.');
    await nextReady();
    await advance(ids[1]);
    console.log('PASS: real normal pages, single/spread/continuous issue boundaries, saved outgoing progress, no successor, Series/Number cache invalidation; injected lookup retry, stale reader/identity response isolation, and save-failure route protection.');
  } finally {
    failSave = false;
    window.fetch = originalFetch;
    release?.();
  }
})();
