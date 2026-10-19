// Run in the browser console on an idle library page, signed in as an administrator.
// Exercises the real UI without saving settings or calling mutation endpoints.
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
    console.log("PASS: settings page routing, skip navigation, cancelled/confirmed drafts, secret cleanup, focus return and browser Back.");
  } finally { window.confirm = originalConfirm; }
})();
