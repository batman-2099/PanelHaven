// Run in the browser console on an idle library page, signed in as an administrator.
// Exercises the real UI with intercepted settings writes; no saved settings or service changes.
(async () => {
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const waitFor = async (condition) => {
    const deadline = Date.now() + 10000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("Settings navigation timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  assert(!document.getElementById("library-view").hidden && !document.querySelector("dialog[open]"), "Start on the idle library page.");
  const originalConfirm = window.confirm;
  const originalFetch = window.fetch;
  try {
    const menu = document.getElementById("account-menu");
    const trigger = menu.querySelector("summary");
    trigger.click();
    assert(menu.open, "Account menu did not open.");
    trigger.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    assert(!menu.open && document.activeElement === trigger, "Escape did not close the menu and restore focus.");
    trigger.click();
    document.getElementById("library-title").click();
    assert(!menu.open, "Outside click did not dismiss the menu.");
    trigger.click();
    document.getElementById("server-settings").click();
    const page = document.getElementById("settings-view");
    await waitFor(() => !page.hidden && !document.getElementById("server-settings-controls").disabled);
    assert(location.hash === "#settings" && document.getElementById("library-view").hidden && !document.querySelector("dialog[open]"), "Settings did not open as a separate page.");
    assert(!menu.open, "Choosing settings left the account menu open.");
    document.querySelector(".skip-link").click();
    await new Promise(resolve => setTimeout(resolve, 50));
    assert(location.hash === "#settings" && !page.hidden && document.activeElement.id === "main", "Skip to content changed the route or failed to focus content.");
    const key = document.getElementById("settings-comic-vine-key");
    const snapshot = await originalFetch("/api/server-settings").then(response => response.json());
    let rejectSave = true, writes = 0;
    window.fetch = async (input, options = {}) => {
      const path = new URL(input instanceof Request ? input.url : input, location.href).pathname;
      if (options.method && options.method !== "GET") {
        if (path !== "/api/server-settings" || options.method !== "PUT") throw new Error("Check-only blocked write");
        writes++;
        return new Response(JSON.stringify(rejectSave ? { error: "Check-only settings rejection" }
          : { ...snapshot, settings: JSON.parse(options.body).settings }), {
          status: rejectSave ? 400 : 200, headers: { "Content-Type": "application/json" }
        });
      }
      return originalFetch(input, options);
    };
    key.value = "check-only-settings-key";
    key.dispatchEvent(new Event("input", { bubbles: true }));
    for (const rejected of [true, false]) {
      rejectSave = rejected;
      document.getElementById("settings-current-password").value = "check-only-password";
      const count = writes;
      document.getElementById("save-server-settings").focus();
      document.getElementById("server-settings-form").requestSubmit(document.getElementById("save-server-settings"));
      await waitFor(() => writes > count && page.getAttribute("aria-busy") === "false");
      const status = document.getElementById("server-settings-status");
      assert(document.activeElement === status && status.textContent.includes(rejected ? "Server settings:" : "Settings saved."),
        "Settings completion must focus the contextual error or fall back from a disabled Save button.");
      assert(key.value === (rejected ? "check-only-settings-key" : "") && !document.getElementById("settings-current-password").value,
        "Settings failure must retain the draft; every result must clear the current password.");
    }
    window.fetch = originalFetch;
    document.getElementById("reload-server-settings").focus();
    document.getElementById("reload-server-settings").click();
    await waitFor(() => page.getAttribute("aria-busy") === "false");
    assert(document.activeElement.id === "reload-server-settings", "Settings reload lost its initiating focus.");
    const archived = document.getElementById("settings-archive-path");
    const unsorted = document.getElementById("settings-unsorted-path");
    assert(archived && unsorted && !archived.required && !unsorted.required, "Optional area path inputs are missing.");
    archived.value = "/navigation-check/archived";
    unsorted.value = "/navigation-check/unsorted";
    archived.dispatchEvent(new Event("input", { bubbles: true }));
    assert(!document.getElementById("save-server-settings").disabled, "Area paths did not mark settings dirty.");
    key.value = "navigation-check-not-a-real-key";
    key.dispatchEvent(new Event("input", { bubbles: true }));
    window.confirm = () => false;
    document.getElementById("settings-catalog-back").click();
    await waitFor(() => location.hash === "#settings");
    assert(!page.hidden && key.value === "navigation-check-not-a-real-key" && archived.value === "/navigation-check/archived" && unsorted.value === "/navigation-check/unsorted", "Cancelled navigation discarded the draft.");
    window.confirm = () => true;
    document.getElementById("settings-catalog-back").click();
    await waitFor(() => !document.getElementById("library-view").hidden && document.getElementById("books").getAttribute("aria-busy") !== "true");
    assert(page.hidden && key.value === "", "Leaving settings retained secret inputs or the page.");
    assert(document.activeElement !== document.body && document.activeElement.checkVisibility(), "Returning to the library lost visible keyboard focus.");
    history.back();
    await waitFor(() => !page.hidden && !document.getElementById("server-settings-controls").disabled);
    assert(location.hash === "#settings" && key.value === "", "Browser Back failed to reopen clean settings.");
    document.getElementById("settings-catalog-back").click();
    await waitFor(() => !document.getElementById("library-view").hidden);
    console.log("PASS: settings page routing, async save/reload focus, contextual rejection, cancelled/confirmed drafts, secret cleanup, focus return and browser Back.");
  } finally { window.confirm = originalConfirm; window.fetch = originalFetch; }
})();
