// Run on a DISPOSABLE server as administrator with Missing files open.
// Requires at least 103 genuinely missing catalog entries and one readable book.
// window.panelHavenMissingRemovalCheck = { disposable: true, keepId: "readable-book-id" };
// Permanently removes missing catalog entries; never run against a real library.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const config = window.panelHavenMissingRemovalCheck;
  assert(config?.disposable === true && config.keepId, "Disposable fixtures are required.");
  assert($("maintenance-dialog").open && $("maintenance-kind").value === "missing", "Open Missing files first.");
  const originalFetch = window.fetch, originalConfirm = window.confirm;
  const report = () => originalFetch("/api/maintenance?kind=missing&pageSize=100").then(r => r.json());
  const initial = await report();
  assert(initial.total >= 103, "Use enough missing entries to exercise multiple API pages after partial removal.");
  let approve = false, loseResponse = false, deletes = 0, release = null;
  const waitFor = async predicate => {
    const deadline = Date.now() + 120000;
    while (!predicate()) {
      if (Date.now() > deadline) throw new Error("Missing cleanup check timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const start = () => { $("remove-all-missing").focus(); $("remove-all-missing").click(); };
  const settled = () => !$("maintenance-kind").disabled && document.activeElement === $("maintenance-status");
  window.confirm = () => approve;
  window.fetch = async (input, options = {}) => {
    const path = new URL(input instanceof Request ? input.url : input, location.href).pathname;
    if (options.method !== "DELETE" || !/^\/api\/books\/[^/]+$/.test(path)) return originalFetch(input, options);
    deletes++;
    const response = await originalFetch(input, options);
    assert(response.ok, "Real missing-source removal failed.");
    if (loseResponse && deletes === 1) await new Promise(resolve => { release = resolve; });
    if (loseResponse && deletes === 2) throw new TypeError("Simulated lost response after real catalog removal");
    return response;
  };
  try {
    start(); await waitFor(settled);
    assert(deletes === 0 && (await report()).total === initial.total, "Cancellation must preserve every entry.");
    approve = loseResponse = true;
    start(); await waitFor(() => release !== null);
    assert($("remove-all-missing").disabled && $("maintenance-kind").disabled && $("refresh-maintenance").disabled,
      "Cleanup must prevent overlapping operations and filter changes.");
    assert(!$("maintenance-dialog").dispatchEvent(new Event("cancel", { cancelable: true })), "Escape must not dismiss pending cleanup.");
    $("remove-all-missing").click();
    release(); release = null;
    await waitFor(settled);
    assert(deletes === 2 && !$("maintenance-error").hidden, "An uncertain result must stop the batch with a visible error, without retrying.");
    assert((await report()).total === initial.total - 2, "Earlier real removals must be retained.");
    loseResponse = false;
    start(); await waitFor(settled);
    assert((await report()).total === 0 && deletes === initial.total, "Remove all must cover every API page without skipping entries as rows disappear.");
    assert($("remove-all-missing").disabled, "Empty cleanup must be disabled.");
    assert((await originalFetch(`/api/books/${encodeURIComponent(config.keepId)}`)).status === 200, "An available comicbook must survive cleanup.");
    for (const kind of ["unavailable", "duplicates", "missing-issues"]) {
      $("maintenance-kind").value = kind;
      $("maintenance-kind").dispatchEvent(new Event("change", { bubbles: true }));
      assert($("remove-all-missing").hidden, "Bulk missing-file removal must not appear in other reports.");
    }
    console.log("PASS: cancelled cleanup, multi-page removal, busy guards, partial/lost response stop without retry, and available-book preservation.");
  } finally {
    release?.();
    window.fetch = originalFetch;
    window.confirm = originalConfirm;
  }
})();
