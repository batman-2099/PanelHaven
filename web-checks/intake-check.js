// Run in the browser console, signed in as admin on an idle DISPOSABLE server.
// Configure a writable Library root, Unsorted, and a real server-side Comic Vine key.
// Scan a disposable CBZ/ZIP or real CBR fixture; keep its original SHA-256 separately.
// Put the fixture on the current flat-books page. Use a fresh fixture for each run.
// window.panelHavenIntakeCheck = { disposable: true, bookId: "fixture-id", destinationRoot: 0,
//   issueId: 1109530, volumeId: 164287, originalSha256: "64 hex digits", converted: false };
// Then paste this file. It saves/embeds real provider metadata, creates directories,
// renames/moves the fixture and, for CBR, converts and deletes the backed-up original.
// Matching responses alone simulate provider failure/empty/tied results. Preparation,
// processing, stale-write refusal, status and original download all use the REAL server.
// A real concurrent catalog edit and a lost successful response are injected below.
// Discard the entire disposable server state and roots afterward. Never use live sources.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const config = window.panelHavenIntakeCheck;
  assert(config?.disposable === true && typeof config.bookId === "string" && Number.isInteger(config.destinationRoot)
    && Number.isInteger(config.issueId) && Number.isInteger(config.volumeId) && /^[a-f0-9]{64}$/i.test(config.originalSha256)
    && typeof config.converted === "boolean", "Explicit disposable fixture, provider IDs, original SHA-256 and conversion expectation are required.");
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
    window.confirm = () => { throw new Error("Candidate processing must not open another confirmation step."); };
    $("clear-selection").click();
    $("search").value = "";
    $("reading-list").value = "";
    $("publisher").value = $("book-rating").value = "";
    $("book-status").value = $("book-availability").value = "all";
    $("group-by").value = "none";
    $("stack-by").value = "none";
    $("library-location").value = "unsorted";
    change("library-location");
    await waitFor(() => location.hash === "#unsorted" && $("books").getAttribute("aria-busy") !== "true" && $("library-title").textContent === "Unsorted intake");
    $("process-unsorted").click();
    await waitFor(() => $("intake-dialog").open && !$("intake-hints").disabled);
    assert($("intake-scope").textContent.includes("current page only"), "No-selection scope must explicitly say current page.");
    close("intake-dialog");
    await waitFor(() => !$("intake-dialog").open);
    const selection = document.querySelector(`[data-book-id="${CSS.escape(config.bookId)}"]`);
    assert(selection, "Put the disposable fixture on the current flat-books page before running.");
    selection.click();
    $("process-unsorted").click();
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
    $("intake-manual").click();
    await waitFor(() => $("comic-vine-dialog").open && !$("comic-vine-series").disabled);
    close("comic-vine-dialog"); close("details-dialog");
    await waitFor(() => document.activeElement === $("intake-manual"));
    const accept = $("intake-candidates").querySelector("button");
    holdPrepare = true;
    accept.click();
    await waitFor(() => !!release);
    assert(!$("intake-cancel").hidden && $("intake-root").disabled, "Read-only preparation was not cancellable or allowed destination edits.");
    $("intake-cancel").click(); release(); release = null;
    await new Promise(resolve => setTimeout(resolve, 50));
    assert(!mutations.some(item => item.path.endsWith("/intake/process")), "Cancelled preparation started processing.");
    accept.click();
    await waitFor(() => !accept.disabled && processStatuses.length === 1);
    assert(processStatuses[0] === 409 && (await readJson(`${base}/metadata`)).fields.Title === "Concurrent disposable edit", "Real stale-revision refusal did not protect the concurrent edit.");
    assert((await readJson(base)).location === originalBook.location && $("intake-original").hidden, "Stale processing moved the source or created a committed backup.");
    holdProcess = true;
    accept.click();
    await waitFor(() => !!release);
    assert($("intake-queue").disabled && $("intake-root").disabled && $("intake-cancel").hidden, "Mutation permits queue changes, destination changes or cancellation.");
    close("intake-dialog");
    assert($("intake-dialog").open, "Mutation allowed dismissal.");
    location.hash = "#library";
    await waitFor(() => location.hash === "#unsorted", "Navigation was not guarded during processing.");
    release(); release = null;
    await waitFor(() => !$("intake-dialog").open && !$("intake-queue").options.length && processStatuses.length === 2);
    const status = await readJson(`${base}/intake-processing`), saved = await readJson(`${base}/metadata`), moved = await readJson(base);
    assert(status.state === "completed" && status.converted === config.converted && status.originalAvailable && !status.canRecover, "Real persisted processing did not complete with expected format and backup.");
    assert(moved.id === originalBook.id && moved.location !== originalBook.location && moved.currentPage === originalBook.currentPage
      && moved.completed === originalBook.completed, "Processing lost comic identity/progress or did not move the comicbook.");
    assert(saved.fields.Series === series && saved.fields.Number === proposal.fields.Number
      && JSON.stringify(saved.custom) === JSON.stringify(originalMetadata.custom), "Provider metadata or original custom fields were lost.");
    for (const [field, value] of Object.entries(originalMetadata.fields)) {
      if (field !== "Volume" && field !== "Title" && !proposal.fields[field]?.trim()) assert(saved.fields[field] === value, `Omitted provider field was erased: ${field}`);
    }
    assert(!$("metadata-dialog").open && !$("organization-dialog").open && document.activeElement === $("library-status"),
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
    await waitFor(() => $("intake-dialog").open && !$("intake-refresh").disabled && !$("intake-original").hidden);
    assert($("intake-form").hidden && $("intake-root").hidden && $("intake-outcome").textContent.includes(status.destination),
      "Reopened processing history exposed new processing or lost the completed outcome.");
    const reopenedBackup = await originalFetch($("intake-original").href);
    assert(reopenedBackup.ok && processStatuses.length === 2, "Reopened Book details lost original access or reprocessed the book.");
    const digest = [...new Uint8Array(await crypto.subtle.digest("SHA-256", await reopenedBackup.arrayBuffer()))].map(byte => byte.toString(16).padStart(2, "0")).join("");
    assert(digest === config.originalSha256.toLowerCase(), "Downloaded backup does not match original fixture bytes.");
    $("intake-refresh").click();
    await waitFor(() => !$("intake-refresh").disabled);
    assert($("intake-dialog").open && !$("intake-original").hidden, "Viewing completed history should not auto-close it.");
    close("intake-dialog");
    await waitFor(() => !$("intake-dialog").open && document.activeElement === $("intake-history"));
    close("details-dialog");
    console.log("PASS: real prepare/process, stale rejection, cancelled preflight, guarded mutation, lost-response status reconciliation, identity/progress, metadata preservation, destination, verified-original download and reopened Book details history. Matching only was simulated; no processing endpoint was mocked. Recovery needs a separate interrupted-operation fixture.");
  } finally {
    release?.();
    window.fetch = originalFetch;
    window.confirm = originalConfirm;
  }
})();
