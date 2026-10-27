(() => {
  const api = window.PulseAccountApi;
  const content = document.querySelector("#dashboard-content");
  const toast = document.querySelector("#dashboard-toast");
  const sidebar = document.querySelector("#dashboard-sidebar");
  const scrim = document.querySelector("#dashboard-scrim");
  const dialog = document.querySelector("#dashboard-dialog");
  const release = {
    version: "1.0.0",
    date: "2026-10-27",
    size: "2.8 MB",
    url: "../downloads/PulseSetup.exe",
    changelog: ["Initial public Windows release."],
  };
  const titles = {
    overview: "OVERVIEW",
    devices: "MY DEVICES",
    downloads: "DOWNLOADS",
    activity: "ACTIVITY",
    settings: "ACCOUNT SETTINGS",
  };
  let user = null;
  let toastTimer;
  let dialogAction = null;
  let activityCursor = null;
  let activityBusy = false;

  const escapeHtml = value => String(value ?? "").replace(/[&<>"']/g, character => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  })[character]);
  const initials = value => (String(value || "P").trim()[0] || "P").toUpperCase();
  const dateText = value => {
    if (!value) return "Not available";
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? "Not available" : new Intl.DateTimeFormat(undefined, { dateStyle: "medium" }).format(date);
  };
  const dateTimeText = value => {
    if (!value) return "Not available";
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? "Not available" : new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(date);
  };

  function showToast(message, isError = false) {
    window.clearTimeout(toastTimer);
    toast.textContent = message;
    toast.dataset.error = String(isError);
    toast.hidden = false;
    toastTimer = window.setTimeout(() => { toast.hidden = true; }, 3600);
  }

  function setBusy(button, busy, label = "Working…") {
    if (busy) {
      button.dataset.originalLabel = button.textContent;
      button.textContent = label;
    } else {
      button.textContent = button.dataset.originalLabel || button.textContent;
      delete button.dataset.originalLabel;
    }
    button.disabled = busy;
  }

  function setAccount(userInfo) {
    user = userInfo;
    const email = typeof userInfo?.email === "string" ? userInfo.email : "";
    const name = typeof userInfo?.name === "string" && userInfo.name.trim()
      ? userInfo.name.trim()
      : email || "Account";
    document.querySelector("#sidebar-avatar").textContent = initials(name);
    document.querySelector("#sidebar-name").textContent = name;
    document.querySelector("#sidebar-email").textContent = email || "Signed in";
    document.querySelector("#signout-button").hidden = false;
    document.querySelectorAll(".nav-item").forEach(button => { button.disabled = false; });
  }

  function renderSignIn(message = "Sign in with your PULSE account to access your dashboard.") {
    user = null;
    document.querySelector("#signout-button").hidden = true;
    document.querySelector("#sidebar-name").textContent = "Account";
    document.querySelector("#sidebar-email").textContent = "Not signed in";
    document.querySelector("#sidebar-avatar").textContent = "P";
    document.querySelector("#topbar-section").textContent = "ACCOUNT ACCESS";
    document.querySelectorAll(".nav-item").forEach(button => {
      button.disabled = true;
      button.classList.remove("is-active");
      button.setAttribute("aria-current", "false");
    });
    content.innerHTML = `
      <section class="signin-layout">
        <div class="signin-copy">
          <p class="eyebrow">PULSE / ACCOUNT</p>
          <h1>Your workspace,<br><span>in one place.</span></h1>
          <p class="signin-description">Sign in to manage your registered devices, account preferences, and PULSE downloads.</p>
          <div class="signin-footnote"><span class="status-dot"></span>PRIVATE BY DESIGN <span>·</span> YOUR ACCOUNT DATA STAYS YOURS</div>
        </div>
        <section class="signin-card">
          <div class="auth-tabs" role="tablist" aria-label="Account access">
            <button class="auth-tab is-active" id="signin-tab" type="button" role="tab" aria-selected="true" aria-controls="signin-panel">Sign in</button>
            <button class="auth-tab" id="signup-tab" type="button" role="tab" aria-selected="false" aria-controls="signup-panel">Create account</button>
          </div>
          <form id="signin-form" role="tabpanel" aria-labelledby="signin-tab">
            <p class="eyebrow">ACCOUNT ACCESS</p>
            <h2>Welcome back</h2>
            <p class="signin-hint">Use your PULSE account credentials.</p>
            <label for="signin-email">EMAIL ADDRESS</label>
            <input id="signin-email" name="email" type="email" autocomplete="username" maxlength="254" required>
            <label for="signin-password">PASSWORD</label>
            <input id="signin-password" name="password" type="password" autocomplete="current-password" required>
            <button class="button button-light signin-submit" type="submit">Sign in <span aria-hidden="true">→</span></button>
            <p class="signin-feedback" id="signin-feedback" role="status" aria-live="polite">${escapeHtml(message)}</p>
          </form>
          <form id="signup-form" role="tabpanel" aria-labelledby="signup-tab" hidden>
            <p class="eyebrow">NEW TO PULSE</p>
            <h2>Create your account</h2>
            <p class="signin-hint">Set up an account to access your workspace.</p>
            <label for="signup-name">YOUR NAME</label>
            <input id="signup-name" name="name" type="text" autocomplete="name" maxlength="80" required>
            <label for="signup-email">EMAIL ADDRESS</label>
            <input id="signup-email" name="email" type="email" autocomplete="email" maxlength="254" required>
            <label for="signup-password">PASSWORD</label>
            <input id="signup-password" name="password" type="password" autocomplete="new-password" minlength="12" required>
            <label for="signup-password-confirm">CONFIRM PASSWORD</label>
            <input id="signup-password-confirm" name="passwordConfirm" type="password" autocomplete="new-password" minlength="12" required>
            <button class="button button-light signin-submit" type="submit">Create account <span aria-hidden="true">→</span></button>
            <p class="signin-feedback" id="signup-feedback" role="status" aria-live="polite">Account creation is not connected yet. No account will be created.</p>
          </form>
          <p class="service-note"><span>ACCOUNT API</span><strong>Connection required</strong><small>No account login or profile data is currently available. This form will not create or simulate an account.</small></p>
        </section>
      </section>`;
    document.querySelector("#signin-form").addEventListener("submit", signIn);
    document.querySelector("#signup-form").addEventListener("submit", registerAccount);
    document.querySelector("#signin-tab").addEventListener("click", () => selectAuthMode("signin"));
    document.querySelector("#signup-tab").addEventListener("click", () => selectAuthMode("signup"));
  }

  function selectAuthMode(mode) {
    const signInSelected = mode === "signin";
    document.querySelector("#signin-tab").classList.toggle("is-active", signInSelected);
    document.querySelector("#signup-tab").classList.toggle("is-active", !signInSelected);
    document.querySelector("#signin-tab").setAttribute("aria-selected", String(signInSelected));
    document.querySelector("#signup-tab").setAttribute("aria-selected", String(!signInSelected));
    document.querySelector("#signin-form").hidden = !signInSelected;
    document.querySelector("#signup-form").hidden = signInSelected;
  }

  async function registerAccount(event) {
    event.preventDefault();
    const form = event.currentTarget;
    if (!form.reportValidity()) return;
    const data = new FormData(form);
    const password = String(data.get("password"));
    if (password !== String(data.get("passwordConfirm"))) {
      document.querySelector("#signup-feedback").textContent = "The passwords do not match.";
      document.querySelector("#signup-feedback").dataset.error = "true";
      return;
    }
    const button = form.querySelector('button[type="submit"]');
    const feedback = document.querySelector("#signup-feedback");
    setBusy(button, true, "Creating account…");
    feedback.dataset.error = "false";
    try {
      await api.register(String(data.get("name")).trim(), String(data.get("email")).trim(), password);
      const account = await api.getMe();
      if (!account || typeof account.email !== "string") throw new Error("The account service returned an invalid profile.");
      setAccount(account);
      renderPage("overview");
    } catch (error) {
      api.setToken(null);
      feedback.dataset.error = "true";
      feedback.textContent = error.message || "Account creation could not be completed.";
    } finally {
      setBusy(button, false);
    }
  }

  async function signIn(event) {
    event.preventDefault();
    const form = event.currentTarget;
    if (!form.reportValidity()) return;
    const button = form.querySelector('button[type="submit"]');
    const feedback = document.querySelector("#signin-feedback");
    const data = new FormData(form);
    setBusy(button, true, "Connecting…");
    feedback.dataset.error = "false";
    try {
      await api.login(String(data.get("email")).trim(), String(data.get("password")));
      const account = await api.getMe();
      if (!account || typeof account.email !== "string") throw new Error("The account service returned an invalid profile.");
      setAccount(account);
      renderPage("overview");
    } catch (error) {
      feedback.dataset.error = "true";
      feedback.textContent = error.message || "Sign in could not be completed.";
      if (error.code === "unauthorized") renderSignIn(error.message);
    } finally {
      setBusy(button, false);
    }
  }

  function setPageTitle(page) {
    document.querySelector("#topbar-section").textContent = titles[page];
    document.querySelectorAll(".nav-item").forEach(button => {
      const selected = button.dataset.page === page;
      button.classList.toggle("is-active", selected);
      button.setAttribute("aria-current", selected ? "page" : "false");
    });
  }

  function renderPage(page) {
    if (!user) {
      renderSignIn("Sign in to view account data. The account API must be connected before access is available.");
      return;
    }
    setPageTitle(page);
    sidebar.classList.remove("is-open");
    scrim.hidden = true;
    document.querySelector("#mobile-menu").setAttribute("aria-expanded", "false");
    content.classList.remove("page-enter");
    void content.offsetWidth;
    content.classList.add("page-enter");
    if (page === "overview") renderOverview();
    else if (page === "devices") renderDevices();
    else if (page === "downloads") renderDownloads();
    else if (page === "activity") renderActivity();
    else renderSettings();
  }

  function accountName() {
    return typeof user?.name === "string" && user.name.trim()
      ? user.name.trim()
      : String(user?.email || "there").split("@")[0];
  }

  function renderOverview() {
    const createdAt = dateText(user?.createdAt);
    content.innerHTML = `
      <section class="page-heading">
        <div><p class="eyebrow">WELCOME BACK</p><h1>Good to see you, ${escapeHtml(accountName())}.</h1><p class="page-subtitle">${new Intl.DateTimeFormat(undefined, { dateStyle: "full" }).format(new Date())}</p></div>
        <button class="button button-muted" type="button" data-page-link="activity">View activity <span aria-hidden="true">→</span></button>
      </section>
      <section class="stat-grid" aria-label="Account summary">
        <article class="stat-card"><span class="stat-kicker">REGISTERED DEVICES</span><strong id="overview-device-count">—</strong><small id="overview-device-note">Checking account data</small></article>
        <article class="stat-card"><span class="stat-kicker">LATEST VERSION</span><strong>${release.version}</strong><small>Windows 10 / 11 · x64</small></article>
        <article class="stat-card"><span class="stat-kicker">LATEST DOWNLOAD</span><strong>—</strong><small>Personal download history is unavailable</small></article>
        <article class="stat-card"><span class="stat-kicker">ACCOUNT STATUS</span><strong class="status-value">${escapeHtml(user?.status === "active" ? "ACTIVE" : "—")}</strong><small>Account created ${escapeHtml(createdAt)}</small></article>
      </section>
      <section class="overview-grid">
        <article class="panel-card release-panel"><div class="panel-heading"><div><p class="eyebrow">LATEST RELEASE</p><h2>PULSE for Windows</h2></div><span class="release-version">V${release.version}</span></div><p>Official 64-bit installer for Windows 10 and Windows 11.</p><div class="release-meta"><span>FILE SIZE<strong>${release.size}</strong></span><span>RELEASED<strong>${dateText(release.date)}</strong></span></div><a class="button button-light" href="${release.url}" download>Download installer <span aria-hidden="true">↓</span></a></article>
        <article class="panel-card device-summary"><div class="panel-heading"><div><p class="eyebrow">YOUR DEVICES</p><h2>Device overview</h2></div><span class="panel-mark">02</span></div><div id="overview-device-state" class="inline-state"><span class="loading-mark small"></span>Loading device information</div><button class="text-button" type="button" data-page-link="devices">Manage devices <span aria-hidden="true">→</span></button></article>
      </section>
      <section class="panel-card recent-activity"><div class="panel-heading"><div><p class="eyebrow">ACCOUNT HISTORY</p><h2>Recent activity</h2></div><button class="text-button" type="button" data-page-link="activity">View all <span aria-hidden="true">→</span></button></div><div id="overview-activity-state" class="inline-state"><span class="loading-mark small"></span>Checking activity records</div></section>`;
    attachPageLinks();
    loadOverviewData();
  }

  async function loadOverviewData() {
    const count = document.querySelector("#overview-device-count");
    const note = document.querySelector("#overview-device-note");
    const deviceState = document.querySelector("#overview-device-state");
    const activityState = document.querySelector("#overview-activity-state");
    try {
      const result = await api.getDevices();
      if (!Array.isArray(result?.devices)) throw new Error("The device service returned invalid data.");
      count.textContent = String(result.devices.length);
      note.textContent = result.devices.length === 1 ? "One account device" : "Devices linked to your account";
      deviceState.innerHTML = result.devices.length
        ? `<strong>${result.devices.length}</strong><span>${result.devices.length === 1 ? "device registered" : "devices registered"}</span>`
        : `<strong>No devices</strong><span>No devices are linked to this account.</span>`;
    } catch (error) {
      count.textContent = "—";
      note.textContent = error.message;
      deviceState.textContent = error.message;
    }
    try {
      const result = await api.getActivity();
      if (!Array.isArray(result?.items)) throw new Error("The activity service returned invalid data.");
      activityState.innerHTML = result.items.length
        ? result.items.slice(0, 3).map(activityRow).join("")
        : `<span class="empty-note">No account activity has been recorded.</span>`;
    } catch (error) {
      activityState.textContent = error.message;
    }
  }

  function renderDevices() {
    content.innerHTML = `
      <section class="page-heading"><div><p class="eyebrow">ACCOUNT / DEVICES</p><h1>My devices</h1><p class="page-subtitle">Review and manage devices associated with your account.</p></div></section>
      <section class="panel-card registration-panel"><div><p class="eyebrow">LINK A DEVICE</p><h2>Register a PULSE device</h2><p>Enter the Device ID displayed in the PULSE desktop app. Registering a device does not grant a license or paid access.</p></div><form id="device-form"><label for="device-id">DEVICE ID</label><div class="inline-form"><input id="device-id" name="deviceId" type="text" inputmode="text" minlength="32" maxlength="32" pattern="[A-Fa-f0-9]{32}" autocomplete="off" spellcheck="false" placeholder="32-character device ID" required><button class="button button-light" type="submit">Register device</button></div><small class="form-hint">32 hexadecimal characters · Device ownership is verified by the account service.</small><p class="form-feedback" id="device-feedback" role="status" aria-live="polite"></p></form></section>
      <section class="panel-card devices-panel"><div class="panel-heading"><div><p class="eyebrow">YOUR DEVICES</p><h2>Registered devices</h2></div><button class="button button-muted" id="refresh-devices" type="button">Refresh</button></div><div id="device-list" class="device-list"><div class="loading-line"><span class="loading-mark small"></span>Loading registered devices</div></div></section>`;
    document.querySelector("#device-form").addEventListener("submit", registerDevice);
    document.querySelector("#refresh-devices").addEventListener("click", loadDevices);
    void loadDevices();
  }

  async function loadDevices() {
    const list = document.querySelector("#device-list");
    if (!list) return;
    list.innerHTML = `<div class="loading-line"><span class="loading-mark small"></span>Loading registered devices</div>`;
    try {
      const result = await api.getDevices();
      if (!Array.isArray(result?.devices)) throw new Error("The device service returned invalid data.");
      list.innerHTML = result.devices.length
        ? result.devices.map(deviceCard).join("")
        : `<div class="empty-state"><span class="empty-symbol">—</span><h3>No devices registered</h3><p>Register a device using the ID shown in your PULSE desktop app.</p></div>`;
      list.querySelectorAll("[data-copy-device]").forEach(button => button.addEventListener("click", () => copyText(button.dataset.copyDevice)));
      list.querySelectorAll("[data-rename-device]").forEach(button => button.addEventListener("click", () => renameDevice(button.dataset.renameDevice)));
      list.querySelectorAll("[data-remove-device]").forEach(button => button.addEventListener("click", () => confirmDeviceRemoval(button.dataset.removeDevice)));
    } catch (error) {
      list.innerHTML = `<div class="error-state"><strong>Device data unavailable</strong><p>${escapeHtml(error.message)}</p></div>`;
    }
  }

  function deviceCard(device) {
    if (!device || typeof device.id !== "string" || typeof device.deviceId !== "string") return "";
    return `<article class="device-row"><div class="device-icon" aria-hidden="true">PC</div><div class="device-details"><strong>${escapeHtml(device.name || "PULSE device")}</strong><code>${escapeHtml(device.deviceId)}</code><span>Registered ${escapeHtml(dateTimeText(device.registeredAt))} · Last seen ${escapeHtml(dateTimeText(device.lastSeenAt))}</span></div><span class="device-status">${escapeHtml(device.status || "UNKNOWN")}</span><div class="device-actions"><button class="text-button" type="button" data-copy-device="${escapeHtml(device.deviceId)}">Copy ID</button><button class="text-button" type="button" data-rename-device="${escapeHtml(device.id)}">Rename</button><button class="text-button danger-text" type="button" data-remove-device="${escapeHtml(device.id)}">Remove</button></div></article>`;
  }

  async function registerDevice(event) {
    event.preventDefault();
    const form = event.currentTarget;
    if (!form.reportValidity()) return;
    const input = form.elements.deviceId;
    const deviceId = input.value.trim().toUpperCase();
    const feedback = document.querySelector("#device-feedback");
    if (!/^[A-F0-9]{32}$/.test(deviceId)) {
      feedback.textContent = "Enter the complete 32-character hexadecimal Device ID.";
      feedback.dataset.error = "true";
      return;
    }
    const button = form.querySelector('button[type="submit"]');
    setBusy(button, true, "Registering…");
    try {
      await api.addDevice(deviceId);
      input.value = "";
      feedback.dataset.error = "false";
      feedback.textContent = "Device registered.";
      showToast("Device registered.");
      await loadDevices();
    } catch (error) {
      api.setToken(null);
      feedback.dataset.error = "true";
      feedback.textContent = error.message || "Device registration failed.";
    } finally {
      setBusy(button, false);
    }
  }

  async function copyText(value) {
    try {
      await navigator.clipboard.writeText(value);
      showToast("Device ID copied.");
    } catch {
      showToast("Clipboard access was denied by your browser.", true);
    }
  }

  async function renameDevice(id) {
    const name = window.prompt("Enter a new device name.");
    if (name === null) return;
    const normalized = name.trim();
    if (!normalized || normalized.length > 64) {
      showToast("Device names must be between 1 and 64 characters.", true);
      return;
    }
    try {
      await api.renameDevice(id, normalized);
      showToast("Device name updated.");
      await loadDevices();
    } catch (error) {
      showToast(error.message || "Device name could not be updated.", true);
    }
  }

  function confirmDeviceRemoval(id) {
    showConfirmation("Remove this device?", "The device will be unlinked from your account. This does not change any license.", async () => {
      try {
        await api.removeDevice(id);
        showToast("Device removed.");
        await loadDevices();
      } catch (error) {
        showToast(error.message || "Device could not be removed.", true);
      }
    });
  }

  function renderDownloads() {
    content.innerHTML = `
      <section class="page-heading"><div><p class="eyebrow">ACCOUNT / DOWNLOADS</p><h1>Downloads</h1><p class="page-subtitle">Official PULSE desktop releases.</p></div></section>
      <article class="download-card"><div class="download-card-top"><div class="download-brand"><span class="brand-mark" aria-hidden="true"><i></i><i></i><i></i><i></i><i></i></span><span>PULSE</span></div><span class="release-version">LATEST RELEASE</span></div><div class="download-card-title"><div><p class="eyebrow">WINDOWS DESKTOP</p><h2>PULSE for Windows</h2><p>Official installer for Windows 10 and Windows 11 · 64-bit</p></div><strong>v${release.version}</strong></div><div class="download-details"><span>FILE<strong>${release.size}</strong></span><span>RELEASE DATE<strong>${dateText(release.date)}</strong></span><span>PLATFORM<strong>Windows x64</strong></span></div><details class="changelog"><summary>Release notes <span aria-hidden="true">＋</span></summary><ul>${release.changelog.map(item => `<li>${escapeHtml(item)}</li>`).join("")}</ul></details><a class="button button-light download-action" href="${release.url}" download>Download PulseSetup.exe <span aria-hidden="true">↓</span></a><p class="download-footnote">The installer is served from the official PULSE website. Only download software you trust.</p></article>
      <section class="download-support"><p class="eyebrow">INSTALLATION</p><h2>Get started in a few steps</h2><ol><li><span>01</span>Download the Windows installer.</li><li><span>02</span>Open PulseSetup.exe and follow setup.</li><li><span>03</span>Launch PULSE from your Start menu.</li></ol></section>`;
  }

  function activityRow(item) {
    if (!item || typeof item.type !== "string" || typeof item.createdAt !== "string") return "";
    return `<article class="activity-row"><span class="activity-marker" aria-hidden="true"></span><div><strong>${escapeHtml(item.label || item.type.replaceAll("_", " "))}</strong><span>${escapeHtml(dateTimeText(item.createdAt))}</span></div><span class="activity-result">${escapeHtml(item.status || "RECORDED")}</span></article>`;
  }

  function renderActivity() {
    activityCursor = null;
    content.innerHTML = `<section class="page-heading"><div><p class="eyebrow">ACCOUNT / HISTORY</p><h1>Activity</h1><p class="page-subtitle">A record of events associated with your account.</p></div></section><section class="panel-card activity-panel"><div class="panel-heading"><div><p class="eyebrow">RECENT EVENTS</p><h2>Account activity</h2></div></div><div id="activity-list" class="activity-list"><div class="loading-line"><span class="loading-mark small"></span>Loading activity</div></div><button class="button button-muted load-more" id="activity-more" type="button" hidden>Load more</button></section>`;
    void loadActivity(false);
  }

  async function loadActivity(append) {
    if (activityBusy) return;
    activityBusy = true;
    const list = document.querySelector("#activity-list");
    const more = document.querySelector("#activity-more");
    if (!list) {
      activityBusy = false;
      return;
    }
    if (!append) list.innerHTML = `<div class="loading-line"><span class="loading-mark small"></span>Loading activity</div>`;
    try {
      const result = await api.getActivity(append ? activityCursor : null);
      if (!Array.isArray(result?.items)) throw new Error("The activity service returned invalid data.");
      activityCursor = typeof result.nextCursor === "string" ? result.nextCursor : null;
      const rows = result.items.map(activityRow).filter(Boolean).join("");
      if (!append) list.innerHTML = "";
      list.insertAdjacentHTML("beforeend", rows || (!append ? `<div class="empty-state"><span class="empty-symbol">—</span><h3>No activity yet</h3><p>Account events will appear here when the activity service is connected.</p></div>` : ""));
      more.hidden = !activityCursor;
      more.onclick = () => void loadActivity(true);
    } catch (error) {
      list.innerHTML = `<div class="error-state"><strong>Activity records unavailable</strong><p>${escapeHtml(error.message)}</p></div>`;
      more.hidden = true;
    } finally {
      activityBusy = false;
    }
  }

  function readPreferences() {
    try {
      return JSON.parse(localStorage.getItem("pulse-dashboard-preferences") || "{}");
    } catch {
      return {};
    }
  }

  function renderSettings() {
    const preferences = readPreferences();
    content.innerHTML = `
      <section class="page-heading"><div><p class="eyebrow">ACCOUNT / PREFERENCES</p><h1>Account settings</h1><p class="page-subtitle">Manage your profile and dashboard preferences.</p></div></section>
      <form class="settings-form" id="settings-form">
        <section class="panel-card settings-section"><div class="settings-heading"><p class="eyebrow">PROFILE</p><h2>Personal information</h2><p>Profile details are managed by your account service.</p></div><div class="settings-fields"><label for="profile-name">DISPLAY NAME</label><input id="profile-name" name="name" maxlength="80" value="${escapeHtml(user?.name || "")}" placeholder="Your name"><label for="profile-email">EMAIL ADDRESS</label><input id="profile-email" name="email" type="email" value="${escapeHtml(user?.email || "")}" readonly><span class="form-hint">Account created ${escapeHtml(dateText(user?.createdAt))}</span></div><button class="button button-muted" type="submit">Save profile</button><p class="form-feedback" id="profile-feedback" role="status" aria-live="polite"></p></section>
        <section class="panel-card settings-section"><div class="settings-heading"><p class="eyebrow">PREFERENCES</p><h2>Your experience</h2><p>These preferences are saved in this browser on this device.</p></div><div class="preference-list"><label class="preference-row" for="preference-language"><span><strong>Language</strong><small>Choose the dashboard language.</small></span><select id="preference-language" name="language"><option value="en">English</option><option value="ko" disabled>한국어 · Coming soon</option></select></label><label class="preference-row" for="preference-notifications"><span><strong>Account notifications</strong><small>Local preference only; delivery requires account service support.</small></span><input id="preference-notifications" class="switch-input" name="notifications" type="checkbox" ${preferences.notifications ? "checked" : ""}><span class="switch" aria-hidden="true"></span></label><label class="preference-row" for="preference-motion"><span><strong>Reduce motion</strong><small>Limit non-essential interface animation.</small></span><input id="preference-motion" class="switch-input" name="reduceMotion" type="checkbox" ${preferences.reduceMotion ? "checked" : ""}><span class="switch" aria-hidden="true"></span></label></div><button class="button button-light" id="save-preferences" type="button">Save preferences</button><p class="form-feedback" id="preferences-feedback" role="status" aria-live="polite"></p></section>
        <section class="panel-card settings-section account-section"><div class="settings-heading"><p class="eyebrow">ACCOUNT</p><h2>Account access</h2><p>Sign out of this browser session. Account deletion requires a connected account service.</p></div><button class="button button-muted" id="settings-signout" type="button">Sign out</button><button class="text-button danger-text" id="delete-account" type="button">Request account deletion</button><p class="service-caption">Account deletion is unavailable until an authenticated deletion API and data-retention policy are provided.</p></section>
      </form>`;
    const settingsForm = document.querySelector("#settings-form");
    settingsForm.addEventListener("submit", saveProfile);
    document.querySelector("#save-preferences").addEventListener("click", savePreferences);
    document.querySelector("#settings-signout").addEventListener("click", signOut);
    document.querySelector("#delete-account").addEventListener("click", () => showToast("Account deletion is not connected. No data was changed.", true));
    document.querySelector("#preference-language").value = preferences.language === "ko" ? "ko" : "en";
    document.querySelector("#preference-motion").addEventListener("change", applyMotionPreference);
  }

  async function saveProfile(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const button = form.querySelector('button[type="submit"]');
    const feedback = document.querySelector("#profile-feedback");
    setBusy(button, true, "Saving…");
    try {
      const name = String(new FormData(form).get("name") || "").trim();
      if (name.length > 80) throw new Error("Display names must be 80 characters or fewer.");
      const updated = await api.updateProfile({ name });
      if (!updated || typeof updated.name !== "string") throw new Error("The profile service returned invalid data.");
      setAccount({ ...user, ...updated });
      feedback.textContent = "Profile updated.";
      feedback.dataset.error = "false";
    } catch (error) {
      feedback.textContent = error.message || "Profile could not be updated.";
      feedback.dataset.error = "true";
    } finally {
      setBusy(button, false);
    }
  }

  function savePreferences() {
    const form = document.querySelector("#settings-form");
    const data = new FormData(form);
    const preferences = {
      language: data.get("language") === "ko" ? "ko" : "en",
      notifications: data.get("notifications") === "on",
      reduceMotion: data.get("reduceMotion") === "on",
    };
    try {
      localStorage.setItem("pulse-dashboard-preferences", JSON.stringify(preferences));
      applyMotionPreference();
      const feedback = document.querySelector("#preferences-feedback");
      feedback.dataset.error = "false";
      feedback.textContent = "Preferences saved in this browser.";
      showToast("Preferences saved.");
    } catch {
      const feedback = document.querySelector("#preferences-feedback");
      feedback.dataset.error = "true";
      feedback.textContent = "Browser storage is unavailable. Preferences were not saved.";
    }
  }

  function applyMotionPreference() {
    const reduceMotion = document.querySelector("#preference-motion")?.checked ?? readPreferences().reduceMotion;
    document.documentElement.classList.toggle("reduce-motion", Boolean(reduceMotion));
  }

  function showConfirmation(title, message, action) {
    document.querySelector("#dialog-title").textContent = title;
    document.querySelector("#dialog-message").textContent = message;
    dialogAction = action;
    dialog.showModal();
  }

  document.querySelector("#dialog-cancel").addEventListener("click", () => dialog.close());
  document.querySelector("#dialog-confirm").addEventListener("click", async event => {
    const button = event.currentTarget;
    const action = dialogAction;
    dialogAction = null;
    dialog.close();
    setBusy(button, true);
    try {
      if (action) await action();
    } finally {
      setBusy(button, false);
    }
  });

  function attachPageLinks() {
    content.querySelectorAll("[data-page-link]").forEach(button => {
      button.addEventListener("click", () => renderPage(button.dataset.pageLink));
    });
  }

  async function signOut() {
    try {
      await api.logout();
      showToast("Signed out.");
    } catch (error) {
      showToast(error.message || "The session could not be ended.", true);
    } finally {
      renderSignIn("Sign in to access your account dashboard.");
    }
  }

  document.querySelectorAll(".nav-item").forEach(button => {
    button.addEventListener("click", () => renderPage(button.dataset.page));
  });
  document.querySelector("#signout-button").addEventListener("click", signOut);
  document.querySelector("#mobile-menu").addEventListener("click", event => {
    const open = sidebar.classList.toggle("is-open");
    scrim.hidden = !open;
    event.currentTarget.setAttribute("aria-expanded", String(open));
  });
  scrim.addEventListener("click", () => {
    sidebar.classList.remove("is-open");
    scrim.hidden = true;
    document.querySelector("#mobile-menu").setAttribute("aria-expanded", "false");
  });

  async function start() {
    applyMotionPreference();
    if (!api.getToken()) {
      renderSignIn();
      return;
    }
    try {
      const account = await api.getMe();
      if (!account || typeof account.email !== "string") throw new Error("The account service returned an invalid profile.");
      setAccount(account);
      renderPage("overview");
    } catch (error) {
      api.setToken(null);
      renderSignIn(error.message || "Sign in to access your account dashboard.");
    }
  }

  void start();
})();
