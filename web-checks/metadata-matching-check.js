// Run in an admin browser console on an idle DISPOSABLE server, with a fixture's Book details open.
// window.panelHavenMetadataMatchingCheck = { disposable: true, bookId: "fixture-id" };
// Provider responses are simulated; hints, catalog previews, conflicts and saves use the real server.
// This changes fixture metadata. Discard the disposable server and its files afterward.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const config = window.panelHavenMetadataMatchingCheck;
  assert(config?.disposable === true && config.bookId && $("details-dialog").open, "Open disposable Book details first.");
  const waitFor = async condition => {
    const deadline = Date.now() + 30000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("Metadata matching timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const originalFetch = window.fetch;
  const base = `/api/books/${encodeURIComponent(config.bookId)}`;
  const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
  const read = async path => { const response = await originalFetch(path); assert(response.ok, "Fixture read failed."); return response.json(); };
  const edit = async fields => {
    const response = await originalFetch(`${base}/metadata`, { method: "PUT", headers: { "Content-Type": "application/json", "X-PanelHaven-Request": "1" },
      body: JSON.stringify({ fields }) });
    assert(response.ok, "Fixture edit failed.");
  };
  await edit({ Writer: "Preserved writer" });
  const beforeBook = await read(base), before = await read(`${base}/metadata`);
  const proposal = { fields: { Series: "Matched series", Number: "37", Title: "Matched issue", Volume: "2025", Year: "2026", Writer: "" } };
  const candidate = { issueId: 34, volumeId: 12, series: "Matched series", number: "37", title: "Matched issue", startYear: "2025", score: 90,
    url: "https://comicvine.gamespot.com/example/4000-34/", coverUrl: `${base}/thumbnail` };
  let searchMode = "error", release, conflict = true, holdSave = false, saves = 0, fileOperation = false;
  window.fetch = async (...args) => {
    const url = new URL(args[0] instanceof Request ? args[0].url : args[0], location.href);
    const method = args[1]?.method || "GET";
    if (method === "POST" && /\/(intake\/(prepare|process)|cbz-replacement|archive-write|file-operations)/.test(url.pathname)) fileOperation = true;
    if (url.pathname === `${base}/metadata-matches/matches`) {
      if (searchMode === "error") return json({ error: "Simulated provider failure" }, 503);
      if (searchMode === "hold") await new Promise(resolve => { release = resolve; });
      return json({ candidates: [candidate], warnings: [] });
    }
    if (url.pathname === "/api/comic-vine/issues/34/metadata") return json(proposal);
    if (url.pathname === "/api/books/metadata" && method === "POST") {
      saves++;
      if (conflict) { conflict = false; await edit({ Title: "Concurrent edit" }); }
      if (holdSave) await new Promise(resolve => { release = resolve; });
    }
    return originalFetch(...args);
  };
  try {
    $("find-metadata").click();
    await waitFor(() => $("intake-dialog").open && !$("intake-match").disabled);
    assert($("intake-root").hidden && $("intake-queue").hidden, "Catalog matching must not offer destination processing.");
    $("intake-match").click();
    await waitFor(() => !$("intake-match").disabled && $("intake-status").textContent.includes("Simulated provider failure"));
    searchMode = "hold";
    $("intake-match").click();
    await waitFor(() => !!release);
    $("intake-cancel").click(); release(); release = null;
    await new Promise(resolve => setTimeout(resolve, 50));
    assert(!$("intake-candidates").children.length && saves === 0, "Cancelled search published candidates or saved metadata.");
    searchMode = "matches";
    $("intake-match").click();
    await waitFor(() => !$("intake-match").disabled && $("intake-candidates").querySelector("button"));
    assert(saves === 0, "Search automatically accepted metadata.");
    $("intake-candidates").querySelector("button").click();
    await waitFor(() => saves === 1 && !$("intake-match").disabled);
    assert($("intake-dialog").open && (await read(`${base}/metadata`)).fields.Title === "Concurrent edit", "Stale acceptance overwrote another edit.");
    holdSave = true;
    $("intake-candidates").querySelector("button").click();
    await waitFor(() => !!release);
    assert($("intake-cancel").hidden && $("intake-candidates").querySelector("button").disabled, "Save permits cancellation or duplicate acceptance.");
    $("intake-dialog").querySelector("[data-close]").click();
    const cancel = new Event("cancel", { cancelable: true });
    $("intake-dialog").dispatchEvent(cancel);
    assert(cancel.defaultPrevented && $("intake-dialog").open, "Save permits dismissal.");
    release(); release = null;
    await waitFor(() => !$("intake-dialog").open && document.activeElement === $("find-metadata"));
    const after = await read(`${base}/metadata`), afterBook = await read(base);
    assert(after.fields.Title === "Matched issue" && after.fields.Series === "Matched series" && after.fields.Number === "37"
      && after.fields.Volume === "2025" && after.fields.Year === "2026" && after.fields.Writer === "Preserved writer", "Acceptance lost suggested or existing metadata.");
    assert(JSON.stringify(before.custom) === JSON.stringify(after.custom) && beforeBook.location === afterBook.location
      && beforeBook.currentPage === afterBook.currentPage && !fileOperation && saves === 2, "Catalog acceptance changed source location, private state, custom metadata, or retried a save.");
    console.log("PASS: ranked catalog matching, search failure/cancellation, stale-write refusal, guarded one-click acceptance, missing-field preservation and catalog-only behavior. Provider responses simulated.");
  } finally {
    release?.();
    window.fetch = originalFetch;
  }
})();
