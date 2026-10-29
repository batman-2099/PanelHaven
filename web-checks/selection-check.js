// Run in the browser console on an idle library with at least two readable books.
// For held real list-write regressions, use only a DISPOSABLE server and set:
// window.panelHavenSelectionCheck = { disposable: true, bookIds: ["A", "B"], listId: "manual-list-id" };
// Both readable fixtures must be on the current page and in that manual list. The check deletes the list.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const waitFor = async condition => {
    const deadline = Date.now() + 15000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("Timed out waiting for selection check.");
      await new Promise(resolve => setTimeout(resolve, 50));
    }
  };
  assert(!$("library-view").hidden && !document.querySelector("dialog[open]"), "Start on the idle library page.");
  $("clear-selection").click();
  $("group-by").value = "none";
  $("stack-by").value = "none";
  $("stack-by").dispatchEvent(new Event("change", { bubbles: true }));
  await waitFor(() => $("books").getAttribute("aria-busy") !== "true");
  const cards = [...$("books").querySelectorAll(".book-card:not(.group-card)")];
  assert(cards.length >= 2, "At least two books are required.");
  const first = cards[0].querySelector("input[type=checkbox]");
  const second = cards[1].querySelector("input[type=checkbox]");
  const cover = cards[1].querySelector(".cover-button");
  try {
    first.click();
    $("select-page").click();
    assert(cards.every(card => card.querySelector("input[type=checkbox]").checked), "Select page must select every book on this page.");
    $("select-page").click();
    assert(cards.every(card => !card.querySelector("input[type=checkbox]").checked) && document.querySelector(".selection-toolbar").hidden, "Clicking again must clear selection and leave selection mode.");
    first.click();
    cover.click();
    assert(first.checked && second.checked && !$("details-dialog").open && !$("library-view").hidden, "Cover click must add to selection, not open a book.");
    cover.click();
    assert(first.checked && !second.checked, "Clicking a selected cover must deselect it.");
    first.click();
    assert(document.querySelector(".selection-toolbar").hidden, "Deselecting the last book must leave selection mode.");
    cover.click();
    await waitFor(() => !$("reader-view").hidden && $("page-stage").querySelector(".reader-page"));
    assert(location.hash === `#reader/${encodeURIComponent(second.dataset.bookId)}?location=${$("library-location-filter").hidden ? "unsorted" : $("library-location").value}` && !second.checked && !$("details-dialog").open, "Outside selection mode, a cover must open the reader directly and retain its area.");
    console.log("PASS: cover selection mode, toggling, and direct reader navigation.");
  } finally {
    if ($("details-dialog").open) $("details-dialog").querySelector("[data-close]").click();
    if (location.hash.startsWith("#reader/")) {
      $("close-reader").click();
      await waitFor(() => !$("library-view").hidden);
    }
    $("clear-selection").click();
  }
  const config = window.panelHavenSelectionCheck;
  if (!config) return;
  assert(config.disposable === true && config.bookIds?.length === 2 && new Set(config.bookIds).size === 2 && config.listId,
    "Two distinct disposable fixtures and a disposable manual list are required.");
  const [a, b] = config.bookIds;
  const originalFetch = window.fetch;
  const originalConfirm = window.confirm;
  const listPath = `/api/lists/${encodeURIComponent(config.listId)}`;
  const idle = () => $("books").getAttribute("aria-busy") !== "true";
  const checkbox = id => $("books").querySelector(`[data-book-id="${CSS.escape(id)}"]`);
  const select = id => { assert(checkbox(id), "Fixture must be visible on this page."); checkbox(id).click(); };
  const changeList = async id => {
    $("reading-list").value = id;
    $("reading-list").dispatchEvent(new Event("change", { bubbles: true }));
    await waitFor(idle);
  };
  const list = await originalFetch(listPath).then(response => response.json());
  assert(list.kind === "manual" && list.bookIds.includes(a) && list.bookIds.includes(b),
    "The disposable manual list must contain both fixtures.");
  assert([...$("reading-list").options].some(option => option.value === config.listId), "Reload the page after creating the disposable list.");
  let heldPath, heldMethod, release, submitted;
  window.fetch = async (input, options) => {
    const path = new URL(input instanceof Request ? input.url : input, location.href).pathname;
    const hold = path === heldPath && options?.method === heldMethod;
    if (hold) { heldPath = null; submitted = options.body && JSON.parse(options.body); }
    const response = await originalFetch(input, options);
    if (hold) {
      assert(response.ok, "Real list mutation failed.");
      await new Promise(resolve => { release = resolve; });
      release = null;
    }
    return response;
  };
  try {
    await changeList(config.listId);
    select(a);
    heldPath = `${listPath}/books`; heldMethod = "DELETE";
    $("remove-list-books").click();
    await waitFor(() => release);
    select(b);
    assert(!checkbox(b).disabled, "Pending list writes must not disable unrelated selection.");
    release();
    await waitFor(() => !release && idle() && !$("remove-list-books").disabled);
    assert(submitted.bookIds.length === 1 && submitted.bookIds[0] === a && checkbox(b).checked && $("selection-count").textContent.startsWith("2 "),
      "Removing A must preserve the newer A+B selection.");
    assert(!(await originalFetch(listPath).then(response => response.json())).bookIds.includes(a), "The real removal must persist.");

    $("clear-selection").click();
    await changeList("");
    select(a);
    $("add-to-list").value = config.listId;
    $("add-to-list").dispatchEvent(new Event("change", { bubbles: true }));
    heldPath = `${listPath}/books`; heldMethod = "POST";
    $("add-list-books").click();
    await waitFor(() => release);
    $("clear-selection").click();
    select(b);
    release();
    await waitFor(() => !release && idle() && !$("add-list-books").disabled);
    assert(submitted.bookIds.length === 1 && submitted.bookIds[0] === a && checkbox(b).checked && $("selection-count").textContent.startsWith("1 "),
      "Adding A must preserve the replacement selection B.");
    const reordered = await originalFetch(listPath).then(response => response.json());
    assert(reordered.bookIds.includes(a), "The real addition must persist.");

    await changeList(config.listId);
    $("book-sort").value = "list";
    $("book-sort").dispatchEvent(new Event("change", { bubbles: true }));
    await waitFor(idle);
    select(a);
    const direction = reordered.bookIds.indexOf(a) ? -1 : 1;
    heldPath = `${listPath}/order`; heldMethod = "PUT";
    checkbox(a).closest(".book-card").querySelector(`[data-move="${direction}"]`).click();
    await waitFor(() => release);
    $("clear-selection").click();
    select(b);
    release();
    await waitFor(() => !release && idle() && !$("delete-list").disabled);
    assert(checkbox(b).checked && $("selection-count").textContent.startsWith("1 "), "Reordering must preserve newer selections.");

    heldPath = listPath; heldMethod = "DELETE";
    window.confirm = message => message.startsWith("Delete reading list") || originalConfirm(message);
    $("delete-list").click();
    await waitFor(() => release);
    await changeList("");
    select(b);
    release();
    await waitFor(() => !release && ![...$("reading-list").options].some(option => option.value === config.listId));
    assert($("reading-list").value === "" && checkbox(b).checked && $("selection-count").textContent.startsWith("1 "),
      "Deleting a previous list must preserve the newer browsing context and selection.");
    console.log("PASS: held real membership, order, and list-deletion responses preserve newer selections.");
  } finally {
    release?.();
    window.fetch = originalFetch;
    window.confirm = originalConfirm;
  }
})();
