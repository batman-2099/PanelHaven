// Run in the browser console, signed in as admin on an idle DISPOSABLE server.
// Configure a writable Library root, the tested source location, and a real server-side Comic Vine key.
// Scan a disposable CBZ/ZIP or real CBR fixture with no earlier processing operation.
// Put the fixture on the current flat-books page. Use a fresh fixture for each run.
// window.panelHavenIntakeCheck = { disposable: true, bookId: "fixture-id", destinationRoot: 0,
//   issueId: 1109530, volumeId: 164287, converted: false, location: "unsorted" };
// Then paste this file. It saves/embeds real provider metadata, creates directories,
// renames/moves the fixture and, for CBR, converts and permanently deletes the original.
// Matching responses alone simulate provider failure/empty/tied results. Preparation,
// processing, stale-write refusal and status use the REAL server.
// A real concurrent catalog edit and a lost successful response are injected below.
// Discard the entire disposable server state and roots afterward. Never use live sources.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const config = window.panelHavenIntakeCheck;
  assert(config?.disposable === true && typeof config.bookId === "string" && Number.isInteger(config.destinationRoot)
    && Number.isInteger(config.issueId) && Number.isInteger(config.volumeId)
    && typeof config.converted === "boolean", "Explicit disposable fixture, provider IDs and conversion expectation are required.");
  const area = config.location || "unsorted", route = area === "unsorted" ? "#unsorted" : "#library";
  assert(["library", "archived", "unsorted"].includes(area), "Choose a configured source location.");
  assert(!$('library-view').hidden && !document.querySelector('dialog[open]'), "Start on an idle library page.");
  const waitFor = async (condition, message) => {
    const deadline = Date.now() + 180000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error(message || "Intake workflow timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const change = id => $(id).dispatchEvent(new Event("change", { bubbles: true }));
  const input = (id, value) => { $(id).value = value; $(id).dispatchEvent(new Event("input", { bubbles: true })); };
  const close = id => document.querySelector(`[data-close="${id}"]`).click();
  const originalFetch = window.fetch, originalConfirm = window.confirm;
  const base = `/api/books/${encodeURIComponent(config.bookId)}`;
  const readJson = async path => { const response = await originalFetch(path); assert(response.ok, `Read failed: ${path}`); return response.json(); };
  const originalBook = await readJson(base), originalMetadata = await readJson(`${base}/metadata`);
  await readJson(`${base}/intake`);
  assert((await readJson(`${base}/intake-processing`)).state === "none", "Use a fresh, unprocessed fixture.");
  const proposal = await readJson(`/api/comic-vine/issues/${config.issueId}/metadata?volumeId=${config.volumeId}`);
  const series = proposal.fields.Series;
  assert(series, "The real provider issue must have series metadata.");
  const candidate = { issueId: config.issueId, volumeId: config.volumeId, series, number: proposal.fields.Number,
    title: proposal.fields.Title, publisher: proposal.fields.Publisher, startYear: proposal.fields.Volume || "unknown",
    coverDate: "See provider", url: proposal.url, score: 80 };
  const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
  const mutations = [], processStatuses = [];
  let mode = "failure", release, matchRequests = 0, holdPrepare = false, conflictOnce = true, holdProcess = false, loseSuccess = true;
  window.fetch = async (...args) => {
    const url = new URL(args[0] instanceof Request ? args[0].url : args[0], location.href);
    const method = args[1]?.method || (args[0] instanceof Request ? args[0].method : "GET");
    if (url.pathname.endsWith("/intake/matches")) {
      matchRequests++;
      if (mode === "failure") return json({ error: "Disposable provider failure" }, 503);
      if (mode === "hold") await new Promise(resolve => { release = resolve; });
      return json({ candidates: mode === "empty" ? [] : [candidate, { ...candidate, title: "Tied provider suggestion" }], warnings: ["Simulated matching only; processing uses real provider metadata."] });
    }
    if (["POST", "PUT", "DELETE", "PATCH"].includes(method)) mutations.push({ path: url.pathname, body: args[1]?.body });
    if (url.pathname === `${base}/intake/prepare`) {
      const response = await originalFetch(...args);
      if (holdPrepare) { holdPrepare = false; await new Promise(resolve => { release = resolve; }); }
      return response;
    }
    if (url.pathname === `${base}/intake/process`) {
      if (conflictOnce) {
        conflictOnce = false;
        const edit = await originalFetch(`${base}/metadata`, { method: "PUT", headers: { "Content-Type": "application/json", "X-PanelHaven-Request": "1" },
          body: JSON.stringify({ fields: { Title: "Concurrent disposable edit" }, custom: {} }) });
        assert(edit.ok, "Could not create real concurrent metadata edit.");
      }
      if (holdProcess) { holdProcess = false; await new Promise(resolve => { release = resolve; }); }
      const response = await originalFetch(...args);
      processStatuses.push(response.status);
      if (response.ok && loseSuccess) { loseSuccess = false; throw new TypeError("Simulated lost response after real processing completed"); }
      return response;
    }
    return originalFetch(...args);
  };
  try {
    window.confirm = () => { throw new Error("Accept must process directly without a browser confirmation."); };
    $("clear-selection").click();
    $("search").value = "";
    $("reading-list").value = "";
    $("publisher").value = $("book-rating").value = "";
    $("book-status").value = $("book-availability").value = "all";
    $("group-by").value = "none";
    $("stack-by").value = "none";
    $("library-location").value = area;
    change("library-location");
    await waitFor(() => location.hash === route && $("books").getAttribute("aria-busy") !== "true" && document.querySelector(`[data-book-id="${CSS.escape(config.bookId)}"]`));
    if (area === "unsorted") {
      $("process-unsorted").click();
      await waitFor(() => $("intake-dialog").open && !$("intake-hints").disabled);
      assert($("intake-scope").textContent.includes("current page only"), "No-selection scope must explicitly say current page.");
      close("intake-dialog");
      await waitFor(() => !$("intake-dialog").open);
    }
    const selection = document.querySelector(`[data-book-id="${CSS.escape(config.bookId)}"]`);
    assert(selection, "Put the disposable fixture on the current flat-books page before running.");
    selection.click();
    document.querySelector(".selection-tools").open = true;
    assert(!$("process-selected").hidden && !$("process-selected").disabled, "Selected processing is unavailable.");
    $("process-selected").click();
    await waitFor(() => !$("intake-hints").disabled && $("intake-queue").value === config.bookId);
    assert($("intake-queue").options.length === 1 && $("intake-scope").textContent.includes("selected"), "Selected queue did not isolate the fixture.");
    assert([...$("intake-root").options].every(option => /^Library \d+$/.test(option.textContent)), "Non-library destination was offered.");
    $("intake-root").value = String(config.destinationRoot);
    change("intake-root");
    assert($("intake-root").value === String(config.destinationRoot), "Configured destination is not a regular library.");
    input("intake-series", series);
    $("intake-match").click();
    await waitFor(() => !$("intake-match").disabled && $("intake-status").textContent.includes("Disposable provider failure"));
    assert(!mutations.length, "Provider failure mutated a comicbook.");
    mode = "empty";
    $("intake-match").click();
    await waitFor(() => $("intake-status").textContent.includes("No candidates"));
    assert(!mutations.length, "Empty provider response mutated a comicbook.");
    mode = "hold";
    $("intake-match").click();
    await waitFor(() => !!release);
    $("intake-cancel").click();
    release(); release = null;
    await new Promise(resolve => setTimeout(resolve, 50));
    assert(!$("intake-candidates").children.length && !mutations.length, "Cancelled search populated candidates or mutated data.");
    mode = "success";
    $("intake-match").click();
    await waitFor(() => $("intake-candidates").querySelectorAll("button").length === 2 && !$("intake-match").disabled);
    assert($("intake-candidates").textContent.includes("Rank score 80/100") && !mutations.length, "Tied ranking automatically processed a candidate.");
    const accept = $("intake-candidates").querySelector("button");
    holdPrepare = true;
    accept.click();
    await waitFor(() => !!release);
    assert(!$("intake-cancel").hidden && $("intake-root").disabled, "Read-only preparation was not cancellable or allowed destination edits.");
    assert(!$("intake-progress").hidden && !$("intake-progress").querySelector("progress").hasAttribute("value"), "Preparing must show indeterminate progress, not a fabricated percentage.");
    $("intake-cancel").click(); release(); release = null;
    await new Promise(resolve => setTimeout(resolve, 50));
    assert(!mutations.some(item => item.path.endsWith("/intake/process")), "Cancelled preparation started processing.");
    assert($("intake-progress").hidden, "Cancelled preparation left processing feedback active.");
    accept.click();
    await waitFor(() => !accept.disabled && processStatuses.length === 1);
    assert(processStatuses[0] === 409 && (await readJson(`${base}/metadata`)).fields.Title === "Concurrent disposable edit", "Real stale-revision refusal did not protect the concurrent edit.");
    assert((await readJson(base)).location === originalBook.location, "Stale processing moved the source.");
    holdProcess = true;
    accept.click();
    await waitFor(() => !!release);
    assert($("intake-preview").hidden, "Acceptance still requires a separate review step.");
    accept.click();
    assert($("intake-queue").disabled && $("intake-root").disabled && $("intake-cancel").hidden, "Mutation permits queue changes, destination changes or cancellation.");
    assert(!$("intake-progress").hidden && $("intake-progress").getBoundingClientRect().height > 0, "Active processing lost its visible progress feedback.");
    close("intake-dialog");
    assert($("intake-dialog").open, "Mutation allowed dismissal.");
    location.hash = "#settings";
    await waitFor(() => location.hash === route, "Navigation was not guarded during processing.");
    release(); release = null;
    await waitFor(() => !$("intake-dialog").open && !$("intake-queue").options.length && processStatuses.length === 2);
    assert($("intake-progress").hidden, "Completed processing left the progress indicator active.");
    const status = await readJson(`${base}/intake-processing`), saved = await readJson(`${base}/metadata`), moved = await readJson(base);
    assert(status.state === "completed" && status.converted === config.converted && !status.canRecover, "Real persisted processing did not complete.");
    assert(moved.id === originalBook.id && moved.location !== originalBook.location && moved.currentPage === originalBook.currentPage
      && moved.completed === originalBook.completed, "Processing lost comic identity/progress or did not move the comicbook.");
    assert(saved.fields.Series === series && saved.fields.Number === proposal.fields.Number
      && JSON.stringify(saved.custom) === JSON.stringify(originalMetadata.custom), "Provider metadata or original custom fields were lost.");
    for (const [field, value] of Object.entries(originalMetadata.fields)) {
      if (field !== "Volume" && field !== "Title" && !proposal.fields[field]?.trim()) assert(saved.fields[field] === value, `Omitted provider field was erased: ${field}`);
    }
    assert($("metadata-view").hidden && !$("organization-dialog").open && document.activeElement === $("library-page"),
      "Successful acceptance did not close the window and return focus to the library.");
    assert(!mutations.some(item => item.path === "/api/books/metadata" || item.path === "/api/file-operations"), "Candidate acceptance used obsolete catalog-only/manual-move endpoints.");
    assert(matchRequests === 4 && mutations.filter(item => item.path.endsWith("/intake/prepare")).length === 3, "Unexpected automatic matching or preparation retry.");
    assert(!$("intake-dialog").open, "Completed acceptance reopened the queue.");
    location.hash = `reader/${encodeURIComponent(config.bookId)}?location=library`;
    await waitFor(() => !$("reader-view").hidden && !$("reader-details").disabled);
    $("reader-details").click();
    await waitFor(() => $("details-dialog").open && !$("intake-history").hidden);
    $("intake-history").focus();
    $("intake-history").click();
    await waitFor(() => $("intake-dialog").open && !$("intake-refresh").disabled);
    assert($("intake-form").hidden && $("intake-root").hidden && $("intake-outcome").textContent.includes(status.destination),
      "Reopened processing history exposed new processing or lost the completed outcome.");
    assert(processStatuses.length === 2, "Reopening history reprocessed the book.");
    $("intake-refresh").click();
    await waitFor(() => !$("intake-refresh").disabled);
    assert($("intake-dialog").open, "Completed processing history must remain open after refresh.");
    close("intake-dialog");
    await waitFor(() => !$("intake-dialog").open && document.activeElement === $("intake-history"));
    close("details-dialog");
    $("close-reader").click();
    await waitFor(() => !$("library-view").hidden);
    $("library-location").value = "library";
    change("library-location");
    await waitFor(() => !$("library-view").hidden && $("books").getAttribute("aria-busy") !== "true" && document.querySelector(`[data-book-id="${CSS.escape(config.bookId)}"]`));
    document.querySelector(`[data-book-id="${CSS.escape(config.bookId)}"]`).click();
    $("process-selected").click();
    await waitFor(() => $("intake-dialog").open && !$("intake-hints").disabled);
    assert($("intake-outcome").textContent.startsWith("Previous operation:") && $("selection-count").textContent.startsWith("1 "),
      "Earlier processing blocked a new queue or cleared its selection.");
    $("intake-match").click();
    await waitFor(() => !$("intake-match").disabled && $("intake-candidates").querySelector("button"));
    $("intake-root").add(new Option("Invalid test destination", "999"));
    $("intake-root").value = "999";
    change("intake-root");
    $("intake-candidates").querySelector("button").click();
    await waitFor(() => !$("intake-candidates").querySelector("button").disabled && $("intake-status").textContent.includes("Persisted status checked"));
    assert($("intake-dialog").open && processStatuses.length === 2 && (await readJson(`${base}/intake-processing`)).revision === status.revision,
      "Rejected preparation mistook an earlier completed operation for new success.");
    $("intake-root").value = String(config.destinationRoot);
    change("intake-root");
    loseSuccess = true;
    $("intake-candidates").querySelector("button").click();
    await waitFor(() => !$("intake-dialog").open && processStatuses.length === 3);
    assert((await readJson(`${base}/intake-processing`)).revision !== status.revision,
      "Reprocessing did not create a new completed operation.");
    console.log(`PASS (${area}): selected processing, real prepare/process, stale rejection, cancelled preflight, guarded mutation, lost-response reconciliation, identity/progress, metadata preservation, history and repeat processing. Matching only was simulated; processing endpoints were real. Recovery needs a separate interrupted-operation fixture.`);
  } finally {
    release?.();
    window.fetch = originalFetch;
    window.confirm = originalConfirm;
  }
})();
