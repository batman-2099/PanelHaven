// Run in the browser console in an opened, named series stack with at least one visible book.
// Default: read-only API calls; checks picker, cancellation, focus and selection preservation.
// Real writes require a DISPOSABLE server, an empty manual list, and >100 matching catalog books:
// window.panelHavenSeriesListCheck = { disposable: true, listId: "empty-manual-list-id",
//   scope: { series: "Fixture Series", location: "library", publisher: null },
//   bookIds: [/* every expected matching book ID, including issues beyond the current page */] };
// Open that exact series/area/publisher scope first. The check leaves the populated list intact.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const waitFor = async condition => {
    const deadline = Date.now() + 20000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("Timed out waiting for series-list check.");
      await new Promise(resolve => setTimeout(resolve, 50));
    }
  };
  assert(!$("library-view").hidden && !document.querySelector("dialog[open]") && !$("add-series-to-list").hidden && !$("add-series-to-list").disabled,
    "Start in an idle, opened named series stack.");
  const first = $("books").querySelector("[data-book-id]");
  assert(first, "At least one visible comicbook is required.");
  const selectedFirst = first.checked;
  if (!selectedFirst) first.click();
  const selection = () => JSON.stringify({ count: $("selection-count").textContent,
    ids: [...$("books").querySelectorAll("[data-book-id]:checked")].map(node => node.dataset.bookId).sort() });
  const view = () => JSON.stringify({ hash: location.hash, group: $("group-label").textContent,
    page: $("library-page").textContent, list: $("reading-list").value, search: $("search").value });
  const initialSelection = selection(), initialView = view();
  const originalFetch = window.fetch;
  const listsResponse = await originalFetch("/api/lists");
  assert(listsResponse.ok, "Reading lists must load.");
  const lists = await listsResponse.json();
  const config = window.panelHavenSeriesListCheck;
  let writes = 0, submitted, release, hold = false, loseResponse = false, result;
  window.fetch = async (input, options) => {
    const path = new URL(input instanceof Request ? input.url : input, location.href).pathname;
    if (!/^\/api\/lists\/[^/]+\/series$/.test(path) || options?.method !== "POST") return originalFetch(input, options);
    writes++;
    submitted = JSON.parse(options.body);
    const response = await originalFetch(input, options);
    assert(response.ok, "Real series-list write must succeed.");
    result = await response.clone().json();
    if (hold) { hold = false; await new Promise(resolve => { release = resolve; }); release = null; }
    if (loseResponse) { loseResponse = false; throw new TypeError("Simulated lost response after real commit"); }
    return response;
  };
  const open = async () => {
    $("add-series-to-list").focus();
    $("add-series-to-list").click();
    await waitFor(() => $("series-list-dialog").open && $("series-list-form").getAttribute("aria-busy") === "false");
  };
  const close = async () => {
    $("close-series-list").click();
    await waitFor(() => !$("series-list-dialog").open);
    await new Promise(resolve => setTimeout(resolve, 0));
  };
  try {
    await open();
    const options = [...$("series-list-target").options].filter(option => option.value);
    assert(JSON.stringify(options.map(option => option.value).sort()) === JSON.stringify(lists.filter(list => list.kind === "manual").map(list => list.id).sort()),
      "Picker must contain all and only manual lists.");
    assert($("series-list-empty").hidden === !!options.length, "No-list guidance must appear only when no manual lists exist.");
    await close();
    assert(writes === 0 && selection() === initialSelection && view() === initialView, "Cancel must not write, clear selection or navigate.");
    assert(document.activeElement === $("add-series-to-list"), "Native dialog close must restore focus to its trigger.");
    console.log("PASS: manual-only grouped picker, empty state, cancel, focus and preserved selection.");
    await open();
    $("create-series-smart-list").click();
    await waitFor(() => $("list-dialog").open && !$("smart-list-controls").disabled);
    assert(!$("series-list-dialog").open && $("list-kind").value === "smart" && $("save-list").disabled,
      "Creating a series smart list must open an unsaved smart draft that requires preview.");
    if (config?.scope) {
      const rules = [...$("list-rules").querySelectorAll(".list-rule")].map(row => ({
        field: row.querySelector(".rule-field").value, operator: row.querySelector(".rule-operator").value,
        value: row.querySelector(".rule-value").value
      }));
      const { series, publisher } = config.scope;
      assert(rules[0]?.field === "Series" && rules[0].operator === "equals" && rules[0].value === series,
        "The draft must match the whole series, not only existing issue IDs.");
      assert(publisher === null ? rules.length === 1 : rules.length === 2 && rules[1].field === "Publisher"
        && rules[1].operator === (publisher === "" ? "isEmpty" : "equals") && rules[1].value === publisher,
        "The draft must preserve scoped, unscoped and blank-publisher matching.");
    }
    $("preview-list").click();
    await waitFor(() => !$("save-list").disabled);
    $("list-dialog").querySelector("[data-close]").click();
    await waitFor(() => !$("list-dialog").open);
    assert(document.activeElement === $("add-series-to-list") && selection() === initialSelection && view() === initialView,
      "Cancelling the smart draft must restore series focus without changing selection or view.");
    console.log("PASS: series smart draft, real preview, cancellation and focus restoration.");
    if (!config) return;
    assert(config.disposable === true && config.listId && config.scope && config.bookIds?.length > 100 && new Set(config.bookIds).size === config.bookIds.length,
      "Real writes require an explicit disposable fixture with >100 distinct expected book IDs.");
    const listPath = `/api/lists/${encodeURIComponent(config.listId)}`;
    const getList = async () => {
      const response = await originalFetch(listPath);
      assert(response.ok, "Disposable list must exist.");
      return response.json();
    };
    const before = await getList();
    assert(before.kind === "manual" && before.bookIds.length === 0, "Use an empty disposable manual list.");
    await open();
    $("series-list-target").value = config.listId;
    assert($("series-list-target").value === config.listId, "Fixture list must be available in the picker.");
    hold = true;
    $("submit-series-list").click();
    await waitFor(() => release);
    assert(["series", "location", "publisher"].every(key => submitted[key] === config.scope[key]), "Submitted scope must exactly match series, area and nullable/exact publisher.");
    assert(result.added === config.bookIds.length, "The server must add the entire >100-issue series, not the current page.");
    const cancel = new Event("cancel", { cancelable: true });
    $("series-list-dialog").dispatchEvent(cancel);
    $("close-series-list").click();
    assert(cancel.defaultPrevented && $("series-list-dialog").open && $("close-series-list").disabled && $("submit-series-list").disabled && $("create-series-smart-list").disabled,
      "Pending mutations must prevent Escape, close and duplicate submit.");
    release();
    await waitFor(() => !release && $("series-list-form").getAttribute("aria-busy") === "false" && !$("submit-series-list").disabled);
    const added = await getList();
    assert(JSON.stringify([...added.bookIds].sort()) === JSON.stringify([...config.bookIds].sort()), "Real list membership must match the full expected scope.");
    assert(selection() === initialSelection && view() === initialView, "Successful addition must preserve selection and view.");
    loseResponse = true;
    $("submit-series-list").click();
    await waitFor(() => !$("submit-series-list").disabled && $("series-list-status").textContent.includes("outcome unknown"));
    assert(writes === 2 && result.added === 0, "Unknown outcome must not trigger automatic retries or duplicate entries.");
    $("submit-series-list").click();
    await waitFor(() => $("series-list-form").getAttribute("aria-busy") === "false" && !$("submit-series-list").disabled);
    assert(writes === 3 && result.added === 0 && JSON.stringify((await getList()).bookIds) === JSON.stringify(added.bookIds),
      "Explicit retry must be idempotent and preserve issue order.");
    assert(selection() === initialSelection && view() === initialView, "Errors and explicit retry must preserve selection and view.");
    await close();
    console.log("PASS: real >100-issue addition, exact scope, pending guards, lost-response feedback and idempotent explicit retry.");
  } finally {
    release?.();
    window.fetch = originalFetch;
    if ($("series-list-dialog").open && !$("close-series-list").disabled) await close();
    if ($("list-dialog").open) $("list-dialog").querySelector("[data-close]").click();
    const current = $("books").querySelector(`[data-book-id="${CSS.escape(first.dataset.bookId)}"]`);
    if (!selectedFirst && current?.checked) current.click();
  }
})();
