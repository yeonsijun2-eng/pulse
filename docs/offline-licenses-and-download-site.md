# PULSE licenses, installer, and download site

PULSE verifies ECDSA-signed licenses locally. Licenses are bound to the 32-character device ID shown on the activation screen and are available for 7 days, 30 days, or permanently. Timed licenses expire from their issue time. Because the app verifies licenses offline, a license cannot be remotely revoked and expiration depends on the local system clock.

## Build the desktop app and installer

From the repository root, build the Windows x64 app and per-user NSIS installer:

```powershell
npm ci
npm run tauri -- build
```

Copy `src-tauri\target\release\pulse.exe` to `website\downloads\Pulse.exe` and `src-tauri\target\release\bundle\nsis\PULSE_1.0.0_x64-setup.exe` to `website\downloads\PulseSetup.exe`. Update the displayed version, installer size, and date in `website\index.html` for each release. The installer targets the current Windows user; an internet connection may be needed to install WebView2 when it is missing.

## Issue a license without exposing the signing key

The website accepts device-bound requests and stores signed license files for download. It never signs licenses and never receives the private signing key. The private key remains protected by Windows DPAPI on the original issuer account. Do not copy it to Render, the website, or a repository.

The existing keypair is initialized on the original issuer PC. Do not generate a replacement unless you also update `LicensingCore\pulse-license-public-key.pem` and rebuild every app that verifies licenses.

Build the graphical issuer application on the original Windows account:

```powershell
dotnet publish .\LicenseIssuerDesktop\Pulse.LicenseIssuer.Desktop.csproj --configuration Release -p:PublishProfile=PulseLicenseIssuer-win-x64
```

The issuer is written to `LicenseIssuerDesktop\bin\Release\net8.0-windows\publish\win-x64\PulseLicenseIssuer.exe`. It verifies that the local private key matches the app's public key before allowing issuance.

For an online request, open `https://pulse.xyz.ci/admin/`, sign in with the administrator password, and prepare the requested device ID for the chosen term. Copy that ID into the local issuer, select the same 7-day, 30-day, or permanent term, generate the license, then paste the signed code back into the admin page. The API verifies the signature, device ID, and term before making the `.pulse-license` file available. The user checks the request in the same browser session and downloads the file; PULSE can import it from the activation screen.

Keep a secure backup of `%LOCALAPPDATA%\PULSE-License-Issuer\signing-key.dpapi`. It can only be opened by the Windows account that created it. Losing it prevents issuing keys compatible with the deployed app. Do not share it or commit it to source control.

## Deploy the website and request API

The Render Blueprint in `render.yaml` publishes the static `website` directory. The license API runs on Cloudflare Workers Free with persistent D1 storage, configured by `wrangler.toml` and `worker\migrations`. Create the D1 database, apply migrations, set `PULSE_ADMIN_PASSWORD` as a Worker secret (use a unique value of at least 24 characters), then deploy with `npm run worker:migrate` and `npm run worker:deploy`. Never put the admin password or signing private key in source control. The allowed website origin is `https://pulse.xyz.ci`; keep it aligned with the static site's custom domain. The Worker verifies uploaded license codes using the same P-256 public key as the desktop app.

The static site files and downloads are under `website`. The Worker endpoint is set in the `pulse-license-api` meta tag on both `website\index.html` and `website\admin\index.html`. D1 stores a hash of each user's private request-receipt token; requests are retained for 180 days and limited to 5 submissions per IP per 30 minutes. Admin sessions expire after 30 minutes and login attempts are rate-limited.

Users paste the device ID from PULSE into the request form. The site returns a private request receipt; the user can keep it in the same browser session or copy and restore it later. The API stores only a hash of this receipt token. Request and signed license records are retained for 180 days; delete requests from the issuer page when they should be removed sooner. No email address or payment details are collected.

The ASP.NET `LicenseServer` remains available for self-hosted deployments; it is not provisioned by the free Render static-site blueprint.
