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
  let candidate = { provider: "comicvine", issueId: 34, volumeId: 12, series: "Matched series", number: "37", title: "Matched issue", startYear: "2025", score: 90,
    url: "https://comicvine.gamespot.com/matched-issue/4000-34/", coverUrl: `${base}/thumbnail` };
  let configuredProvider = null;
  const searches = [], acceptedProviders = [];
  let searchMode = "error", release, conflict = true, holdSave = false, saves = 0, fileOperation = false;
  window.fetch = async (...args) => {
    const url = new URL(args[0] instanceof Request ? args[0].url : args[0], location.href);
    const method = args[1]?.method || "GET";
    if (method === "POST" && /\/(intake\/(prepare|process)|cbz-replacement|archive-write|file-operations)/.test(url.pathname)) fileOperation = true;
    if (url.pathname === "/api/metadata-provider/status") return json({ providers: ["comicvine", "metron"].map(provider =>
      ({ provider, configured: configuredProvider === null || configuredProvider === provider })) });
    if (url.pathname === `${base}/metadata-matches/matches`) {
      const provider = url.searchParams.get("provider");
      assert(method === "POST" && ["comicvine", "metron"].includes(provider), "Search omitted its explicit provider.");
      searches.push(provider);
      if (searchMode === "error") return json({ error: "Simulated provider failure" }, 503);
      if (searchMode === "hold") await new Promise(resolve => { release = resolve; });
      candidate = { ...candidate, provider, url: provider === "comicvine"
        ? "https://comicvine.gamespot.com/matched-issue/4000-34/" : "https://metron.cloud/issue/34/" };
      return json({ candidates: [candidate, ...["https://metron.cloud/issue/34/", "http://metron.cloud/issue/34/",
        "https://metron.cloud.evil.test/issue/34/", "https://user:secret@comicvine.gamespot.com/issue/4000-34/",
        "javascript:alert(1)"].map(url => ({ ...candidate, url }))], warnings: [] });
    }
    if (url.pathname === "/api/metadata-provider/issues/34/metadata") {
      assert(url.searchParams.get("provider") === candidate.provider && url.searchParams.get("volumeId") === "12"
        && url.searchParams.get("intake") === "true", "Metadata acceptance lost candidate provenance.");
      acceptedProviders.push(url.searchParams.get("provider"));
      return json(proposal);
    }
    if (url.pathname === "/api/books/metadata" && method === "POST") {
      saves++;
      if (conflict) { conflict = false; await edit({ Title: "Concurrent edit" }); }
      if (holdSave) await new Promise(resolve => { release = resolve; });
    }
    return originalFetch(...args);
  };
  try {
    for (const provider of ["comicvine", "metron"]) {
      configuredProvider = provider;
      $("find-metadata").click();
      const match = $(`intake-match-${provider}`), unavailable = $(`intake-match-${provider === "comicvine" ? "metron" : "comicvine"}`);
      await waitFor(() => $("intake-dialog").open && !match.disabled);
      assert(unavailable.disabled, "An unconfigured provider remained available.");
      const count = searches.length;
      unavailable.click();
      assert(searches.length === count, "Disabled provider started a search.");
      match.click();
      await waitFor(() => searches.length === count + 1 && !match.disabled);
      assert(searches.at(-1) === provider && saves === 0, "Named button queried another provider or automatically saved.");
      const closed = new Promise(resolve => $("intake-dialog").addEventListener("close", resolve, { once: true }));
      $("intake-dialog").querySelector("[data-close]").click();
      await closed;
    }
    configuredProvider = null;
    $("find-metadata").click();
    await waitFor(() => $("intake-dialog").open && !$("intake-match-comicvine").disabled && !$("intake-match-metron").disabled);
    assert($("intake-root").hidden && $("intake-queue").hidden, "Catalog matching must not offer destination processing.");
    searchMode = "hold";
    $("intake-match-metron").click();
    await waitFor(() => !!release);
    $("intake-cancel").click(); release(); release = null;
    await new Promise(resolve => setTimeout(resolve, 50));
    assert(!$("intake-candidates").children.length && saves === 0, "Cancelled search published candidates or saved metadata.");
    searchMode = "matches";
    $("intake-match-metron").click();
    await waitFor(() => !$("intake-match-metron").disabled && $("intake-candidates").querySelector("button"));
    assert(saves === 0, "Search automatically accepted metadata.");
    $("intake-candidates").querySelector("button").click();
    await waitFor(() => saves === 1 && !$("intake-match-metron").disabled);
    assert($("intake-dialog").open && (await read(`${base}/metadata`)).fields.Title === "Concurrent edit", "Stale acceptance overwrote another edit.");
    $("intake-match-comicvine").click();
    await waitFor(() => !$("intake-match-comicvine").disabled && $("intake-candidates").querySelector("button"));
    assert(searches.at(-1) === "comicvine" && candidate.provider === "comicvine", "Comicvine Match reused the preceding Metron search.");
    const providerLink = $("intake-candidates").querySelector("a");
    assert(providerLink?.href === candidate.url && providerLink.rel === "noopener noreferrer" && providerLink.target === "_blank",
      "Comic Vine candidate links lost their provider URL or safe external-link attributes.");
    const links = $("intake-candidates").querySelectorAll("a");
    assert(links.length === 2 && links[1].href === "https://metron.cloud/issue/34/" && links[1].rel === "noopener noreferrer",
      "Metron links were rejected or an untrusted provider URL became clickable.");
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
    assert(acceptedProviders.join(",") === "metron,comicvine", "Acceptance lost either candidate's provider identity.");
    console.log("PASS: explicit Comicvine/Metron button requests, independent credential availability, provider-bound acceptance, search failure/cancellation, stale-write refusal, guarded one-click acceptance and catalog-only behavior. Provider responses simulated.");
  } finally {
    release?.();
    window.fetch = originalFetch;
  }
})();
