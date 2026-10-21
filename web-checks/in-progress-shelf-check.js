// Run on an unfiltered Library page on a DISPOSABLE server with an in-progress
// comicbook saved mid-book in ordinary page order. This reads it and marks it completed.
// window.panelHavenInProgressCheck = { disposable: true };
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const waitFor = async predicate => {
    const deadline = Date.now() + 15000;
    while (!predicate()) {
      if (Date.now() > deadline) throw new Error('In Progress workflow timed out.');
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  assert(window.panelHavenInProgressCheck?.disposable === true, 'Disposable server required.');
  const idle = () => $('books').getAttribute('aria-busy') !== 'true';
  assert(!$('library-view').hidden && $('library-location').value === 'library', 'Start on the Library page.');
  await waitFor(idle);
  $('group-by').value = 'none';
  $('stack-by').value = 'series';
  $('stack-by').dispatchEvent(new Event('change', { bubbles: true }));
  await waitFor(idle);
  $('book-status').value = 'in-progress';
  $('book-status').dispatchEvent(new Event('change', { bubbles: true }));
  await waitFor(idle);
  assert($('stack-by').value === 'none', 'In Progress did not switch to individual comicbooks.');
  const cover = $('books').querySelector('[data-library-focus^="book:"]');
  assert(cover, 'An in-progress fixture is required.');
  const id = cover.dataset.libraryFocus.slice('book:'.length);
  const response = await fetch(`/api/books/${id}`);
  assert(response.ok, 'Cannot read fixture.');
  const book = await response.json();
  assert(book.started && !book.completed, 'In Progress included an unread or completed comicbook.');
  assert(book.currentPage > 0 && book.currentPage < book.pageCount - 1, 'Fixture must have a saved mid-book position.');
  cover.click();
  await waitFor(() => !$('reader-view').hidden && $('reader-pages').querySelector('img'));
  await $('reader-pages').querySelector('img').decode();
  assert(Number($('page-number').value) === book.currentPage + 1, 'In Progress did not resume the saved reading position.');
  $('close-reader').click();
  await waitFor(() => !$('library-view').hidden && idle() && document.activeElement?.dataset.libraryFocus === `book:${id}`);
  assert($('book-status').value === 'in-progress', 'Reader return lost the selected reading status.');
  const completed = await fetch('/api/reading-status', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-PanelHaven-Request': '1' }, body: JSON.stringify({ bookIds: [id], completed: true }) });
  assert(completed.ok, 'Could not complete the disposable fixture.');
  $('refresh-library').click();
  await waitFor(idle);
  assert(![...$('books').querySelectorAll('[data-book-id]')].some(node => node.dataset.bookId === id),
    'Completed comicbook remained in In Progress.');
  assert($('book-status').value === 'in-progress' && $('reading-list').size > 1,
    'Refresh lost In Progress or collapsed reading lists.');
  console.log('PASS: In Progress main-grid selection, real saved-position resume, return focus, completed-book removal, refresh state, expanded reading lists.');
})();
