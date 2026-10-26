// Run in the browser console on an idle Library page signed in as an administrator.
// Uses an isolated real-app frame, synthetic accounts, and intercepted writes; no saved data changes.
// History traversal is simulated with popstate; verify actual Back separately in a top-level disposable page.
(async () => {
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const waitFor = async condition => {
    const deadline = Date.now() + 15000;
    while (!condition()) {
      if (Date.now() > deadline) throw new Error("Account check timed out.");
      await new Promise(resolve => setTimeout(resolve, 25));
    }
  };
  const originalFetch = window.fetch;
  const identity = await originalFetch("/api/session").then(response => response.json());
  assert(identity.authenticated && identity.role === "admin", "Sign in as an administrator first.");
  const html = await originalFetch("/").then(response => response.text());
  const frame = document.createElement("iframe");
  frame.title = "Isolated account regression check";
  frame.style.cssText = "width:100%;height:800px";
  document.body.append(frame);
  const win = frame.contentWindow;
  const doc = win.document;
  const $ = id => doc.getElementById(id);
  const json = (value, status = 200) => new win.Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } });
  let users = [{ ...identity }, { id: "account-check-other", username: "check-other", role: "reader", mustChangePassword: false },
    { id: "account-check-draft", username: "check-draft", role: "reader", mustChangePassword: false }];
  let passwordRequests = 0, accountWrites = 0, accountReads = 0, logoutRequests = 0;
  let releasePassword, releaseAccounts, holdAccounts = false, failAccounts = false, expireScan = false;
  let outcome = "network";
  doc.open();
  win.confirm = () => true;
  win.fetch = async (input, options = {}) => {
    const path = new URL(input instanceof win.Request ? input.url : input, location.href).pathname;
    const method = options.method || "GET";
    if (path === "/api/session" || path === "/api/login") return json(identity);
    if (path === "/api/logout") { logoutRequests++; return new win.Response(null, { status: 204 }); }
    if (path === "/api/account/password") {
      passwordRequests++;
      return new Promise(resolve => { releasePassword = resolve; });
    }
    if (path === "/api/scan" && expireScan) {
      expireScan = false;
      return json({ error: "Check-only expired session" }, 401);
    }
    if (path === "/api/users" && method === "GET") {
      accountReads++;
      if (holdAccounts) {
        holdAccounts = false;
        await new Promise(resolve => { releaseAccounts = resolve; });
      }
      if (failAccounts) { failAccounts = false; throw new TypeError("Check-only refresh failure"); }
      return json(users);
    }
    if (path.startsWith("/api/users") && method !== "GET") {
      accountWrites++;
      if (outcome === "validation") return json({ error: "Check-only rejected account change" }, 400);
      const body = options.body ? JSON.parse(options.body) : null;
      if (method === "POST") users.push({ id: "account-check-created", username: body.username, role: body.role, mustChangePassword: false });
      if (path.endsWith("/role")) users.find(user => path.includes(user.id)).role = body.role;
      if (path.endsWith("/password")) users.find(user => path.includes(user.id)).mustChangePassword = true;
      if (method === "DELETE") users = users.filter(user => !path.endsWith(user.id));
      if (outcome === "network") throw new TypeError("Check-only lost response");
      return json({ error: "Check-only server failure" }, 503);
    }
    // Never forward a mutation, including on assertion failures or unexpected UI actions.
    if (method !== "GET") return json({ error: "Check-only blocked write" }, 400);
    return originalFetch(input, options);
  };
  const clean = () => {
    const event = new win.Event("beforeunload", { cancelable: true });
    win.dispatchEvent(event);
    return !event.defaultPrevented;
  };
  const fillPassword = () => {
    const form = $("password-form");
    form.elements.currentPassword.value = "check-old-password";
    form.elements.newPassword.value = form.elements.confirmation.value = "check-new-password";
    form.requestSubmit();
  };
  const openPassword = async () => {
    $("change-password").click();
    await waitFor(() => !$("password-view").hidden);
  };
  const row = id => $("user-list").querySelector(`[data-user-id="account-check-${id}"]`);
  try {
    doc.write(html);
    doc.close();
    await waitFor(() => $("startup")?.hidden && !$("library-view").hidden && $("books").getAttribute("aria-busy") !== "true");
    await openPassword();
    $("password-form").elements.currentPassword.value = "check-draft-password";
    assert(!clean(), "An unsubmitted password draft must protect unload.");
    win.confirm = () => false;
    win.location.hash = "#settings";
    await waitFor(() => win.location.hash === "#library");
    assert(!$("password-view").hidden && $("password-form").elements.currentPassword.value === "check-draft-password",
      "Accidental navigation discarded an unsubmitted password draft.");
    $("cancel-password").click();
    await waitFor(() => !$("library-view").hidden);
    assert(clean() && !$("password-form").elements.currentPassword.value, "Deliberate Cancel must clear credentials without prompting.");
    win.confirm = () => true;
    await openPassword();
    fillPassword();
    await waitFor(() => Boolean(releasePassword));
    assert([...$("password-form").elements].every(control => control.disabled) && $("logout").disabled && $("catalog-home").inert,
      "Password save must lock fields, submit, Cancel, sign out, and home navigation.");
    assert(!clean(), "A pending password save must protect unload.");
    $("cancel-password").click();
    $("logout").click();
    $("password-form").dispatchEvent(new win.Event("submit", { bubbles: true, cancelable: true }));
    assert(passwordRequests === 1 && logoutRequests === 0 && !$("password-view").hidden, "Pending password changes allowed another action.");
    win.location.hash = "#settings";
    await waitFor(() => win.location.hash === "#library");
    assert(!$("password-view").hidden && $("password-form").elements.newPassword.value === "check-new-password", "Hash navigation lost the pending password form.");
    win.history.pushState({ catalog: true }, "", "#settings");
    win.dispatchEvent(new win.PopStateEvent("popstate", { state: win.history.state }));
    await waitFor(() => win.location.hash === "#library");
    assert(!$("password-view").hidden, "Simulated history navigation departed during a password save.");
    releasePassword(json({ error: "Check-only rejected password" }, 400)); releasePassword = null;
    await waitFor(() => !$("cancel-password").disabled);
    assert(!clean() && !$("logout").disabled && !$("catalog-home").inert && doc.activeElement === $("password-status")
      && $("password-status").textContent.includes("Password change"), "Password failure must focus its contextual summary and retain draft unload protection.");
    assert($("password-form").elements.newPassword.value === "check-new-password", "Password failure discarded entered values.");

    fillPassword();
    await waitFor(() => Boolean(releasePassword));
    const stalePassword = releasePassword;
    releasePassword = null;
    expireScan = true;
    // Background scan polling remains active while the password request is pending.
    await waitFor(() => !$("login-view").hidden);
    assert(!$("logout").disabled && !$("catalog-home").inert && clean(), "Session expiry left password controls locked.");
    $("login-form").elements.username.value = identity.username;
    $("login-form").elements.password.value = "check-only-login";
    $("login-form").querySelector('[type="submit"]').click();
    await waitFor(() => !$("library-view").hidden && $("books").getAttribute("aria-busy") !== "true");
    await openPassword();
    fillPassword();
    await waitFor(() => Boolean(releasePassword));
    stalePassword(new win.Response(null, { status: 204 }));
    await new Promise(resolve => setTimeout(resolve, 50));
    assert(!$("password-view").hidden && $("cancel-password").disabled && !clean(), "An old auth epoch unlocked or replaced a newer password request.");
    releasePassword(json({ error: "Check-only rejected password" }, 400)); releasePassword = null;
    await waitFor(() => !$("cancel-password").disabled);
    $("cancel-password").click();
    await waitFor(() => !$("library-view").hidden && $("books").getAttribute("aria-busy") !== "true");

    $("manage-users").click();
    await waitFor(() => !$("user-controls").disabled);
    const create = $("user-form");
    create.elements.username.value = "check-created";
    create.elements.password.value = "check-created-password";
    const draft = row("draft");
    const draftRole = draft.querySelector("select");
    draftRole.value = "admin";
    const draftPassword = draft.querySelector('input[name="password"]');
    draftPassword.value = "check-preserved-draft";
    holdAccounts = true;
    const reads = accountReads;
    create.requestSubmit();
    await waitFor(() => Boolean(releaseAccounts));
    assert($("user-status").textContent.includes("unknown") && $("user-controls").disabled && !clean(), "Lost mutation responses must announce uncertainty and lock through reconciliation.");
    $("user-dialog").querySelector("[data-close]").click();
    assert($("user-dialog").open, "Account dialog closed during reconciliation.");
    releaseAccounts(); releaseAccounts = null;
    await waitFor(() => !$("user-controls").disabled);
    assert(accountWrites === 1 && accountReads === reads + 1 && row("created"), "Creation was retried or observable creation was not reconciled.");
    assert(create.elements.username.value === "check-created" && draftRole.value === "admin" && draftPassword.value === "check-preserved-draft", "Reconciliation discarded account drafts.");
    assert(doc.activeElement === $("user-status") && $("user-status").textContent.includes("check-created"),
      "Uncertain account creation must focus an account-specific summary.");

    outcome = "server";
    const reset = row("other").querySelector("details");
    reset.open = true;
    const resetForm = reset.querySelector("form");
    resetForm.elements.password.value = resetForm.elements.confirmation.value = "check-reset-password";
    resetForm.requestSubmit();
    await waitFor(() => !$("user-controls").disabled);
    assert(accountWrites === 2 && row("other").querySelector("p").textContent.includes("required at next sign-in"), "Password reset did not refresh observable account state exactly once.");
    assert($("user-status").textContent.includes("unknown") && $("user-status").textContent.includes("cannot confirm a password change") && resetForm.elements.password.value === "check-reset-password",
      "A refreshed list must not prove password success or discard its draft.");
    const roleForm = row("other").querySelector("form");
    roleForm.elements.role.value = "admin";
    roleForm.requestSubmit();
    await waitFor(() => !$("user-controls").disabled);
    assert(accountWrites === 3 && roleForm.elements.role.dataset.savedValue === "admin" && draftRole.value === "admin", "Role reconciliation lost saved state or another draft.");
    row("created").querySelector("button.danger").click();
    await waitFor(() => !$("user-controls").disabled);
    assert(accountWrites === 4 && !row("created") && draftPassword.value === "check-preserved-draft", "Deletion reconciliation failed or discarded another account draft.");

    outcome = "validation";
    const validationReads = accountReads;
    roleForm.requestSubmit();
    await waitFor(() => !$("user-controls").disabled);
    assert(accountReads === validationReads && $("user-status").textContent.includes("Check-only rejected account change")
      && $("user-status").textContent.includes("check-other") && !$("user-status").textContent.includes("unknown")
      && doc.activeElement === $("user-status"), "Definite account rejection must focus its contextual summary without reconciliation.");
    outcome = "network";
    failAccounts = true;
    roleForm.requestSubmit();
    await waitFor(() => !$("user-controls").disabled);
    assert(accountWrites === 6 && $("user-status").textContent.includes("unknown") && $("user-status").textContent.includes("could not refresh") && draftPassword.value === "check-preserved-draft",
      "Refresh failure hid uncertainty, retried the mutation, or discarded drafts.");
    $("user-dialog").querySelector("[data-close]").click();
    await waitFor(() => !$("user-dialog").open);
    await openPassword();
    fillPassword();
    await waitFor(() => Boolean(releasePassword));
    releasePassword(new win.Response(null, { status: 204 })); releasePassword = null;
    await waitFor(() => !$("login-view").hidden);
    assert($("login-message").textContent.startsWith("Password changed.") && clean() && !$("catalog-home").inert,
      "Successful password change failed to sign out and restore clean controls.");
    console.log("PASS: password draft/busy/navigation/unload guards, contextual error focus, auth-epoch isolation, account uncertainty, reconciliation, draft preservation, and no mutation retries.");
  } finally {
    releasePassword?.(json({ error: "Check cleanup" }, 400));
    releaseAccounts?.();
    frame.remove();
  }
})();
