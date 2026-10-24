// Paste into the browser console on an idle DISPOSABLE server, never live data.
// Admin setup: scan three fresh readable CBZ fixtures on the current flat-books page.
// window.panelHavenDeletionCheck = { disposable: true, bookIds: ["delete-id", "lost-response-id", "keep-id"] };
// This PERMANENTLY DELETES the first two fixtures using the real endpoints. The third must survive.
// Reader setup: sign in as a reader with a visible book and use { disposable: true, reader: true }.
// Discard fixture roots/database afterward; independently assert first two source files are absent.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const config = window.panelHavenDeletionCheck;
  assert(config?.disposable === true, "Explicit disposable-server opt-in is required.");
  assert(!$("library-view").hidden && !document.querySelector("dialog[open]"), "Start on an idle flat-books library page.");
  const waitFor = async condition => {
    const deadline = Date.now() + 120000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("Deletion check timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const originalFetch = window.fetch;
  const select = id => {
    const checkbox = document.querySelector(`[data-book-id="${CSS.escape(id)}"]`);
    assert(checkbox, "Fixture must be on the current flat-books page.");
    checkbox.click();
  };
  const exists = async id => (await originalFetch(`/api/books/${encodeURIComponent(id)}`)).status;
  if (config.reader) {
    document.querySelector("[data-book-id]").click();
    assert($("delete-selected").hidden, "Reader must not see deletion action.");
    for (const path of ["delete-preview", "delete"]) {
      const response = await originalFetch(`/api/books/${path}`, { method: "POST", headers: { "Content-Type": "application/json", "X-PanelHaven-Request": "1" }, body: JSON.stringify({ bookIds: [document.querySelector("[data-book-id]").dataset.bookId], confirmed: true, expectedRevision: "forbidden" }) });
      assert(response.status === 403, "Reader deletion endpoints must be forbidden.");
    }
    console.log("PASS: reader action hidden and real endpoints forbidden.");
    return;
  }
  assert(config.bookIds?.length === 3 && new Set(config.bookIds).size === 3 && !$("delete-selected").hidden, "Three distinct disposable fixtures and an admin session are required.");
  const [first, second, keep] = config.bookIds;
  for (const id of config.bookIds) assert(await exists(id) === 200, "All fixtures must exist before this check.");
  let requests = 0, release = null, loseResponse = false;
  window.fetch = async (...args) => {
    const path = new URL(args[0] instanceof Request ? args[0].url : args[0], location.href).pathname;
    if (path !== "/api/books/delete") return originalFetch(...args);
    requests++;
    const response = await originalFetch(...args);
    assert(response.ok, "Real deletion failed.");
    await new Promise(resolve => { release = resolve; });
    release = null;
    if (loseResponse) throw new TypeError("Simulated lost response after real deletion");
    return response;
  };
  try {
    $("clear-selection").click();
    select(first);
    $("delete-selected").click();
    await waitFor(() => $("delete-dialog").open);
    assert(requests === 0 && await exists(first) === 200 && $("delete-items").children.length === 1,
      "Review must list the selected comicbook before any deletion request.");
    assert(document.activeElement === $("delete-dialog").querySelector('[value="cancel"]'), "Review must initially focus the safe Cancel action.");
    $("delete-dialog").querySelector('[value="cancel"]').click();
    await waitFor(() => !document.querySelector(".selection-toolbar").inert && $("books").getAttribute("aria-busy") !== "true");
    assert(requests === 0 && await exists(first) === 200 && $("selection-count").textContent.startsWith("1 "),
      "Cancelling review must preserve the selected file and catalog record.");
    $("delete-selected").click();
    await waitFor(() => $("delete-dialog").open);
    $("delete-dialog").querySelector('[value="delete"]').click();
    await waitFor(() => release !== null);
    const hash = location.hash;
    location.hash = "settings";
    await waitFor(() => location.hash === hash);
    $("clear-selection").click();
    $("delete-selected").click();
    assert(!document.querySelector("dialog[open]") && document.querySelector(".selection-toolbar").inert && $("clear-selection").disabled && requests === 1, "Deletion must stay inline while blocking selection and duplicate submission.");
    release();
    await waitFor(() => !document.querySelector(".selection-toolbar").inert && $("books").getAttribute("aria-busy") !== "true");
    assert(await exists(first) === 404 && await exists(keep) === 200, "Deleted catalog entry must disappear; unrelated fixture must survive.");
    assert($("selection-count").textContent.startsWith("0 "), "Confirmed deleted ID must be deselected.");
    assert(!document.querySelector("dialog[open]") && document.activeElement === $("management-status"), "Completion must remain inline with stable focus.");
    select(second);
    loseResponse = true;
    $("delete-selected").click();
    await waitFor(() => $("delete-dialog").open);
    assert(requests === 1, "A previous confirmation must not authorize a new selection.");
    $("delete-dialog").querySelector('[value="delete"]').click();
    await waitFor(() => release !== null);
    release();
    await waitFor(() => $("management-status").textContent.includes("Could not confirm deletion"));
    assert(await exists(second) === 404 && await exists(keep) === 200, "Lost response must follow real deletion, preserving unrelated book.");
    assert($("selection-count").textContent.startsWith("1 ") && requests === 2, "Unknown outcome must retain selection and never retry.");
    assert(!document.querySelector("dialog[open]") && document.activeElement === $("management-status"), "Failure must remain inline with stable focus.");
    assert($("management-status").textContent.includes("Refresh and inspect"), "Unknown outcome must remain visible inline.");
    console.log("PASS: reviewed real deletion, cancel preservation, busy guards, inline status/focus, deselection, unrelated book preservation and lost-response no-retry.");
  } finally {
    release?.();
    window.fetch = originalFetch;
  }
})();
