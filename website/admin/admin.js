(() => {
  const apiBase = document.querySelector('meta[name="pulse-license-api"]')?.content?.replace(/\/+$/, "");
  if (!apiBase) return;

  const loginForm = document.querySelector("#admin-login");
  const dashboard = document.querySelector("#admin-dashboard");
  const requestList = document.querySelector("#request-list");
  const feedback = document.querySelector("#admin-feedback");
  const logoutButton = document.querySelector("#logout-button");
  const tokenStorageKey = "pulse-admin-session";
  let token = sessionStorage.getItem(tokenStorageKey);

  function showMessage(message, isError = false) {
    feedback.textContent = message;
    feedback.dataset.error = String(isError);
  }

  async function api(path, options = {}) {
    const response = await fetch(`${apiBase}${path}`, {
      ...options,
      headers: {
        "Content-Type": "application/json",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...options.headers,
      },
    });
    const body = response.headers.get("content-type")?.includes("application/json")
      ? await response.json()
      : null;
    if (!response.ok) {
      if (response.status === 401) signOut();
      throw new Error(body?.error || `Request failed (${response.status}).`);
    }
    return body;
  }

  function signOut() {
    token = null;
    sessionStorage.removeItem(tokenStorageKey);
    loginForm.hidden = false;
    dashboard.hidden = true;
    logoutButton.hidden = true;
    requestList.replaceChildren();
  }

  function makeButton(label, action, className = "license-secondary-button") {
    const button = document.createElement("button");
    button.type = "button";
    button.className = className;
    button.textContent = label;
    button.addEventListener("click", action);
    return button;
  }

  function appendText(parent, tag, className, value) {
    const node = document.createElement(tag);
    node.className = className;
    node.textContent = value;
    parent.append(node);
    return node;
  }

  function createRequestCard(request) {
    const card = document.createElement("article");
    card.className = "request-card";
    appendText(card, "span", "request-state", request.status.replaceAll("_", " ").toUpperCase());
    appendText(card, "p", "request-device", request.deviceId);
    appendText(card, "p", "request-meta", `Submitted ${new Date(request.createdAt).toLocaleString()}`);

    const controls = document.createElement("div");
    controls.className = "request-controls";
    if (request.status !== "issued") {
      const term = document.createElement("select");
      term.className = "request-term";
      for (const [value, label] of [
        ["seven_day", "7 DAYS"],
        ["thirty_day", "30 DAYS"],
        ["permanent", "PERMANENT"],
      ]) {
        const option = document.createElement("option");
        option.value = value;
        option.textContent = label;
        option.selected = request.licenseType === value;
        term.append(option);
      }
      controls.append(term);

      controls.append(makeButton("COPY DEVICE ID", async () => {
        try {
          await navigator.clipboard.writeText(request.deviceId);
          showMessage("Device ID copied. Select the same term in the local issuer.");
        } catch {
          showMessage("Clipboard access was denied by the browser.", true);
        }
      }));

      controls.append(makeButton(
        request.status === "awaiting_signature" ? "UPDATE TERM" : "PREPARE LICENSE",
        async event => {
          const button = event.currentTarget;
          button.disabled = true;
          try {
            await api(`/admin/requests/${request.id}/prepare`, {
              method: "POST",
              body: JSON.stringify({ licenseType: term.value }),
            });
            await loadRequests();
            showMessage("Request prepared. Sign this device ID with the matching term in the local PULSE issuer.");
          } catch (error) {
            showMessage(error.message, true);
            button.disabled = false;
          }
        },
        "license-primary-button",
      ));

      if (request.status === "awaiting_signature") {
        const code = document.createElement("textarea");
        code.className = "admin-license-code";
        code.maxLength = 1024;
        code.rows = 3;
        code.placeholder = "Paste the signed PULSE1 license code from the local issuer";
        code.setAttribute("aria-label", "Signed license code");
        controls.append(code);
        controls.append(makeButton("UPLOAD SIGNED LICENSE", async event => {
          const button = event.currentTarget;
          button.disabled = true;
          try {
            await api(`/admin/requests/${request.id}/license`, {
              method: "POST",
              body: JSON.stringify({ licenseCode: code.value.trim() }),
            });
            await loadRequests();
            showMessage("Signed license verified and made available to the user.");
          } catch (error) {
            showMessage(error.message, true);
          } finally {
            button.disabled = false;
          }
        }, "license-primary-button"));
      }
    } else {
      appendText(card, "p", "request-meta", `Issued ${new Date(request.issuedAt).toLocaleString()}`);
    }

    controls.append(makeButton("DELETE", async event => {
      const button = event.currentTarget;
      if (!window.confirm("Delete this request and its stored license? This cannot be undone.")) return;
      button.disabled = true;
      try {
        await api(`/admin/requests/${request.id}`, { method: "DELETE" });
        await loadRequests();
        showMessage("Request deleted.");
      } catch (error) {
        showMessage(error.message, true);
        button.disabled = false;
      }
    }));
    card.append(controls);
    return card;
  }

  async function loadRequests() {
    const requests = await api("/admin/requests");
    requestList.replaceChildren();
    if (!requests.length) {
      appendText(requestList, "p", "empty-requests", "No license requests yet.");
      return;
    }
    for (const request of requests) requestList.append(createRequestCard(request));
  }

  async function enterDashboard() {
    loginForm.hidden = true;
    dashboard.hidden = false;
    logoutButton.hidden = false;
    await loadRequests();
  }

  loginForm.addEventListener("submit", async event => {
    event.preventDefault();
    const password = document.querySelector("#admin-password");
    const button = loginForm.querySelector('button[type="submit"]');
    button.disabled = true;
    try {
      const result = await api("/admin/login", {
        method: "POST",
        body: JSON.stringify({ password: password.value }),
      });
      token = result.token;
      sessionStorage.setItem(tokenStorageKey, token);
      password.value = "";
      await enterDashboard();
      showMessage("Signed in. License codes are checked against the PULSE app's public key before upload.");
    } catch (error) {
      showMessage(error.message, true);
    } finally {
      button.disabled = false;
    }
  });

  document.querySelector("#refresh-requests").addEventListener("click", async event => {
    const button = event.currentTarget;
    button.disabled = true;
    try {
      await loadRequests();
      showMessage("Requests refreshed.");
    } catch (error) {
      showMessage(error.message, true);
    } finally {
      button.disabled = false;
    }
  });

  logoutButton.addEventListener("click", signOut);

  if (token) {
    void enterDashboard().catch(error => showMessage(error.message, true));
  }
})();
