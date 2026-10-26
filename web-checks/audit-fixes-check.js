// Run on an idle authenticated Library page as an administrator with at least one comicbook.
// Reads real catalog data; comparison reads are held locally. No server data is changed.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const waitFor = async predicate => {
    const deadline = Date.now() + 30000;
    while (!predicate()) {
      if (Date.now() > deadline) throw new Error('Audit fixes check timed out.');
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const idle = () => $('books').getAttribute('aria-busy') !== 'true';
  const change = async (id, value) => {
    $(id).value = value;
    $(id).dispatchEvent(new Event('change', { bubbles: true }));
    await waitFor(idle);
  };
  const originalFetch = window.fetch;
  let releaseComparison;
  assert(!$('library-view').hidden && !document.querySelector('dialog[open]'), 'Start on the idle Library page.');
  try {
    await change('group-by', 'none');
    await change('stack-by', 'none');
    const book = await originalFetch('/api/books?page=1&pageSize=1').then(response => response.json()).then(result => result.items[0]);
    assert(book, 'At least one comicbook is required.');
    $('search').value = book.series || book.title || '';
    $('search-form').requestSubmit();
    await waitFor(idle);
    await change('book-sort', 'title');
    await change('sort-direction', 'desc');
    const query = $('search').value;
    await change('book-status', 'newly-added');
    assert($('search').disabled && !$('return-to-browse').hidden, 'Discovery must offer return to the previous browsing context.');
    $('return-to-browse').click();
    await waitFor(() => idle() && !$('search').disabled);
    assert($('search').value === query && $('book-sort').value === 'title' && $('sort-direction').value === 'desc' && $('stack-by').value === 'none', 'Discovery return lost browsing criteria.');
    const card = $('books').querySelector('.book-card');
    assert(card, 'Returned browsing criteria must still have a result.');
    const checkbox = card.querySelector('[data-book-id]');
    checkbox.click();
    assert(document.querySelector('.selection-hint').checkVisibility(), 'Selection instructions must be visible.');
    const details = card.querySelector('.book-details');
    details.focus(); details.click();
    await waitFor(() => $('details-dialog').open && !$('rating-form').hidden);
    assert(!$('library-view').hidden && $('reader-view').hidden && checkbox.checked, 'Details must not enter the reader or change selection.');
    $('details-dialog').querySelector('[data-close]').click();
    assert(document.activeElement === details, 'Closing details must return focus to its catalog action.');
    $('clear-selection').click();
    window.fetch = async (input, options) => {
      const url = new URL(input instanceof Request ? input.url : input, location.href);
      if (url.pathname === '/api/maintenance') return new Response(JSON.stringify({ page: 1, pageSize: 48, total: 1, items: [{ ...book, duplicateKey: 'audit-only' }] }), { headers: { 'Content-Type': 'application/json' } });
      if (url.pathname.startsWith('/api/duplicates/')) return new Promise(resolve => { releaseComparison = () => resolve(new Response('{}', { status: 503 })); });
      return originalFetch(input, options);
    };
    $('account-menu').open = true;
    $('maintenance').click();
    await change('maintenance-kind', 'duplicates');
    await waitFor(() => $('maintenance-items').querySelector('[aria-label^="Compare copies:"]'));
    $('maintenance-items').querySelector('[aria-label^="Compare copies:"]').click();
    await waitFor(() => releaseComparison && $('duplicate-dialog').open);
    $('duplicate-dialog').querySelector('[data-close]').click();
    assert(!$('duplicate-dialog').open, 'Read-only comparison Close must work while its request is pending.');
    releaseComparison(); releaseComparison = null;
    $('maintenance-dialog').querySelector('[data-close]').click();
    console.log('Audit fixes checks passed: discovery return, details during selection, focus return, pending comparison dismissal.');
  } finally {
    releaseComparison?.();
    window.fetch = originalFetch;
  }
})();
