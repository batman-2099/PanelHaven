// Run in the browser console on an idle library page, signed in as an administrator.
// Requires a completed scan containing at least one invalid comicbook. Does not change source files.
(async () => {
  const $ = id => document.getElementById(id);
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const waitFor = async condition => {
    const deadline = Date.now() + 10000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("Scan log check timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const originalFetch = window.fetch;
  const originalClick = HTMLAnchorElement.prototype.click;
  const menuWasOpen = $("account-menu").open;
  const originalCreateObjectURL = URL.createObjectURL;
  const records = (await originalFetch("/api/scan/log").then(response => response.text())).trim().split("\n").map(line => JSON.parse(line));
  const failure = records.find(record => record.event === "file" && record.entry?.outcome === "error");
  assert(failure?.entry?.detail && failure.entry.stage, "Fixture needs a logged archive failure with exception details.");
  let download, created;
  try {
    $("account-menu").open = true;
    await waitFor(() => !$("export-scan-log").disabled);
    window.fetch = (input, options) => String(input) === "/api/scan/log"
      ? Promise.resolve(new Response(JSON.stringify({ error: "Diagnostic snapshot is unavailable." }), { status: 503, headers: { "Content-Type": "application/json" } }))
      : originalFetch(input, options);
    $("export-scan-log").click();
    await waitFor(() => !$("export-scan-log").disabled);
    assert($("scan-log-status").textContent.includes("Diagnostic snapshot is unavailable.") && $("scan-log-status").textContent.includes("Try exporting again"), "Export failure must explain the problem and allow retry.");
    assert($("account-menu").open, "Export feedback must remain visible in the menu.");
    window.fetch = originalFetch;
    HTMLAnchorElement.prototype.click = function () {
      if (this.download.startsWith("panelhaven-scan-log-")) {
        assert(this.download.endsWith(".jsonl"), "Diagnostic download needs its JSONL filename.");
        assert(created?.url === this.href, "Download must use the exported diagnostic blob.");
        download = created.blob.text();
      } else originalClick.call(this);
    };
    URL.createObjectURL = function (blob) {
      const url = originalCreateObjectURL.call(URL, blob);
      created = { url, blob };
      return url;
    };
    $("export-scan-log").click();
    await waitFor(() => !!download && !$("export-scan-log").disabled);
    const exported = (await download).trim().split("\n").map(line => JSON.parse(line));
    assert(exported.some(record => record.scanId === failure.scanId && record.entry?.path === failure.entry.path && record.entry?.detail === failure.entry.detail), "Downloaded snapshot lost the real archive failure details.");
    assert($("scan-log-status").textContent === "Scan log download started.", "Successful retry must replace the failure feedback.");
    if (!$("dismiss-scan").hidden) {
      $("dismiss-scan").click();
      $("account-menu").open = true;
      assert($("export-scan-log").checkVisibility() && !$("export-scan-log").disabled, "Dismissing a scan must not hide its diagnostic export.");
    }
    console.log("Scan log browser checks passed: failure/retry, actual diagnostic download, dismissal retention.");
  } finally {
    window.fetch = originalFetch;
    HTMLAnchorElement.prototype.click = originalClick;
    URL.createObjectURL = originalCreateObjectURL;
    $("account-menu").open = menuWasOpen;
  }
})();