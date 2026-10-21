// Run in the browser console on an idle, unfiltered Library page. Read-only real API/render checks.
// Fixture: <=96 books, >24 (publisher, series) stacks, whole-number issues with differing
// stack minima/maxima, and a shared named series in two publishers on the first 24-stack page.
// Example: Press A/B each have 15 series (5 shared), two issues per series: 2+i and 10+i.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const same = (actual, expected, message) => assert(JSON.stringify(actual) === JSON.stringify(expected), message);
  const waitFor = async predicate => {
    const deadline = Date.now() + 15000;
    while (!predicate()) {
      if (Date.now() > deadline) throw new Error('Independent browse check timed out.');
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
  const get = async (endpoint, query = {}) => {
    const response = await fetch(`/api/${endpoint}?${new URLSearchParams({ location: 'library', status: 'all', sort: 'number', direction: 'asc', page: '1', pageSize: '96', ...query })}`);
    assert(response.ok, `Could not read ${endpoint}: ${response.status}.`);
    return response.json();
  };
  const sections = () => [...$('books').querySelectorAll(':scope > details.book-section')];
  const cards = () => [...$('books').querySelectorAll('.cover-button[data-library-focus]')];
  const members = (publisher, series) => baseline.items.filter(book => book.publisher === publisher && book.series === series);
  const unpack = result => {
    let offset = 0;
    const entries = [];
    for (const section of result.sections) {
      assert(section.start === offset && section.count > 0, 'Section ranges are not contiguous and page-local.');
      for (const item of result.items.slice(section.start, section.start + section.count)) entries.push({ section: section.key, item });
      offset += section.count;
    }
    assert(offset === result.items.length, 'Section ranges do not cover every result exactly once.');
    return entries;
  };
  const identity = entry => [entry.section, entry.item.key ?? entry.item.id];
  const checkPage = async (stacked, page = 1) => {
    const result = await get(stacked ? 'book-groups' : 'books', { sectionBy: 'publisher', groupBy: stacked ? 'series' : 'none', direction: $('sort-direction').value, page: String(page), pageSize: '24' });
    const entries = unpack(result), rendered = sections();
    assert(rendered.length === result.sections.length, 'Group by did not render one native section per API range.');
    result.sections.forEach((section, index) => {
      const node = rendered[index], content = [...node.querySelectorAll('.book-card')];
      assert(node.querySelector(':scope > summary h2')?.textContent === section.label && node.querySelector(':scope > .book-grid'), 'Section lacks its label, heading, or native grid.');
      assert(content.length === section.count, 'Section card count differs from its API range.');
      const items = result.items.slice(section.start, section.start + section.count);
      same(content.map(card => stacked ? card.querySelector('h3')?.textContent : card.querySelector('[data-book-id]')?.dataset.bookId),
        items.map(item => stacked ? item.label : item.id), 'Section membership or Arrange order differs from the API.');
      if (stacked) assert(content.every((card, i) => card.querySelector('progress')?.value === items[i].completionPercent), 'Stack progress differs from its private API aggregate.');
    });
    assert(new Set(cards().map(card => card.dataset.libraryFocus)).size === entries.length, 'Section/stack focus identities collide.');
    assert($('library-page').textContent === `Page ${page} of ${Math.ceil(result.total / 24)}` && $('library-page-bottom').textContent === $('library-page').textContent, 'Pagers disagree with API totals.');
    return { result, entries };
  };
  assert(!$('library-view').hidden && $('group-navigation').hidden && $('library-location').value === 'library' && $('book-status').value === 'all'
    && !$('search').value && !$('reading-list').value && !$('publisher').value && !$('book-rating').value && $('book-availability').value === 'all'
    && !document.querySelector('dialog[open]'), 'Start on an idle, unfiltered Library root.');
  await waitFor(idle);
  const baseline = await get('books', { groupBy: 'none', sectionBy: 'none' });
  assert(baseline.total > 24 && baseline.total <= 96 && baseline.items.every(book => /^\d+$/.test(book.number)), 'Use the documented small whole-number fixture.');
  assert(baseline.sections.length === 0, 'Group none unexpectedly returned sections.');
  await change('group-by', 'publisher');
  await change('stack-by', 'series');
  await change('book-sort', 'number');
  await change('sort-direction', 'asc');
  await change('page-size', '24');
  const first = await checkPage(true);
  assert(first.result.total > 24 && first.result.sections.length > 1, 'Fixture needs >24 stacks and two publishers on page one.');
  const pair = first.entries.find(entry => first.entries.some(other => other.section !== entry.section && other.item.key === entry.item.key));
  assert(pair, 'Fixture needs one series under two publishers on page one.');
  const twin = first.entries.find(entry => entry.section !== pair.section && entry.item.key === pair.item.key);
  for (const direction of ['asc', 'desc']) {
    await change('sort-direction', direction);
    const all = await get('book-groups', { groupBy: 'series', sectionBy: 'publisher', direction });
    const entries = unpack(all), distinct = new Set(baseline.items.map(book => JSON.stringify([book.publisher, book.series])));
    assert(all.total === distinct.size && entries.length === distinct.size, 'Same-series stacks from different publishers were merged.');
    let previous = null;
    const ranks = [];
    for (const entry of entries) {
      const books = members(entry.section, entry.item.key);
      const rank = (direction === 'asc' ? Math.min : Math.max)(...books.map(book => Number(book.number)));
      ranks.push(rank);
      assert(books.length === entry.item.count && books.some(book => book.id === entry.item.coverId), 'Stack count or cover includes another section, or was calculated after paging.');
      assert(entry.item.completed === books.filter(book => book.completed).length && entry.item.inProgress === books.filter(book => book.started && !book.completed).length
        && entry.item.unread === books.filter(book => !book.started && !book.completed).length, 'Stack reading counts include another section.');
      assert(entry.item.completionPercent === Math.floor(books.filter(book => book.completed).length * 100 / books.length), 'Stack completion percentage was not calculated from all exact members.');
      if (previous) {
        assert(previous.section.toLowerCase() <= entry.section.toLowerCase(), 'Arrange reordered the publisher sections.');
        if (previous.section === entry.section) assert(direction === 'asc' ? previous.rank <= rank : previous.rank >= rank, 'Arrange Number did not sort stack member minima/maxima naturally.');
      }
      previous = { section: entry.section, rank };
    }
    assert(new Set(ranks).size > 1, 'Fixture needs different stack issue minima/maxima.');
    const pageOne = await checkPage(true);
    same(pageOne.entries.map(identity), entries.slice(0, 24).map(identity), 'Page one is not the prefix of the fully sorted stacks.');
    $('next-books').click(); await waitFor(idle);
    const pageTwo = await checkPage(true, 2);
    same(pageTwo.entries.map(identity), entries.slice(24, 48).map(identity), 'Page two duplicates, loses, or reorders stack keys.');
    const laterCover = cards()[0], laterKey = laterCover.dataset.libraryFocus;
    laterCover.closest('details').open = true;
    laterCover.focus(); laterCover.click();
    await waitFor(() => idle() && !$('group-navigation').hidden);
    history.back();
    await waitFor(() => idle() && $('group-navigation').hidden && document.activeElement.dataset.libraryFocus === laterKey);
    await checkPage(true, 2);
    $('previous-books').click(); await waitFor(idle);
  }
  await change('sort-direction', 'asc');
  const closedIndex = first.result.sections.findIndex(section => section.key === twin.section);
  sections()[closedIndex].querySelector('summary').focus();
  if (sections()[closedIndex].open) sections()[closedIndex].querySelector('summary').click();
  await waitFor(() => !sections()[closedIndex].open);
  await new Promise(resolve => setTimeout(resolve, 0));
  $('refresh-library').click(); await waitFor(idle);
  await checkPage(true);
  assert(!sections()[closedIndex].open, 'Refresh lost the collapsed publisher.');
  await change('sort-direction', 'desc');
  assert(!sections()[closedIndex].open, 'Arrange change lost the collapsed publisher.');
  await change('sort-direction', 'asc');
  for (const entry of [pair, twin]) {
    const sectionIndex = first.result.sections.findIndex(section => section.key === entry.section);
    if (!sections()[sectionIndex].open) {
      sections()[sectionIndex].querySelector('summary').click();
      await waitFor(() => sections()[sectionIndex].open);
    }
    const cover = cards()[first.entries.findIndex(candidate => candidate.section === entry.section && candidate.item.key === entry.item.key)];
    const focusKey = cover.dataset.libraryFocus;
    const expected = members(entry.section, entry.item.key).map(book => book.id).sort();
    cover.focus(); cover.click(); await waitFor(() => idle() && !$('group-navigation').hidden);
    assert($('group-by').value === 'publisher' && $('stack-by').value === 'series' && !$('book-sort').disabled && !$('sort-direction').disabled, 'Opening a stack changed independent controls.');
    same(cards().map(card => card.dataset.libraryFocus.slice('book:'.length)).sort(), expected, 'Opening a stack leaked books from another publisher.');
    await change('sort-direction', 'desc');
    const exact = await get('books', { sectionBy: 'publisher', sectionKey: entry.section, groupBy: 'series', groupKey: entry.item.key, direction: 'desc', pageSize: '24' });
    same(exact.items.map(book => book.id).sort(), expected, 'The real API did not constrain both exact keys.');
    same(cards().map(card => card.dataset.libraryFocus), exact.items.map(book => `book:${book.id}`), 'In-stack Arrange differs from its exact API request.');
    history.back();
    await waitFor(() => idle() && $('group-navigation').hidden && document.activeElement.dataset.libraryFocus === focusKey);
    assert(sections()[sectionIndex].open && $('sort-direction').value === 'asc', 'Back lost the root Arrange value or visible return focus.');
    if (entry === pair) assert(!sections()[closedIndex].open, 'Back lost another publisher collapse state.');
    history.forward();
    await waitFor(() => idle() && !$('group-navigation').hidden);
    assert($('group-by').value === 'publisher' && $('stack-by').value === 'series' && $('sort-direction').value === 'desc', 'Forward lost Group, Stack, or Arrange.');
    same(cards().map(card => card.dataset.libraryFocus), exact.items.map(book => `book:${book.id}`), 'Forward lost the exact publisher/series stack.');
    $('back-to-groups').click();
    await waitFor(() => idle() && $('group-navigation').hidden && document.activeElement.dataset.libraryFocus === focusKey);
  }
  await change('stack-by', 'none');
  assert($('group-by').value === 'publisher' && !$('books').querySelector('.group-card'), 'Removing stacks also removed grouping or left stack cards.');
  for (const direction of ['asc', 'desc']) {
    await change('sort-direction', direction);
    const all = await get('books', { sectionBy: 'publisher', groupBy: 'none', direction });
    const entries = unpack(all);
    same(entries.map(entry => entry.item.id).sort(), baseline.items.map(book => book.id).sort(), 'Grouping changed catalog membership.');
    entries.forEach((entry, i) => {
      assert(entry.section === entry.item.publisher, 'A grouped book belongs to the wrong publisher.');
      if (i) assert(entries[i - 1].section.toLowerCase() <= entry.section.toLowerCase(), 'Arrange reordered individual-book publisher sections.');
      if (i && entries[i - 1].section === entry.section) assert(direction === 'asc'
        ? Number(entries[i - 1].item.number) <= Number(entry.item.number) : Number(entries[i - 1].item.number) >= Number(entry.item.number), 'Grouping without stacks lost natural issue sorting.');
    });
    const pageOne = await checkPage(false);
    same(pageOne.entries.map(identity), entries.slice(0, 24).map(identity), 'Grouped individual books were paged before sorting.');
    $('next-books').click(); await waitFor(idle);
    const pageTwo = await checkPage(false, 2);
    same(pageTwo.entries.map(identity), entries.slice(24, 48).map(identity), 'Grouped individual books repeat or skip a page boundary.');
    $('previous-books').click(); await waitFor(idle);
  }
  await change('group-by', 'none');
  assert(!sections().length && !$('books').querySelector('.group-card'), 'Group none / Stack none did not restore individual comicbooks.');
  console.log('PASS: independent Group/Stack/Arrange, exact dual-key isolation, natural min/max stack sorting, full-member aggregates, native collapse/refresh/history/focus, grouping without stacks and both directions across page boundaries.');
})();
