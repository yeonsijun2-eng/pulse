# PULSE licenses, installer, and download site

PULSE verifies ECDSA-signed licenses locally. Licenses are bound to the 32-character device ID shown on the activation screen and are available for 7 days, 30 days, or permanently. Timed licenses expire from their issue time. Because the app verifies licenses offline, a license cannot be remotely revoked and expiration depends on the local system clock.

## Build the app and installer

From the repository root, publish the self-contained Windows x64 app:

```powershell
dotnet publish .\ValorantLauncher.csproj --configuration Release -p:PublishProfile=Pulse-win-x64
```

The app is written to `website\downloads\Pulse.exe`. The installer script packages that executable into a per-user setup program with Start Menu and optional desktop shortcuts. It installs under the current user's Local AppData and does not require administrator access.

Install Inno Setup 6 if needed, then compile the installer:

```powershell
winget install --id JRSoftware.InnoSetup --exact
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" .\Installer\Pulse.iss
```

The resulting `website\downloads\PulseSetup.exe` is linked from the download page. Upload the `website` directory as a static site after building the app and installer.

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

The Render Blueprint in `render.yaml` defines the static download site and the ASP.NET license API. Provision both services from the repository root. The API uses a persistent 1 GB disk; the paid Render plan is required for persistent disks. Configure `PULSE_ADMIN_PASSWORD` in the API service's secret environment variables with a unique password of at least 24 characters. Never put this value in source control. The allowed website origin is `https://pulse.xyz.ci`; keep it aligned with the static site's custom domain. The API verifies uploaded license codes using only the public key at `LicensingCore\pulse-license-public-key.pem`.

The static site files are in `website`; downloads remain under `website\downloads`. Add `pulse.xyz.ci` to the static site's custom domains and configure the DNS records shown by Render. The API is expected at `https://pulse-license-api.onrender.com`; update the `pulse-license-api` meta tag on both website pages if the Render service domain changes.

Users paste the device ID from PULSE into the request form. The site returns a private request receipt; the user can keep it in the same browser session or copy and restore it later. The API stores only a hash of this receipt token. Request and signed license records are retained for 180 days; delete requests from the issuer page when they should be removed sooner. No email address or payment details are collected.

For each app release, update the version in `ValorantLauncher.csproj` and `Installer\Pulse.iss`, publish the app and installer, and update the version, file size, and update date on `website\index.html` before deployment.
