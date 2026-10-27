(() => {
  const apiBase = document.querySelector('meta[name="pulse-license-api"]')?.content?.replace(/\/+$/, "");
  const form = document.querySelector("#license-request-form");
  if (!apiBase || !form) return;

  const deviceId = document.querySelector("#device-id");
  const feedback = document.querySelector("#license-feedback");
  const receiptPanel = document.querySelector("#license-receipt");
  const statusLabel = document.querySelector("#request-status");
  const statusMessage = document.querySelector("#request-message");
  const checkButton = document.querySelector("#check-license-status");
  const downloadButton = document.querySelector("#download-license");
  const receiptStorageKey = "pulse-license-request";
  let receipt = readReceipt();

  const deviceFromLink = /^#device=([a-f0-9]{32})$/i.exec(window.location.hash)?.[1];
  if (deviceFromLink) {
    deviceId.value = deviceFromLink.toUpperCase();
    window.history.replaceState(null, "", `${window.location.pathname}${window.location.search}`);
    deviceId.focus();
    showMessage("Your PULSE device ID is ready. Submit it to request a license.");
  }

  function readReceipt() {
    try {
      const saved = JSON.parse(sessionStorage.getItem(receiptStorageKey) || "null");
      return saved && /^[a-f0-9]{32}$/.test(saved.requestId)
        && typeof saved.requestToken === "string" ? saved : null;
    } catch {
      return null;
    }
  }

  function showMessage(message, isError = false) {
    feedback.textContent = message;
    feedback.dataset.error = String(isError);
  }

  function saveReceipt() {
    try {
      sessionStorage.setItem(receiptStorageKey, JSON.stringify(receipt));
      return true;
    } catch {
      return false;
    }
  }

  async function request(path, options = {}) {
    const response = await fetch(`${apiBase}${path}`, {
      ...options,
      headers: { "Content-Type": "application/json", ...options.headers },
    });
    const body = response.headers.get("content-type")?.includes("application/json")
      ? await response.json()
      : null;
    if (!response.ok) throw new Error(body?.error || `Request failed (${response.status}).`);
    return body;
  }

  function renderStatus(status) {
    receiptPanel.hidden = false;
    statusLabel.textContent = status.status.replaceAll("_", " ").toUpperCase();
    statusMessage.textContent = status.status === "issued"
      ? `${status.licenseType?.replaceAll("_", " ")} license ready. Download the file and import it in PULSE.`
      : status.status === "awaiting_signature"
        ? "Your request is approved and is being prepared."
        : "Your request is waiting for approval.";
    downloadButton.disabled = status.status !== "issued";
  }

  async function checkStatus() {
    if (!receipt) return;
    checkButton.disabled = true;
    try {
      const status = await request(`/license-requests/${receipt.requestId}`, {
        headers: { "X-Request-Token": receipt.requestToken },
      });
      renderStatus(status);
      showMessage("Request status updated.");
    } catch (error) {
      showMessage(error.message || "Could not check the request status.", true);
    } finally {
      checkButton.disabled = false;
    }
  }

  form.addEventListener("submit", async event => {
    event.preventDefault();
    if (!form.reportValidity()) return;
    const normalizedId = deviceId.value.trim().toUpperCase();
    if (!/^[A-F0-9]{32}$/.test(normalizedId)) {
      showMessage("Enter the complete 32-character device ID from PULSE.", true);
      return;
    }

    const submitButton = form.querySelector('button[type="submit"]');
    submitButton.disabled = true;
    try {
      receipt = await request("/license-requests", {
        method: "POST",
        body: JSON.stringify({ deviceId: normalizedId }),
      });
      const saved = saveReceipt();
      renderStatus(receipt);
      showMessage(saved
        ? "Request submitted. Keep this browser session open to check for your license."
        : "Request submitted. Copy the private request receipt now; browser storage is unavailable.");
    } catch (error) {
      showMessage(error.message || "Could not submit the request.", true);
    } finally {
      submitButton.disabled = false;
    }
  });

  document.querySelector("#copy-device-id").addEventListener("click", async () => {
    if (!/^[A-Fa-f0-9]{32}$/.test(deviceId.value.trim())) {
      showMessage("Paste the 32-character device ID from PULSE first.", true);
      return;
    }
    try {
      await navigator.clipboard.writeText(deviceId.value.trim().toUpperCase());
      showMessage("Device ID copied.");
    } catch {
      showMessage("Clipboard access was denied by the browser.", true);
    }
  });

  document.querySelector("#copy-request-receipt").addEventListener("click", async () => {
    if (!receipt) return;
    try {
      await navigator.clipboard.writeText(JSON.stringify(receipt));
      showMessage("Request receipt copied. Keep it private; it grants access to this request.");
    } catch {
      showMessage("Clipboard access was denied by the browser.", true);
    }
  });

  document.querySelector("#restore-request-receipt").addEventListener("click", () => {
    try {
      const restored = JSON.parse(document.querySelector("#request-receipt-input").value);
      if (!/^[a-f0-9]{32}$/.test(restored.requestId)
        || typeof restored.requestToken !== "string"
        || restored.requestToken.length < 32
        || restored.requestToken.length > 128) {
        throw new Error("The receipt format is invalid.");
      }

      receipt = restored;
      saveReceipt();
      void checkStatus();
    } catch (error) {
      showMessage(error.message || "The request receipt could not be restored.", true);
    }
  });

  checkButton.addEventListener("click", () => void checkStatus());

  downloadButton.addEventListener("click", async () => {
    if (!receipt || downloadButton.disabled) return;
    downloadButton.disabled = true;
    try {
      const response = await fetch(`${apiBase}/license-requests/${receipt.requestId}/download`, {
        headers: { "X-Request-Token": receipt.requestToken },
      });
      if (!response.ok) {
        const body = await response.json().catch(() => null);
        throw new Error(body?.error || `Download failed (${response.status}).`);
      }

      const url = URL.createObjectURL(await response.blob());
      const link = document.createElement("a");
      link.href = url;
      link.download = `PULSE-${receipt.requestId.slice(0, 8)}.pulse-license`;
      document.body.append(link);
      link.click();
      link.remove();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
      showMessage("License downloaded. Import the .pulse-license file in the PULSE activation screen.");
    } catch (error) {
      showMessage(error.message || "Could not download the license.", true);
    } finally {
      downloadButton.disabled = false;
    }
  });

  if (receipt) {
    void checkStatus();
  }
})();
