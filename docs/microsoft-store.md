# Publishing OficinaDiag on the Microsoft Store

Complete guide to submitting the app as **free** on the Microsoft Store.
Store-distributed, the app installs with one click, updates itself and
**doesn't trigger SmartScreen** (Microsoft signs the package).

> Status: **published**. Live listing at
> <https://apps.microsoft.com/detail/9P2BM91SFKFM> (publisher: individual
> Partner Center account). This guide stays as reference for **submitting
> new versions** — each release = new `tools\build-store.ps1` + new
> submission (review ~24–72h), starting at step 3.

---

## 1. Partner Center account (once)

1. Go to <https://partner.microsoft.com/dashboard> → **Apps and games**.
2. Create a developer account:
   - **Individual** — free; the Store shows your personal name as publisher.
   - **Company** — paid (~$99 once) and requires company validation; shows
     "Oficina de Sistemas" as publisher. Recommended if we want the company
     name on the Store.
3. Under **Apps → New app**, reserve the name **`OficinaOS Diag`**.

## 2. Copy the identity into the manifest

In Partner Center: your app → **Product management → App identity**. Copy:

| Field | Into |
|---|---|
| `Package/Identity/Name` | `Name` in `packaging/store/AppxManifest.xml` |
| `Package/Identity/Publisher` | `Publisher` in the same file |

And replace `REPLACE_PUBLISHER_DISPLAY_NAME` with the legal name (what
appears as publisher on the Store).

## 3. Build the package

```powershell
tools\build-store.ps1
```

Does a self-contained publish, generates the resource index (scale-200
tiles) and produces in `out-store\`:

- `OficinaOSDiag_<version>_x64.msix` — the package itself
- `OficinaOSDiag_<version>_x64.msixupload` — **this is what you upload**

Notes:

- The `.msix` goes **unsigned** on purpose — the Store signs at publish.
- The version comes from `Version` in the `.csproj`; MSIX requires 4
  segments (`0.1.9` → `0.1.9.0` automatically).
- To test locally before the Store you must sign with a test certificate —
  for fast iteration the normal ZIP still works.

## 4. Submission — what to fill in

| Step | What to put |
|---|---|
| **Packages** | Upload the `.msixupload` |
| **Properties** | Category: **Utilities & tools** |
| **Age ratings** | IARC questionnaire — no sensitive content → **Everyone / 3+** |
| **Pricing** | **Free** |
| **Availability** | All markets; publish when approved |
| **Store listings** | PT + EN minimum (ES optional) — texts below |
| **Privacy policy** | Mandatory public URL — use `https://cloud.oficinaos.app/privacy.html` (already exists) or the "Privacy" section of the README |
| **Notes for certification** | Text below — includes the `runFullTrust` justification |

### Why `runFullTrust` (justification for review)

The Store detects the restricted capability and asks for a written
justification. Ready-to-paste text (English — the review is done in English):

> This is a packaged classic (desktop bridge) app. It diagnoses phones over
> USB by spawning bundled child processes: Google's `adb.exe`
> (platform-tools) for Android and the libimobiledevice toolset for iOS.
> Child process spawning requires `runFullTrust`. The app is a free,
> open-source utility (MIT) — source: github.com/braindeadpt/oficinaos-diag.

### Test notes for review

> The app works standalone without a phone: the main window, scan history,
> HTML/JSON export and settings all function without a device. USB scans
> require an Android phone with USB debugging or an iPhone with the Apple
> USB driver (iTunes / Apple Devices app). The on-device screen test serves
> a page on the local network (privateNetworkClientServer). Optional cloud
> features (send-to-shop, AI report) are opt-in buttons only.

## 5. Listing text (PT + EN)

**Name:** OficinaOS Diag
**Short description (PT):** Diagnóstico de telemóveis por USB — bateria, ecrã, sensores. Grátis e local.
**Short description (EN):** USB phone diagnostics — battery, screen, sensors. Free and local.

**Long description (PT):**

> O OficinaOS Diag lê qualquer Android ou iPhone ligado por cabo: modelo,
> bateria (nível, ciclos, capacidade real), ecrã, sensores e armazenamento.
> O scan corre 100% no PC — nada sai sem autorização. Inclui teste de ecrã
> e de toque no próprio telemóvel, exportação em HTML/JSON e histórico de
> scans. Ferramenta gratuita do ecossistema OficinaOS, open source (MIT).
> iPhone: requer o driver USB da Apple (iTunes ou app «Apple Devices»).

**Long description (EN):**

> OficinaOS Diag reads any Android or iPhone over USB: model, battery
> (level, cycles, real capacity), screen, sensors and storage. Scans run
> 100% locally — nothing leaves the PC without permission. Includes an
> on-device screen and touch test, HTML/JSON export and scan history. A
> free tool from the OficinaOS ecosystem, open source (MIT). iPhone
> requires the Apple USB driver (iTunes or Apple Devices app).

**Screenshots (required ≥1, 1366×768 or larger):** main window, scan
result, test page on the phone. Taken from the running app.

## 6. After publishing

- The app installs from the Store with one click — no SmartScreen, and
  updates automatically for everyone.
- **Each new version** = new `build-store.ps1` + new submission (review
  ~24–72h). The ZIP on GitHub Releases stays for those who prefer portable.
- History (`%APPDATA%\OficinaDiag`) migrates to the MSIX package's private
  space — switching from ZIP to Store starts with an empty history.
