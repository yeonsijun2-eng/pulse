(() => {
  const apiBase = document.querySelector('meta[name="pulse-license-api"]')?.content?.replace(/\/+$/, "");
  const tokenKey = "pulse-account-session";
  let token = null;

  function getToken() {
    if (token) return token;
    try {
      token = sessionStorage.getItem(tokenKey);
      return token;
    } catch {
      return null;
    }
  }

  function setToken(value) {
    if (!value) {
      token = null;
      try {
        sessionStorage.removeItem(tokenKey);
      } catch {
        return;
      }
      return;
    }
    try {
      sessionStorage.setItem(tokenKey, value);
      token = value;
    } catch {
      token = null;
      throw new Error("Browser session storage is unavailable. Enable storage to sign in.");
    }
  }

  async function request(path, options = {}) {
    if (!apiBase) throw new Error("The account service is not configured.");
    let response;
    try {
      response = await fetch(`${apiBase}${path}`, {
        ...options,
        headers: {
          ...(options.body ? { "Content-Type": "application/json" } : {}),
          ...(getToken() ? { Authorization: `Bearer ${getToken()}` } : {}),
          ...options.headers,
        },
      });
    } catch {
      throw new Error("Could not reach the account service. Check your connection and try again.");
    }

    const body = response.headers.get("content-type")?.includes("application/json")
      ? await response.json().catch(() => null)
      : null;
    if (!response.ok) {
      if (response.status === 404) {
        throw new Error("Account services are not connected yet. Device and account data have not been saved.");
      }
      if (response.status === 401) {
        setToken(null);
        const error = new Error("Your session has expired. Sign in again.");
        error.code = "unauthorized";
        throw error;
      }
      throw new Error(typeof body?.error === "string" ? body.error : `The request failed (${response.status}).`);
    }
    return body;
  }

  window.PulseAccountApi = Object.freeze({
    getToken,
    setToken,
    async login(email, password) {
      const result = await request("/account/login", {
        method: "POST",
        body: JSON.stringify({ email, password }),
      });
      if (!result || typeof result.token !== "string" || result.token.length < 32) {
        throw new Error("The account service returned an invalid session.");
      }
      setToken(result.token);
      return result;
    },
    async register(name, email, password) {
      const result = await request("/account/register", {
        method: "POST",
        body: JSON.stringify({ name, email, password }),
      });
      if (!result || typeof result.token !== "string" || result.token.length < 32) {
        throw new Error("The account service returned an invalid session.");
      }
      setToken(result.token);
      return result;
    },
    async logout() {
      try {
        if (getToken()) await request("/account/logout", { method: "POST" });
      } finally {
        setToken(null);
      }
    },
    getMe: () => request("/account/me"),
    getDevices: () => request("/account/devices"),
    addDevice: deviceId => request("/account/devices", { method: "POST", body: JSON.stringify({ deviceId }) }),
    renameDevice: (id, name) => request(`/account/devices/${encodeURIComponent(id)}`, {
      method: "PATCH",
      body: JSON.stringify({ name }),
    }),
    removeDevice: id => request(`/account/devices/${encodeURIComponent(id)}`, { method: "DELETE" }),
    getRelease: () => request("/account/releases/latest"),
    getActivity: cursor => request(`/account/activity?limit=20${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`),
    updateProfile: profile => request("/account/profile", { method: "PATCH", body: JSON.stringify(profile) }),
    updateSettings: settings => request("/account/settings", { method: "PATCH", body: JSON.stringify(settings) }),
    deleteAccount: () => request("/account", { method: "DELETE" }),
  });
})();
