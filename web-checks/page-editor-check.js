// Run in an admin browser console on a DISPOSABLE server with Book details open.
// window.panelHavenPageEditorCheck = { disposable: true, bookId: "fixture-id" };
// Requires at least two pages. Saves real page order, types and bookmarks to the catalog.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const config = window.panelHavenPageEditorCheck;
  assert(config?.disposable === true && config.bookId && $("details-dialog").open, "Open disposable Book details first.");
  const waitFor = async condition => {
    const deadline = Date.now() + 30000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("Page editor check timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const base = `/api/books/${encodeURIComponent(config.bookId)}`;
  const originalFetch = window.fetch, originalConfirm = window.confirm;
  const read = async suffix => { const r = await originalFetch(base + suffix); assert(r.ok, "Fixture read failed."); return r.json(); };
  const before = await read("/page-order"), originalMetadata = await read("/page-metadata");
  assert(before.pages.length >= 2, "At least two pages are required.");
  const readerPosition = location.hash.startsWith(`#reader/${config.bookId}`) && before.pages[Number($("page-number").value) - 1] === 0 ? $("page-number").value : null;
  const bookmark = originalMetadata.pages[0].bookmark === "Combined editor bookmark" ? "Updated combined bookmark" : "Combined editor bookmark";
  const secondType = originalMetadata.pages[1].type === "FrontCover" ? "Story" : "FrontCover";
  const field = (page, name) => $("page-order-pages").querySelector(`[data-page="${page}"] [data-page-field="${name}"]`);
  const change = (page, name, value) => { const input = field(page, name); input.value = value; input.dispatchEvent(new Event("input", { bubbles: true })); };
  const orderInView = () => Array.from($("page-order-pages").children, node => Number(node.dataset.page));
  const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
  let release, holdLoad = true, holdSave = true, loseOrderResponse = false;
  window.fetch = async (...args) => {
    const url = new URL(args[0] instanceof Request ? args[0].url : args[0], location.href);
    const method = args[1]?.method || "GET";
    const response = await originalFetch(...args);
    if (url.pathname === base + "/page-metadata" && (method === "GET" && holdLoad || method === "PUT" && holdSave)) {
      if (method === "GET") holdLoad = false; else holdSave = false;
      await new Promise(resolve => { release = resolve; });
    }
    if (url.pathname === base + "/page-order" && method === "PUT" && loseOrderResponse) {
      loseOrderResponse = false;
      throw new TypeError("Simulated lost response after the real order commit");
    }
    return response;
  };
  try {
    window.confirm = () => false;
    $("edit-page-metadata").click();
    await waitFor(() => release && $("page-order-pages").children.length === before.pages.length);
    const close = $("page-metadata-dialog").querySelector("[data-close]");
    const cancel = new Event("cancel", { cancelable: true });
    $("page-metadata-dialog").dispatchEvent(cancel);
    assert(close.disabled && cancel.defaultPrevented && $("reset-page-changes").disabled, "Editor unlocked while metadata still loads.");
    const card = $("page-order-pages").firstElementChild;
    const image = card.querySelector("img"), imageError = card.querySelector(".page-order-image-error");
    const retry = card.querySelector("[data-retry-image]");
    image.dispatchEvent(new Event("error"));
    const initialSource = image.src;
    retry.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    assert(retry.disabled && image.src === initialSource, "Thumbnail retry bypassed the loading guard.");
    release(); release = null;
    await waitFor(() => !$("reset-page-changes").disabled);
    const bookmarkInput = field(0, "bookmark"), initialOrder = orderInView();
    bookmarkInput.focus();
    bookmarkInput.value = bookmark;
    bookmarkInput.setSelectionRange(3, 3);
    bookmarkInput.dispatchEvent(new Event("input", { bubbles: true }));
    assert(field(0, "bookmark") === bookmarkInput && document.activeElement === bookmarkInput
      && bookmarkInput.selectionStart === 3 && bookmarkInput.selectionEnd === 3 && bookmarkInput.value === bookmark
      && same(orderInView(), initialOrder) && !$("save-page-changes").disabled,
      "Typing a bookmark replaced its control, moved the caret, changed order or lost the saveable draft.");
    change(1, "type", secondType);
    $("page-order-pages").firstElementChild.querySelector('[data-move="1"]').click();
    image.loading = "eager";
    await waitFor(() => image.complete && image.naturalWidth > 0);
    image.dispatchEvent(new Event("error"));
    const draftOrder = orderInView();
    retry.focus();
    retry.click();
    const retrySource = image.src;
    retry.click();
    assert(retry.getAttribute("aria-disabled") === "true" && image.src === retrySource && new URL(retrySource).pathname === new URL(initialSource).pathname,
      "Thumbnail retry did not guard duplicate requests or changed its image endpoint.");
    await waitFor(() => imageError.hidden && image.complete && image.naturalWidth > 0);
    assert(document.activeElement === card && same(orderInView(), draftOrder) && field(0, "bookmark").value === bookmark
      && field(1, "type").value === secondType && !$("save-page-changes").disabled,
      "Thumbnail retry lost focus, reordered pages or discarded unsaved details.");
    close.click();
    assert($("page-metadata-dialog").open && !$("save-page-changes").disabled, "Unsaved combined draft bypassed the close guard.");
    $("save-page-changes-bottom").click();
    await waitFor(() => !!release);
    assert(close.disabled && $("reset-page-changes").disabled && $("save-page-changes-bottom").disabled && field(0, "bookmark").matches(":disabled"), "Editor unlocked during combined save.");
    image.dispatchEvent(new Event("error"));
    const savingSource = image.src;
    retry.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    assert(retry.disabled && image.src === savingSource, "Thumbnail retry bypassed the save guard.");
    release(); release = null;
    await waitFor(() => !$("reset-page-changes").disabled);
    const order = await read("/page-order"), metadata = await read("/page-metadata");
    const expected = [...before.pages]; [expected[0], expected[1]] = [expected[1], expected[0]];
    assert($("save-page-changes").disabled && $("save-page-changes-bottom").disabled && same(order.pages, expected) && metadata.pages[0].bookmark === bookmark && metadata.pages[1].type === secondType,
      "One save did not persist order, type and bookmark against their physical pages.");
    change(0, "bookmark", "Discard this draft");
    $("page-order-pages").firstElementChild.querySelector('[data-move="1"]').click();
    $("reset-page-changes").click();
    assert(field(0, "bookmark").value === "Discard this draft" && !$("save-page-changes").disabled, "Cancelled reset discarded changes.");
    window.confirm = () => true;
    $("reset-page-changes").click();
    await waitFor(() => !$("reset-page-changes").disabled);
    assert($("save-page-changes").disabled && same(orderInView(), expected) && field(0, "bookmark").value === bookmark,
      "Reset did not restore both saved order and details.");
    change(0, "bookmark", bookmark + " after uncertain save");
    $("page-order-pages").firstElementChild.querySelector('[data-move="1"]').click();
    loseOrderResponse = true;
    $("save-page-changes").click();
    await waitFor(() => !$("reset-page-changes").disabled);
    const committedOrder = await read("/page-order"), committedMetadata = await read("/page-metadata");
    assert(!loseOrderResponse && $("save-page-changes").disabled && same(committedOrder.pages, before.pages) && committedMetadata.pages[0].bookmark === bookmark + " after uncertain save",
      "Uncertain combined save lost committed values or allowed an unsafe retry.");
    $("reset-page-changes").click();
    await waitFor(() => !$("reset-page-changes").disabled);
    assert(same(orderInView(), committedOrder.pages) && field(0, "bookmark").value === committedMetadata.pages[0].bookmark && $("save-page-changes").disabled,
      "Reset failed to reconcile an uncertain commit.");
    if (readerPosition !== null) assert($("page-number").value === readerPosition, "Reset left the reader using the unconfirmed order.");
    const closed = new Promise(resolve => $("page-metadata-dialog").addEventListener("close", resolve, { once: true }));
    close.click();
    await closed;
    console.log("PASS: unified save/reset, local thumbnail retry and focus, lightweight bookmark input, physical-page identity, loading/save guards, cancelled reset and lost-response recovery.");
  } finally {
    release?.(); window.fetch = originalFetch; window.confirm = originalConfirm;
  }
})();
