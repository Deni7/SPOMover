# SPOMover — user guide

Українська версія: [USER_GUIDE.uk.md](USER_GUIDE.uk.md)

Version 0.2 (stage 2 — mapping and inventory), 06.11.2026.

SPOMover splits a large SharePoint Online library (millions of files) into several libraries of the same site: folders from different places of the source are moved into the chosen libraries according to a mapping table — in a controlled way, in waves, with handling of locked files and resuming after a failure.

**What already works (stages 1–2):**
- **inventory** of the source and target libraries: a complete list of files, totals for every folder, checked-out files;
- **mapping** "source folder → library and folder" with validation while you type, import and export;
- **coverage check**: what goes where, what stays, name conflicts, paths that are too long.

The plan and moving come in later stages ([3.2](#32-what-comes-in-later-stages)). For now SPOMover changes nothing in SharePoint.

The guide has three parts:
- **Part 1** — what to know before you start: concepts, installation, access to SharePoint, starting the app.
- **Part 2** — step-by-step scenarios.
- **Part 3** — reference: every field of every screen, statuses, reports, CLI commands.

---

## Contents

- [Part 1. Before you start](#part-1-before-you-start)
  - [1.1. How it works](#11-how-it-works)
  - [1.2. Key concepts](#12-key-concepts)
  - [1.3. Safety guarantees](#13-safety-guarantees)
  - [1.4. Requirements and installation](#14-requirements-and-installation)
  - [1.5. Setting up access to SharePoint (once)](#15-setting-up-access-to-sharepoint-once)
  - [1.6. Starting the app](#16-starting-the-app)
  - [1.7. The app window](#17-the-app-window)
- [Part 2. Scenarios](#part-2-scenarios)
  - [S1. First-time setup of access to SharePoint](#s1-first-time-setup-of-access-to-sharepoint)
  - [S2. A profile for the libraries](#s2-a-profile-for-the-libraries)
  - [S3. Inventory](#s3-inventory)
  - [S4. Mapping and coverage check](#s4-mapping-and-coverage-check)
  - [S5. Pause, stop, closing the window, reboot](#s5-pause-stop-closing-the-window-reboot)
  - [S6. Scheduled runs from the command line](#s6-scheduled-runs-from-the-command-line)
  - [S7. Moving the settings to another machine](#s7-moving-the-settings-to-another-machine)
- [Part 3. Reference](#part-3-reference)
  - [3.1. Connection](#31-connection)
  - [3.2. What comes in later stages](#32-what-comes-in-later-stages)
  - [3.3. Profile](#33-profile)
  - [3.4. New run](#34-new-run)
  - [3.5. How long inventory takes](#35-how-long-inventory-takes)
  - [3.6. Dashboard and run page](#36-dashboard-and-run-page)
  - [3.7. File table, errors, file page](#37-file-table-errors-file-page)
  - [3.8. Statuses](#38-statuses)
  - [3.9. Error classes and what to do](#39-error-classes-and-what-to-do)
  - [3.10. Reports](#310-reports)
  - [3.11. Command line (CLI)](#311-command-line-cli)
  - [3.12. App files and folders](#312-app-files-and-folders)
  - [3.13. Stage 2 limitations](#313-stage-2-limitations)
  - [3.14. If something goes wrong](#314-if-something-goes-wrong)
  - [3.15. Mapping coverage check](#315-mapping-coverage-check)
  - [3.16. Move plan](#316-move-plan)
  - [3.17. Moving (apply)](#317-moving-apply)
  - [3.18. Checking the target libraries](#318-checking-the-target-libraries)
  - [3.19. Deleting empty source folders (EXE-7)](#319-deleting-empty-source-folders-exe-7)
  - [3.20. Pilot report](#320-pilot-report)

---

# Part 1. Before you start

## 1.1. How it works

The work goes in three steps; stages 1–2 provide the first two.

**1. Inventory** — for the source library and every target library:

1. Resolves the site, the library and (if set) the folder, and checks that the app has access to them.
2. Gets the list of all files and folders of the library through Microsoft Graph (delta), page by page.
3. Writes every page to the state database together with the position to continue from. After a stop or a failure the listing continues from the same page, not from the beginning.
4. Stores the path, size, format, and who changed each file and when.
5. At the end calculates for every folder: how many files and how much data it holds including all subfolders, and the longest path.
6. Checked-out files and who holds them come with the listing (Graph returns this for every file). Separately, through SharePoint REST, the source is queried for files without any published version — the listing does not see them. SharePoint gives this list only with the `fullcontrol` role on the site; without it (or without the SharePoint `Sites.Selected` permission) the step is skipped with a note, and the listing is still complete.

File contents are not downloaded, nothing changes in SharePoint.

**2. Mapping and coverage check.** In the profile you set which source folder goes to which library and folder. The coverage check applies the mapping to every file of the latest inventory. It shows what goes where and what stays in the source, and finds problems that show only on real files: name conflicts, paths that are too long, checked-out files.

**3. Plan and moving** — later stages ([3.2](#32-what-comes-in-later-stages)).

## 1.2. Key concepts

| Concept | What it is |
|---|---|
| **Connection** | An app registered in Entra (Azure AD) and the certificate it uses to sign in to SharePoint. The app gets access only to the sites explicitly granted to it. |
| **Source library** | The library being split: site + library (+ optionally a folder that limits the inventory). |
| **Target libraries** | Libraries of the same site that folders are moved to. They must exist before you start. |
| **Mapping** | A table "path in the source → library and path in it". For every file the row with the longest match applies. Files not covered by any row stay in the source. |
| **Coverage check** | Applying the mapping to the latest inventory: how many files and how much data each row moves, what stays, name conflicts, paths that are too long ([3.15](#315-mapping-coverage-check)). |
| **Profile** | Everything for a run: source, target libraries, mapping, speed, retries, connection. |
| **Run** | One execution of a profile. It has a number, a state and its own history; it continues after a stop. |
| **Mode** | For now `inventory`. `plan`, `apply`, `verify` come later. |
| **Settings snapshot** | A copy of the profile and connections taken when the run is created. The run works only with the snapshot: later changes to the profile do not affect it. |
| **RU (resource units)** | The units SharePoint measures load in. Every app has an RU limit per minute and per day. The app itself makes sure it does not exceed the configured share of the limit. |

## 1.3. Safety guarantees

- **Inventory and the mapping check change nothing in SharePoint** and do not download file contents.
- **Nothing is lost after a stop or a failure:** every page of the listing is saved together with the position to continue from.
- **Every file gets a final status;** files whose folder could not be determined go to "Needs attention".
- **Care for SharePoint.** The app works below the load limit. If SharePoint asks to wait, the pause applies to the whole app. If SharePoint answers only 503 for a long time, the run stops by itself.
- **Access only to granted sites** (`Sites.Selected`), with no password or secret in the settings.

## 1.4. Requirements and installation

**Machine:**
- Windows 10/11 or Windows Server with Desktop Experience;
- WebView2 Runtime (Windows 10/11 already have it; the installer checks that it is present);
- .NET is not needed separately: it is part of the package;
- access to `login.microsoftonline.com`, `graph.microsoft.com` and (from stage 4) `<tenant>.sharepoint.com`.

A dedicated VM with automatic sign-in of the account is recommended: an inventory of a large library takes hours. Disk space for the data folder — from 1 GB per million files.

**Installing with the installer** — `SPOMover-<version>-x64.msi`:

- installs the app into `C:\Program Files\SPOMover` (administrator rights required) and adds a shortcut to the Start menu;
- one folder holds the app `SPOMover.exe`, the CLI `spo-mover.exe` and this guide (`docs\`);
- a newer version installs over the old one, without uninstalling;
- data (connections, profiles, run state, logs) is kept separately, in `%LOCALAPPDATA%\SPOMover`. Updating and uninstalling the app do not touch it.

Silent installation (for example, through Intune or SCCM):

```bash
msiexec /i SPOMover-0.1.0-x64.msi /qn
```

**Without installation** — `SPOMover-<version>-x64-portable.zip`: unpack into any folder (for example, `C:\Tools\SPOMover`) and run `SPOMover.exe`. No administrator rights required.

**Building a release from source** (on a developer machine, .NET 10 SDK required):

```bash
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

The script runs the tests, publishes the app and the CLI together with .NET and builds the MSI and the zip in `out\release`.

**Checking the installation without looking at the screen:**

```bash
SPOMover.exe --selftest C:\Temp\selftest.txt
```

The file must contain `RESULT ok`.

**Access to SharePoint** is set up by an administrator once: a certificate, an app in Entra with the `Sites.Selected` permission, access granted to the site. Step by step — [1.5](#15-setting-up-access-to-sharepoint-once). Then — scenario [S1](#s1-first-time-setup-of-access-to-sharepoint).

## 1.5. Setting up access to SharePoint (once)

SPOMover signs in to SharePoint as an Entra app (app-only), without a user: an app in Entra + a certificate. There is no password or secret in the settings — the certificate's private key is only in the Windows certificate store on the VM; only the public part is uploaded to Entra.

Order: certificate on the machine → app in Entra → access to the sites → connection in SPOMover.

### Step 1. Certificate

You don't need to buy a certificate anywhere: a self-signed one is fine for an app signing in to Entra. It is created on the machine where SPOMover will run, **under the account that will run it** (otherwise the app won't see the key), or in the machine store with access for that account (Option B). If the company has an internal certification authority and the policy requires it, the certificate can be issued there; the requirements are the same: RSA 2048+, with a private key, in the `My` store.

**Option A — the app is run by a regular user** (the `CurrentUser` store). PowerShell under that account, no administrator rights required:

```powershell
$cert = New-SelfSignedCertificate -Subject "CN=spo-mover-1" -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 2048 -KeySpec Signature -KeyExportPolicy NonExportable -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(2)
```

**Option B — one certificate shared by several accounts of the machine** (the `LocalMachine` store). PowerShell as administrator, `Cert:\LocalMachine\My` instead of `Cert:\CurrentUser\My`. Then give the accounts that need it access to the key: `certlm.msc` → Personal → Certificates → the certificate → All Tasks → Manage Private Keys → add the account with the **Read** permission.

Then, in the same PowerShell window — the thumbprint for the connection and the public part for Entra:

```powershell
$cert.Thumbprint
```

```powershell
Export-Certificate -Cert $cert -FilePath .\spo-mover-1.cer
```

**Where the key is, and where the `.cer` is.** The private key is created together with the certificate and stays only in the store where `New-SelfSignedCertificate` was run; SPOMover takes the certificate from there. The `.cer` contains only the public key and is needed only for Entra (step 2) — it can be sent to the admin by e-mail. **Do not import the `.cer` into the Windows store:** that creates a copy of the certificate without the private key, and the app will show it as "(no private key)".

`NonExportable` means the private key cannot be exported (neither to a `.pfx` nor otherwise): the certificate cannot be moved to another VM or another account. For another account on this machine — Option B (`LocalMachine` and the **Read** permission on the key); for a new VM — a new certificate, added to the app in Entra next to the old one. To find the thumbprint and check the key later: `Get-ChildItem Cert:\CurrentUser\My | Format-List Subject, Thumbprint, HasPrivateKey` (or `LocalMachine\My`).

The connection's certificate list ([3.1](#31-connection)) marks: "(no private key)" — this store has only the public part; "(no access to the key)" — the key exists, but the account the app runs under has not been given the **Read** permission (Option B).

The certificate is valid for 2 years. Before it expires, create a new one, add it to the app next to the old one, change the certificate in the connection, then delete the old one. The app refuses an expired certificate with the error `failed:auth`.

### Step 2. App in Entra

An Entra admin is needed (Application Administrator or Global Administrator).

1. [Entra admin center](https://entra.microsoft.com) → **App registrations** → **New registration**: a name, for example `spo-mover-1`; **Accounts in this organizational directory only**; no Redirect URI needed.
2. On the **Overview** page note the **Application (client) ID** (the connection's **Client ID** field) and the **Directory (tenant) ID** (the **Tenant** field; or the `…onmicrosoft.com` domain).
3. **Certificates & secrets** → **Certificates** → **Upload certificate** → the `.cer` file from step 1. The thumbprint shown in Entra must match `$cert.Thumbprint`.
4. **API permissions** → **Add a permission** → **Microsoft Graph** → **Application permissions** → **Sites.Selected** → **Add**. The `User.Read` (Delegated) permission added by default can be removed.
5. **Grant admin consent for …** — the status must turn green "Granted".

Sites.Selected by itself gives no access to any site — only to those granted explicitly in the next step.

**The second permission — SharePoint.** **API permissions** → **Add a permission** → **SharePoint** → **Application permissions** → **Sites.Selected** → **Add** → **Grant admin consent**. Folders are moved (stage 4) and files without a published version are queried (stage 2) through SharePoint REST; the Microsoft Graph permission does not extend to SharePoint REST. The site access granted in step 3 applies to both permissions. Without this permission the inventory works, but the check-out search is skipped with a note.

### Step 3. Access to sites

After step 2 the app has the `Sites.Selected` permission but **no access to any site yet**: access is granted separately for every site. Until that is done, the connection test gets a token, but the library answers `403`.

**What to grant.** A role on the **site collection** that holds the source library and the target libraries (an address like `https://contoso.sharepoint.com/sites/archive`). The role applies to all libraries and subsites of the collection; it does not change library permissions (inheritance is not broken).

| Role | Is it suitable |
|---|---|
| `read` | Enough for inventory (stage 1); moving will end with `403`. |
| `write` | **Required for moving** (stage 4). Can be granted right away. |
| `owner`, `fullcontrol` | Not needed for inventory and moving. `fullcontrol` is needed to see files without a published version (with `write` SharePoint answers 403), and also for check-in on behalf of an administrator and temporarily freezing the library, if they get implemented (FEATURES, LCK-3, LCK-4). |

**Who grants it.** A SharePoint admin or a Global Administrator — an account that can consent to the `Sites.FullControl.All` permission (that is what is needed to grant access to sites). This permission is **not added** to the `spo-mover-1` app itself, nor is `Sites.ReadWrite.All`: with them the per-site restriction no longer applies.

You will need the app's **Client ID** from step 2 and the site addresses. Below are three equivalent methods — pick whichever suits the admin.

#### Method 1. Graph Explorer (in the browser, nothing to install)

1. Open [Graph Explorer](https://developer.microsoft.com/graph/graph-explorer) and sign in with the admin account.
2. **Modify permissions** (tab above the request) → find `Sites.FullControl.All` → **Consent**. If the button is unavailable, Global Administrator consent is needed.
3. Get the site id: method **GET**, the address below → **Run query** → copy the `id` field from the response (looks like `contoso.sharepoint.com,5a58…,9f2e…`).

```http
GET https://graph.microsoft.com/v1.0/sites/contoso.sharepoint.com:/sites/archive
```

4. Grant access: method **POST**, the address with that `id`, in the **Request body** — the JSON below (put in the Client ID) → **Run query**. The response is `201 Created`.

```http
POST https://graph.microsoft.com/v1.0/sites/{site id}/permissions
Content-Type: application/json

{
  "roles": ["write"],
  "grantedToIdentities": [{ "application": { "id": "<app Client ID>", "displayName": "spo-mover-1" } }]
}
```

5. Check: **GET** `https://graph.microsoft.com/v1.0/sites/{site id}/permissions` — the list has an entry with `spo-mover-1` and the `write` role.

Repeat steps 3–5 for every site.

#### Method 2. PnP.PowerShell (handy for many sites)

Requires PowerShell 7.4+ and the module `Install-Module PnP.PowerShell -Scope CurrentUser`. To sign in, PnP needs its own app in Entra — it is registered once (a Global Administrator consents to the permissions), and then its Client ID is used:

```powershell
Register-PnPEntraIDAppForInteractiveLogin -ApplicationName "PnP PowerShell" -Tenant contoso.onmicrosoft.com
```

Grant access (sign in to any site of the tenant; `-Site` sets the site for the permission):

```powershell
Connect-PnPOnline -Url https://contoso.sharepoint.com -Interactive -ClientId <PnP app Client ID>
```

```powershell
Grant-PnPEntraIDAppSitePermission -AppId <spo-mover-1 Client ID> -DisplayName spo-mover-1 -Site https://contoso.sharepoint.com/sites/archive -Permissions Write
```

Several sites at once:

```powershell
"https://contoso.sharepoint.com/sites/archive", "https://contoso.sharepoint.com/sites/fs-new" | ForEach-Object { Grant-PnPEntraIDAppSitePermission -AppId <spo-mover-1 Client ID> -DisplayName spo-mover-1 -Site $_ -Permissions Write }
```

Check: `Get-PnPEntraIDAppSitePermission -Site https://contoso.sharepoint.com/sites/archive`. Older PnP versions have the same commands named `…PnPAzureADAppSitePermission`.

#### Method 3. Microsoft Graph PowerShell

```powershell
Connect-MgGraph -Scopes Sites.FullControl.All
```

```powershell
$site = Get-MgSite -SiteId "contoso.sharepoint.com:/sites/archive"
New-MgSitePermission -SiteId $site.Id -BodyParameter @{ roles = @("write"); grantedToIdentities = @(@{ application = @{ id = "<spo-mover-1 Client ID>"; displayName = "spo-mover-1" } }) }
```

Check: `Get-MgSitePermission -SiteId $site.Id`.

#### After granting

- **Check in SPOMover:** **Connections → Test connection** with a profile that contains this site. Every library must be green. If access has just been granted and the test still gives `403`, retry in a few minutes.
- **A new site in the profile** — first grant access to it the same way, otherwise the run will end with an access error.
- **Site search** in the profile editor may not find sites that have not been granted — enter the full site address.
- **Revoke access** when the work is done: in Graph — `DELETE https://graph.microsoft.com/v1.0/sites/{site id}/permissions/{permission id}` (`permission id` — from the response of GET `…/permissions`); in PnP — `Revoke-PnPEntraIDAppSitePermission -PermissionId <id> -Site …`; in Graph PowerShell — `Remove-MgSitePermission`. To disable the app for all sites at once, revoke the consent to `Sites.Selected` in Entra (step 2).

### Step 4. Connection in SPOMover

In the app window — scenario [S1](#s1-first-time-setup-of-access-to-sharepoint): **Connections → Create**, tenant, Client ID, certificate from the store, **Test connection**. For several apps in a pool — one connection per app, with different names.

From the command line — import a JSON (`tenantId`, `clientId`, `certificateThumbprint`, `certificateStore` — `CurrentUser` or `LocalMachine`, as in step 1, and `licenses`) and test it: `test` gets a token and tries to read every library of the profile.

```bash
spo-mover connections import connection.json
```

```bash
spo-mover connections test spo-mover-1 --profile split
```

**If the test fails:**
- "Certificate … not found in the store" — the app is running under a different account than the one the certificate was created under, or the wrong store is selected;
- "no private key" — the selected store has only an imported `.cer`; select the store where the certificate was created, or create a new one (a `.cer` is not imported into the store);
- "No access to the private key" — in option B the app's account has not been given Read on the key;
- `AADSTS700027` / `AADSTS700016` — the certificate has not been uploaded to the app, or the Client ID / tenant is wrong;
- there is a token but `403` on the library — admin consent to Sites.Selected has not been given (step 2), or access to this site has not been granted (step 3).

## 1.6. Starting the app

```bash
SPOMover.exe --data D:\SPOMover
```

| Parameter | What it is | Why, and the effect | Default |
|---|---|---|---|
| `--data <folder>` | The data folder: the state database and logs. | All settings and runs live here. Another folder is a separate, independent data set. For millions of files — on a disk with enough space. | `%LOCALAPPDATA%\SPOMover` |
| `--urls <address>` | The address of the local API for the CLI. | Usually not needed. If the port is busy (for example, another user of this machine has already started the app), the API takes a free port and writes the address to `api.json` in the data folder — the CLI of the same user finds it by itself. An explicit `--urls` is needed only for a fixed address; if it is busy, the app warns and names the actual one. | `http://localhost:5090` |
| `--selftest <file>` | Self-test: start, open the sections, write the result, exit. | Checking an installation or an update. | — |

One instance runs per data folder: starting it again opens the window that is already running.

**Several users on one machine.** Each user runs the app under their own account: they have their own data folder (`%LOCALAPPDATA%\SPOMover`), their own settings, runs and certificate. The first instance listens on `localhost:5090`, the next ones on free ports; each user's CLI finds its own instance through `api.json`. The API accepts only requests with the key of its own instance (it is also in `api.json`), so another user of the machine cannot control someone else's runs. Do not put the data folder (`--data`) in a folder shared by several accounts: others could read the key from it.

**Autostart on a VM.** For a run to continue after a reboot, turn on automatic sign-in of the account and add a Task Scheduler task "At log on" with the command `SPOMover.exe --data D:\SPOMover`. A run interrupted by a failure or by the process being killed is resumed by the app itself. A run stopped by a normal exit from the app has the state "stopped" and is continued with the **Resume run** button (or `spo-mover start <run>`). Before a planned reboot of the VM, stop the run, or check after the reboot that it has resumed.


## 1.7. The app window

On the left is the menu:
- **Dashboard** — the current run (or the latest one, if nothing is running now);
- **Runs** — the list of runs and the "New run" button;
- **Plan and move** — for every profile: the latest inventory, the latest plan (marked "outdated" if the inventory is newer) and the latest move; the **Plan…** button ([3.16](#316-move-plan));
- **Settings:** **Profiles**, **Connections**;
- **Help** — this guide inside the app: sections as a tree, search, "previous / next" buttons.

Next to screen titles and on every profile tab there is a **?** button ("What these fields mean", "Help"). It opens, in a window, the section of the guide about exactly these fields; **Open in help** — the same section on the help page. The **?** button in the top right corner opens the help from the beginning.

Every settings list (profiles, connections) has:
- **Create**, **Import JSON**;
- for every row — export to JSON, rename (references in profiles are updated), clone and delete (the app warns which profiles use a connection).

Closing the window during a run — see [S5](#s5-pause-stop-closing-the-window-reboot).

---

# Part 2. Scenarios

## S1. First-time setup of access to SharePoint

1. The administrator does steps 1–3 of [1.5](#15-setting-up-access-to-sharepoint-once): certificate, app in Entra with `Sites.Selected`, access to the site.
2. **Connections → Create:**
   - a name, for example `spo-mover-1`;
   - **Tenant** and **Client ID** from Entra;
   - a certificate from the list (store and thumbprint are filled in automatically);
   - the tenant's license range.
3. **Save**, then **Test connection**. The "token" row must be green.
4. When the profile is ready ([S2](#s2-a-profile-for-the-libraries)) — **Test connection** with that profile: every library must be green.

Fields — [3.1](#31-connection).

## S2. A profile for the libraries

1. **Profiles → Create.** On the **Libraries** tab — the profile name.
2. **Select the source library:** the site address (or search), the library from the list. The folder is usually not set: the inventory must see the whole source.
3. **Add libraries** — the target libraries of the same site: tick them in the list or enter the names. Create them in SharePoint beforehand: the inventory lists them too, to find conflicts with what is already there.
4. **Mapping** can be filled in now or after the inventory ([S4](#s4-mapping-and-coverage-check)): mapping errors do not prevent the inventory.
5. **Performance:** for the first run — the **Careful** or **Off-hours** preset.
6. **Connection:** select the connection from [S1](#s1-first-time-setup-of-access-to-sharepoint).
7. **Validate**, then **Save**. Errors are shown as a counter on the tabs.

Fields — [3.3](#33-profile).

## S3. Inventory

1. **Runs → New run** (or **Profile → Run…**): profile, mode `inventory`. If there has already been an inventory, the dialog shows its result: files and size.
2. **Start.** The run page opens ([3.6](#36-dashboard-and-run-page)):
   - "Files found" and "Size" grow with every page of the listing;
   - "Listing speed" — files per minute;
   - load on SharePoint — per app.
3. When it is finished:
   - the **By library** breakdown (the stage of each — "waiting", "listing ≈ X %", "done"; files, size, folders) and **By format** (files, size, share of files) — how much of what is where;
   - **Files** — the complete list with filters and search; export to CSV or XLSX;
   - **Errors and attention** — files with the status `failed:*` (usually there are none);
   - the **Checked out** card and the **Check-out** report — whom to ask to release files before moving;
   - **Reports → Folders** — every folder with the number of files, size and the longest path: the basis for the mapping;
   - **Reports → Summary** — a short report for approval, with the largest top-level folders.

For 1.5 million files the inventory takes hours ([3.5](#35-how-long-inventory-takes)); it can be paused, stopped and resumed ([S5](#s5-pause-stop-closing-the-window-reboot)).

**Repeated inventory** — a new run of the same profile; the estimates in the profile and in the new run dialog, the coverage check and the plan use the latest one. There are two ways:

- **Full** — the libraries are listed again.
- **Changes only** (a checkbox in the new run dialog, or `spo-mover inventory <profile> --update`). The new run starts from the result of the latest completed inventory and asks SharePoint only for what has changed since then: new, changed, renamed, moved and deleted files and folders. Paths of files under a renamed or moved folder are recalculated. Libraries that were not in the previous run (added to the profile later) are listed in full. The previous run does not change. The run's note shows how many changes were applied.

On `fs-test` (65 thousand files) an update after ≈ 36 thousand changes took 41 s, a full inventory — 2 min 47 s, and the results matched down to the file. Before every wave an update with changes only is enough.

## S4. Mapping and coverage check

1. Take **Reports → Folders** of the latest inventory (CSV) and decide with the owners which folder goes where.
2. **Profile → Mapping:** a row for every folder (or file) that is moved:
   - **Source** — the path from the root of the source library: `Finance/2023`. `/` — the whole library;
   - **Library** — one of the targets, or **keep in source** (an exception inside a folder that is moved);
   - **Path in library** — where to: empty — the same path as in the source; `/` — the root of the library; `2023` — the folder `2023`.

   A large mapping is easier to prepare in Excel (a sample — the **Sample CSV** button) and load with the **Import** button or the `spo-mover map import` command ([3.11](#311-command-line-cli)).
3. Errors show right next to the rows: an unknown library, the same source twice with different targets, invalid characters. **Where will a file or folder go** — checks one path.
4. **Save**, then **Check coverage…** ([3.15](#315-mapping-coverage-check)):
   - **No rule** — what will stay in the source; if that is not intended, add rows;
   - **Name conflicts** and **Paths too long** — fix the mapping (another folder, a shorter path) or the files in SharePoint; the list — the **Problems** report;
   - **Checked out** — ask the owners to release the files (the **Problems** report, type `checked_out`).
5. Repeat 2–4 until no errors remain. The **Where each file goes** report — for approval with the owners and for SPOReplaceLink.

The check does not see changes made in SharePoint after the inventory: before approval, run the inventory again.

6. When no errors remain — the **plan**: `spo-mover plan build <profile> -o <folder>` ([3.16](#316-move-plan)). It shows the batches, the waves, the folders that will be created in the target and the time forecast. The `batches` report — for approval. In the window — **Profiles → [profile] → Plan…**.

## S5. Pause, stop, closing the window, reboot

| Action | What happens |
|---|---|
| **Pause** | New pages of the listing are not requested; the current one completes. **Resume** — from the same place. |
| **Stop** | The current page completes, the position is saved, the state is "stopped". **Resume run** — from the same place. |
| Closing the window during a run | The app asks: **minimize to tray** (the run goes on; the window — double-click the icon next to the clock) or **stop the run and exit**. |
| Failure, process killed, reboot | The run that was running resumes by itself on the next start of the app — from the last saved page. |
| Outside the working windows ([Performance tab](#performance-tab)) | The run waits for a window and resumes by itself. |
| Daily RU budget used up | The run waits until the budget recovers and resumes by itself. |

**Delete run** (a button on the run page, only for a run that is not running): deletes the run's file list, statuses, attempts and metrics. Nothing changes in SharePoint.

## S6. Scheduled runs from the command line

The CLI works through the open app of the same account ([3.11](#311-command-line-cli)). An example for Task Scheduler — a nightly inventory with waiting and reports:

```bash
spo-mover inventory split --wait
```

```bash
spo-mover export 12 -o D:\Reports\run-12
```

Exit code `0` — completed without errors, `1` — there are files that need attention, `2` — a settings error or the app is unavailable, `3` — the run was stopped or interrupted.

## S7. Moving the settings to another machine

1. On the old machine: **Profiles** and **Connections** → export JSON (or `spo-mover profiles get split -o split.json`).
2. On the new machine — a new certificate ([1.5, step 1](#step-1-certificate)), added to the same app in Entra.
3. Import the connection JSON, then fix the certificate (select the new one from the list); import the profiles.
4. **Test connection** with a profile.

Runs are not moved: the state database (`state.db`) stays on the old machine. To move with the history, copy the whole data folder while the app is closed.

---

# Part 3. Reference

For every field, the name in the profile or connection JSON is given in brackets (for import, export and the CLI).

## 3.1. Connection

**Connections → [name]**

| Field (JSON) | What it is | Why, effect and result | Default |
|---|---|---|---|
| Name (`name`) | The name of the connection in the app. | Profiles refer to the connection by it. Renaming updates the references in profiles. | — |
| Tenant (`tenantId`) | The Directory (tenant) ID from Entra, or the `…onmicrosoft.com` domain. | Where the app signs in. A wrong value — a sign-in error `AADSTS…`. | — |
| Client ID (`clientId`) | The Application (client) ID of the app in Entra. | Which app to sign in as. Must be a GUID. | — |
| Authentication (`authMode`) | `certificate` — the app signs in with a certificate, without a user; `deviceCode` — a user signs in with a code. | Certificate is the main way: works without a person, on a schedule. Code is a fallback: the rights are then limited to the user's rights. | `certificate` |
| Certificate from the Windows store | A list of certificates in `CurrentUser\My` and `LocalMachine\My`, with their validity. | Selecting one fills in the thumbprint and the store. Expired ones and those without a private key are marked. | — |
| Thumbprint (`certificateThumbprint`) | 40 hexadecimal characters. | Which certificate to use. The private key stays in the Windows store; there are no secrets in the settings. | — |
| Store (`certificateStore`) | `CurrentUser` or `LocalMachine`. | Where to look for the certificate. | `CurrentUser` |
| Licenses in the tenant (`licenses`) | The range of the number of M365 licenses in the tenant. | Microsoft sets the SharePoint limits by it, and the app — its RU budget. A range that is too high leads to 429, too low — to slower work. | up to 1,000 |
| RU/min (`appPerMinuteOverride`) | A manual value of the app's per-minute limit. | If Microsoft has reported a different limit, or the pilot measured a different one. Empty — from the table. | empty |
| RU/day (`appPerDayOverride`) | A manual value of the daily limit. | The same for the daily limit. | empty |
| Tenant, RU per 5 min (`tenantPer5MinOverride`) | A manual value of the limit of the whole tenant. | For the ceiling of an app pool. | empty |
| Test connection | A button; optionally — a profile. | Checks the settings and the certificate, gets a token and, if a profile is selected, checks access to each of its libraries. | — |

## 3.2. What comes in later stages

| Stage | What comes |
|---|---|
| 2. Mapping and inventory | **Done.** Source and target libraries, mapping, coverage check, folder totals, checked-out files. |
| 3. Planner | **Done** (in the window — from stage 5). Splitting into batches (a whole folder, or files of one folder), waves with a time forecast, folders to create, conflicts, plan export (`spo-mover plan`, [3.16](#316-move-plan)). Every build is a separate snapshot; changes are shown compared to the previous one. Inventory with changes only (delta). The plan page in the window — stage 5. |
| 4. Executor | **Done** (in the window — from stage 5). `apply` (moving with SharePoint's own means, [3.17](#317-moving-apply)) with retries, deferred waves for locked files and reconciliation after failures; `verify`, `retry`, `export-rules` for SPOReplaceLink; checking the target libraries, deleting empty source folders, pilot report. Requires the **SharePoint → Sites.Selected** permission in the Entra app and the `write` role on the site. |

The full list — `docs/FEATURES.md` and `docs/IMPLEMENTATION_PLAN.md` in the repository.

## 3.3. Profile

**Profiles → [name]**. Buttons at the top:
- **JSON** — export;
- **Run…** — a new run with this profile (inventory);
- **Plan…** — saves the profile and opens the plan page ([3.16](#316-move-plan)): build, review, move;
- **Validate** — a full check, as before a start;
- **Save** — saves even with errors, but such a profile won't start.

Errors are shown as a counter on the tab and in a list above the fields.

### Libraries tab

| Field (JSON) | What it is | Why, effect and result | Default |
|---|---|---|---|
| Profile name (`name`) | Only for a new profile. | The name in lists, the CLI and reports. | — |
| Source site (`source.siteUrl`) | The site address `https://…sharepoint.com/sites/…`. | Site search works only for sites that have been granted access; the address can be entered in full. Target libraries are on the same site. | — |
| Source library (`source.library`) | The library name as in SharePoint (`Documents`), its URL name (`Shared Documents`) or the full address. | What to split. An unknown library stops the run with a list of the existing ones. | — |
| Source folder (`source.folder`) | A folder in the library, for example `Fin/2024`. | The inventory is limited to it. Mapping paths are still counted from the root of the library. | the whole library |
| Target libraries (`targetLibraries`) | Names of libraries of the same site. **Add libraries** shows the site's libraries (access required) or accepts names. | What the mapping can refer to. The inventory lists them too; a non-existent library stops the run. The source library cannot be a target. | — |
| Already contains ≈, files in the source ≈ | From the latest inventory of this profile, by library name. | The scale before the start. | not listed yet |

### Mapping tab

A mapping row (`mapping[]`):

| Field (JSON) | What it is | Why, effect and result | Default |
|---|---|---|---|
| Source (`source`) | A path in the source library from its root: a folder (`Finance/2023`) or a file. `/` — the whole library. | The row applies to this path and everything below it. For every file the row with the **longest** match applies, so the order of rows does not matter, and a nested row overrides the outer one. Case, `\` or `/` and `%20` do not matter; a match is only on a folder boundary (`Fin` does not match `Finance`). | — |
| Library (`library`) | One of the target libraries, or `-` (**keep in source**). | Where the subtree goes. `-` is an exception: this subtree stays in place even if the outer folder is moved. | — |
| Path in library (`path`) | The path in the target library that the source becomes. | Empty — the same path as in the source (`Finance/2023` → `Finance/2023`). `/` — the root of the library (the contents of the source go to the root). Any other value — a new name and place (`2023` or `Reports/2023`); for a file — the full new path with the name. | as in the source |
| Comment (`comment`) | Any text. | Who approved it, why. | — |

**Checks while you type:**

| Check | Type |
|---|---|
| Source or library not set; an address instead of a path; `.` or `..` in the path | error |
| The library is not among the targets, or it is the source library | error |
| The same source in several rows with different targets | error |
| Invalid characters in the target path (`" * : < > ? \|`), a dot at the end of a name, `_vti_`, `Forms` in the root | error |
| The same source twice with the same target; a row that repeats an outer one; a path for "keep in source" | warning |
| Several sources lead into one folder (the contents will be merged) | warning |

**Buttons:**
- **Import (CSV, XLSX)** — columns: source, library, path, comment (path and comment are optional). The separator is a tab, `;`, `|` or a comma; a header row is skipped. The existing rows can be replaced or added to;
- **CSV** — export;
- **Sample CSV** — a sample file for import: a header and rows of every kind (a whole folder, a "keep in source" exception, another path, the library root, a single file) with explanations in the comment column, already with the profile's target libraries. The separator is `;` — Excel opens it in columns;
- **Where will a file or folder go** — the result for one path;
- **Check coverage…** — saves the profile and opens [3.15](#315-mapping-coverage-check).

Mapping errors do not prevent the inventory, but moving (stage 4) won't start with them.

### Planning tab

| Field (JSON) | What it is | Why, effect and result | Default |
|---|---|---|---|
| Largest batch, items (`plan.maxBatchItems`) | How many files and folders a subtree may have for the folder to be moved with one request. | More — fewer batches, but each request is longer. SharePoint does not move a folder with 5,000 or more items, so from 1 to 4,999. | 1000 |
| Batches at once (`plan.parallel`) | How many batches are moved in parallel. | The speed grows almost proportionally; increase gradually while watching 429/503 on the dashboard. Divides the plan forecast. | 4 |
| Batch speed, files/s (`plan.filesPerSecond`) | The speed of one batch for the forecast. | Only a forecast; after the first wave put in the measured one (run dashboard, "Move speed" ÷ 60 ÷ batches at once). | 2.2 |
| Move open files (`move.bypassSharedLocks`) | Move files open in Word/Excel for the web. | Without it SharePoint refuses such a file (423), and it is deferred and retried in waves; a folder with such a file is then moved item by item. With it — the file moves right under the user editing it: they see no message, and their changes are kept (tested on a tenant). | off |
| Wait per item, s (`move.secondsPerItem`) | How long to wait for the answer to a folder move, on top of the request timeout, per item. | Protects large batches from a premature "no response". | 0.5 |

### Performance tab

**Presets** fill in the fields with one click (then save the profile):

| Preset | Requests at once | RU budget | Windows |
|---|---|---|---|
| Careful | 4 | 50% | no windows |
| Standard | 8 | 80% | no windows |
| Off-hours | 8 | 80% in the windows; outside the windows the run waits | Mon–Fri 20:00–07:00 and Sat–Sun all day; "Work only in windows" on |

| Field (JSON) | What it is | Why, effect and result | Default |
|---|---|---|---|
| Requests at once (`performance.concurrency`) | The limit of simultaneous requests of one app to SharePoint. | More — faster in stages with many requests; the inventory goes page after page, so it hardly affects it. 1…64. | 8 |
| RU budget, % of limit (`performance.budgetPercent`) | What share of the app's limit to use. | Lower — less impact on others and a lower risk of 429; higher — faster. This value applies outside the working windows. 1…100. | 80 |
| Request timeout, s (`performance.requestTimeoutSeconds`) | How long to wait for the answer to one request. | After that the request is retried. Minimum 5. | 120 |
| Time zone of windows (`performance.timeZone`) | The time zone the windows are set in. | Empty — the machine's time. | empty |
| Work only in windows (`performance.onlyInWindows`) | A switch. | Outside the windows the run waits; new pages of the listing are not requested. Requires at least one window. | off |
| Working windows (`performance.windows`) | A list: **Days** (Mon…Sun; empty — every day), **From** and **To** (HH:MM, `24:00` — end of day; a window can cross midnight and belongs to the day it starts), **Budget, %**. | A separate RU budget for off-hours, automatic pause and resume. | none |

### Retries tab

| Field (JSON) | What it is | Why, effect and result | Default |
|---|---|---|---|
| Attempts per request (`retry.requestAttempts`) | How many times in total one request to SharePoint is made (the first attempt + retries on network failures, 5xx, 429 with a pause per `Retry-After`). | More — more resilient to short failures, but longer waiting. If a page of the listing is still not received, the listing continues from the same page up to 5 more times. 1…20. | 6 |
| Batch attempts (`retry.fileAttempts`) | How many times to try a move batch that still has files left because of temporary errors. | After the last attempt such files are `failed:transient`. | 3 |
| Pauses between batch attempts, min (`retry.filePausesMinutes`) | The pause before the 2nd, 3rd… attempt of a batch. | Separated by ";". | 1; 5; 15 |
| Locked: retry every, min (`retry.lockedWaveMinutes`) | How often a batch tries checked-out files or files open for editing. | Waves of retries until the owner releases the file. | 10 |
| Locked: wait up to, min (`retry.lockedLimitMinutes`) | How long to wait for the release. | After that the file is `failed:locked`; then "Retry failed…". | 120 |
| Access denials in a row — stop (`retry.authFailuresToStop`) | How many batches in a row with 401/403 stop the run. | A safeguard: the app's rights were revoked or the certificate has expired. | 10 |
| Only 503 for this many min — blocking (`retry.blockedMinutes`) | How long only 503 answers mean a possible blocking of the app. | The run stops with the reason "possible SPO blocking", without further attempts. | 15 |

### Connection tab

| Field (JSON) | What it is | Why, effect and result | Default |
|---|---|---|---|
| Connection (`connections`) | One or several connections. | Which app to work with. Several — only with a pool. | — |
| App pool (`pool.enabled`) | Several apps in one run. Turned on with a warning. | Before the start it is checked that every app sees all the libraries. For one site a pool is not needed: Microsoft does not recommend several apps for one task. | off |
| Tenant budget ceiling, % (`pool.tenantCeilingPercent`) | The share of the whole tenant's limit that the whole pool may use. | Protects users and other apps. 1…100. | 50 |

## 3.4. New run

**Runs → New run** or **Profile → Run…**

| Field | What it is | Why, effect and result |
|---|---|---|
| Profile | The profile of the run. | The profile is validated right away; errors show in the dialog, and the run won't start with them. |
| Mode | `inventory` — a list of all files of the libraries, with no changes in SharePoint. | Moving (apply) is started from the plan page, deleting empty folders (cleanup) — from the move run page ([3.16](#316-move-plan), [3.19](#319-deleting-empty-source-folders-exe-7)). |
| Changes only since run N | Present when the profile already has a completed inventory. | An update with changes only (delta) instead of a full listing ([S3](#s3-inventory)). |
| One-off changes for this run | Requests at once, RU budget, "Work only in windows". **Reset to profile** — remove the changes. | Change the parameters of one run without changing the profile. The changes go into the run's snapshot. |
| Previous inventory | Files and size from the latest inventory of the profile. | To know the scale before the start. |

Only one run executes at a time: runs share the app's budget.

## 3.5. How long inventory takes

- **The load is small.** One page of the listing is one request; a page holds hundreds of items. For 1.5 million files that is thousands of requests — a fraction of a percent of the daily RU limit.
- **The time is set by SharePoint:** pages are requested one after another, each takes from a fraction of a second to a few seconds. For 1.5 million files expect hours. The exact speed is shown by the "Listing speed" card; use it as a guide for the next runs.
- **Fastest — off-hours:** SharePoint is less loaded and asks to wait less often.
- **Breaks don't cost a repeat:** after a pause, a stop or a failure the listing continues from the last saved page.

## 3.6. Dashboard and run page

**Header:**
- the run number, state (running, paused, stopped, interrupted, completed, completed with errors), mode, profile;
- for a stopped or interrupted run — the reason;
- the run's **notes**, for example "Files without a published version were not checked…" with the reason.

**Progress:**
- **Inventory** — "Scanned ≈ X%: N of ≈ M items", start, duration, time to completion; under the bar — the stage: "Library 2 of 3: "test2" — listing files and folders", then "Searching for files without a published version". Graph does not say in advance how many items are left, so the app takes the total number (files and folders) from SharePoint at the start of the inventory, for all libraries at once (the list's `ItemCount`). It is an estimate: on `fs-test` SharePoint reported 67,639 items, the listing found 69,371 (≈ 2.5% more), so the percentage is marked "≈" and does not reach 100 until the listing ends. When the number is unknown — an inventory of a library folder, an update with changes only — the bar just runs, without a percentage. When finished — "Listing complete: N files found".
- **Moving** — N of M plan files done (%), start, duration, time to completion.

**Cards:**

| Card | What it shows | What to look at |
|---|---|---|
| Files found | How many files have been listed (grows during the listing). | — |
| Size | The total size of the listed files. | — |
| Errors | Files with `failed:*`; the tooltip — by class. Clicking the card — the errors tab with errors only. | [3.9](#39-error-classes-and-what-to-do) |
| Checked out | Checked-out source files and files without a published version (check-outs in the target libraries — only in the report). | The "Check-out" report: whom to ask to release the files. |
| Deferred | Files waiting for the next attempt (only if there are any; they appear with stage 4). | — |
| Needs attention | Files with `failed:*`. Clicking the card — the errors tab with these files. | The "Needs attention" report ([3.10](#310-reports)). |
| Listing speed | Files per minute; the tooltip — the throttling state. | "paused" — SharePoint asked to wait. |

**Load on SharePoint** — a row for every app and a tenant row:

| Column | What it is |
|---|---|
| RU budget per minute | What share of the per-minute budget is used now. |
| RU for 24 h, RU total | Spent in the last day and since the run was last started. |
| Requests | How many requests have been made. |
| 429 / 503 | How many times SharePoint asked to wait or was overloaded. |
| Concurrency | The current limit of simultaneous requests (lowered after 429 and gradually restored). |
| State | working / paused until HH:MM:SS / possible blocking. |

**Trend** (a snapshot once a minute):
- "Progress" — files found;
- "Speed and errors" — files per minute between neighbouring snapshots; on the right axis — files with an error, retries and 429/503;
- hover the mouse — the values at that moment appear under the chart; select a range — both charts zoom in to it; "1 h", "6 h", "24 h", "All" or a double click — the time span;
- a gap in the speed line — the run was not working at that time (paused, the program was closed).

**Breakdowns:**
- by format — the 15 most common: files, size, processed, errors;
- by library — a block for every library: name (and folder), site address, the same numbers.

**Buttons:**
- **Pause / Resume / Stop**, **Start / Resume run** — [S5](#s5-pause-stop-closing-the-window-reboot);
- **Reports** — [3.10](#310-reports);
- **Delete…** — for a run that is not running.

**Tabs of the run page:**
- **Overview** — the dashboard;
- **Files**, **Errors and attention** — [3.7](#37-file-table-errors-file-page);
- **Run settings** — the snapshot of the profile and connections the run works with.

## 3.7. File table, errors, file page

**Files:**
- **filters** — status (including `attention — needs attention`), error class, format (the 30 most common in the run), library, search by part of the path;
- **sorting** — click a column header;
- **Ignore selected** (checkboxes, with a comment);
- **CSV / XLSX** — export of the current view with the filters, all pages;
- clicking a row — the file page.

**Errors and attention:** groups "status + message" with the number of files, the class and a recommendation. Group actions: ☰ files, ⊘ ignore. The switch above the table: **All**, **Errors** (`failed:*`), **Needs attention**.

**File page:**

| Section | What it shows |
|---|---|
| Buttons | **Open in SharePoint**; **Ignore** (with a comment). |
| Metadata | Size, who changed it in SharePoint and when, library, eTag, attempts, app, error class, message, comment. |
| Attempt history | Every attempt: time, number, stage, result, HTTP code, `request-id` (for contacting Microsoft support), app. |

## 3.8. Statuses

**File statuses — final:**

| Status | Meaning | What to do |
|---|---|---|
| `listed` | The file has been listed: path, size, metadata saved. | — |
| `ignored` | Marked manually with a comment. | — |
| `skipped:deleted` | Deleted or moved after the listing. | — |
| `moved` | Moving: the file is in the target library at the new path. | — |
| `verified` | Verification confirmed: the file is in the target at the new path and is not in the source. | — |
| `skipped:notfound` | Moving: the file is no longer in the source and not in the target either (deleted before the move). | — |
| `failed:<class>` | An error, see [3.9](#39-error-classes-and-what-to-do). | — |

**Intermediate statuses:** `waiting_path` (the listing has not yet reached the file's folder; the path is resolved at the end of the listing), `pending` (waiting for a move or a retry), `deferred` (the file is locked or checked out; retried in a wave, [3.17](#317-moving-apply)).

**Run states:**

| State | Meaning |
|---|---|
| created | Not started yet. |
| running / paused | — |
| stopped | Stopped manually; can be resumed. |
| interrupted | Stopped because of a listing, access or blocking failure; the reason is on the dashboard. Can be resumed once the cause is removed. |
| completed | All files have a final status, there are no errors. |
| completed with errors | All files have a final status, there are `failed:*`. |

## 3.9. Error classes and what to do

| Class | Examples | Automatic retry | What to do |
|---|---|---|---|
| temporary (`transient`) | Network drop, 5xx, 429 after the retries ran out, timeout, a corrupted response | Yes: a request — per `retry.requestAttempts`, a page of the listing — up to 5 more times | If the run was interrupted — **Resume run** later |
| conflict (`conflict`) | The target already has a file with this name | No: the file is not overwritten | Resolve manually: rename one of the files or change the mapping, then a new plan and `apply` |
| locked (`locked`) | The file is checked out or open for editing | Yes: in waves every `retry.lockedWaveMinutes`, up to `retry.lockedLimitMinutes` | Ask the owner to release the file; after the limit — `failed:locked`, a new plan and `apply` |
| unknown result (`job`) | No answer to a move request, a connection drop, 5xx | Yes, after reconciling the source and the target | — |
| verification (`verify`) | After the move the file is not in the target, or it is still in the source | No | Review the `verify` report; `retry --class verify` moves again what is left in the source |
| access (`auth`) | 401, 403 | No | Check the app's rights on the site and the certificate; resume |
| file (`file`) | Library or folder not found, 404, 413 | No | Check the library and folder names in the profile |
| internal (`internal`) | An error of the app itself; a file for which delta did not return a folder | No | Send the log to the developer; repeat the inventory |

## 3.10. Reports

**Run page → Reports** (CSV or XLSX; the summary — TXT) or `spo-mover export <run> -o <folder>`.

| Report | What it contains | What for |
|---|---|---|
| Files (`files`) | `id`, `library` (the number of the library in the profile, from 1), `path`, `format`, `size`, `modified`, `modified_by`, `status`, `error_class`, `error`, `attempts`, `comment` | The complete list: analysing the structure, preparing the mapping. For millions of rows — CSV (XLSX opens slowly, and Excel shows no more than 1,048,576 rows). |
| Folders (`folders`) | `library`, `path` (`/` — the root), `depth`, `files` and `bytes` (including all subfolders), `direct_files` (only in the folder itself), `max_path` (the longest file path in the subtree) | The structure of the libraries for the mapping: which folders are large, where paths are deep. |
| Check-out (`checkouts`) | `library`, `path`, `checked_out_by`, `published` ("no" — a file without a published version), `size` | Whom to ask to release files before moving (LCK-1). |
| Needs attention (`attention`) | `id`, `path`, `status`, `error_class`, `message`, `attempts`, `action` | A to-do list: what to do with each file. |
| Summary (`summary`) | Duration, files, size, speed, files and size by library, the largest top-level folders of the source, checked-out files, counts by status, notes | A short report for approval. |

Mapping reports — in [3.15](#315-mapping-coverage-check).

## 3.11. Command line (CLI)

`spo-mover <command>`. Commands run through the app that is open **under the same account**. The CLI takes the API address and key from `api.json` in the app's data folder (`--data`, the `SPO_MOVER_DATA` variable, by default `%LOCALAPPDATA%\SPOMover`; if the app was started with `--data`, pass the same folder). `--host` (the `SPO_MOVER_HOST` variable) sets the address explicitly; without `api.json` — `http://localhost:5090`.

| Command | What it does |
|---|---|
| `profiles\|connections list` | List. |
| `… get <name> [-o file.json]` | Show or export. |
| `… import <file.json> [--name]` | Create or replace. |
| `… delete <name>` | Delete. |
| `profiles validate <name>` | Validate a profile as before a start. |
| `connections test <name> [--profile <profile>]` | Token and access to the profile's libraries. |
| `inventory <profile> [--wait] [--update]` | A new inventory; `--wait` — wait with progress; `--update` — only the changes since the latest completed one ([S3](#s3-inventory)). |
| `runs [--take N]`, `status <run>`, `wait <run>` | List, state, waiting. |
| `start\|pause\|resume\|stop <run>` | Control. |
| `files <run> [--status] [--class] [--search] [--take]` | Files of the run. |
| `ignore <file> --comment "…"` | Ignore a file. |
| `delete-run <run> [--yes]` | Delete a run that is not running, with all its results. Asks for confirmation. |
| `export <run> -o <folder>` | All reports of the run ([3.10](#310-reports)). |
| `map test <profile> <path…>` | Where each path goes and by which row. |
| `map check <profile> [-o <folder>]` | Coverage check against the latest inventory; with `-o` — also the four mapping reports. Exit code `1` if there are errors. |
| `map import <profile> <file> [--append]` | Load the mapping from CSV/TSV/XLSX (replace; with `--append` — add). Code `2` if the mapping has errors (it is saved anyway). |
| `map export <profile> -o <file.csv>` | Export the mapping. |
| `plan build <profile> [-o <folder>]` | Build a plan from the latest inventory ([3.16](#316-move-plan)); with `-o` — the six plan reports. Moves nothing. Refuses if the mapping has errors. |
| `plan show <plan> [-o <folder>]` | Summary and reports of a saved plan. |
| `apply <profile> [--plan N] [--wait]` | **Moving.** Execute the profile's plan (without `--plan` — the newest one). Changes SharePoint ([3.17](#317-moving-apply)). |
| `verify <run> [--update] [-o <folder>]` | Verify an apply run against the inventory after it; `--update` — first update the inventory with changes only. Code `1` if there are mismatches. |
| `retry <run> [--class <class>…] [--file <number>…] [--wait]` | Retry moving the failed or deferred files of an apply run (with no conditions — all of them). |
| `export-rules <run> -o <file.csv>` | Link replacement rules for SPOReplaceLink from an apply run. |
| `check-targets <profile> [-o <file.csv>]` | Check the target libraries before moving ([3.18](#318-checking-the-target-libraries)). Code `1` if there are warnings. |
| `cleanup-folders <run> [--yes] [--wait]` | After verification: delete to the site recycle bin the source folders that the move has emptied ([3.19](#319-deleting-empty-source-folders-exe-7)). |
| `pilot-report <run> [-o <file.txt>]` | Pilot report of an apply run ([3.20](#320-pilot-report)). |

Ctrl+C during `--wait` stops the run gracefully. Exit codes: `0` — fine, `1` — something needs attention, `2` — a settings error or the app is unavailable, `3` — the run was stopped or interrupted.

## 3.12. App files and folders

In the data folder (`--data`):

| Path | What it is |
|---|---|
| `state.db` | The database: settings, runs, the list of files and folders, folder totals, check-outs, the result of the latest mapping check, attempts, metrics. Back it up while the app is closed. For 1.5 million files — up to several GB. |
| `logs\host-*.jsonl` | The log (JSON lines): every error with details. Sent to the developer in case of problems. |
| `api.json` | The address and key of the local API of the running instance — for the CLI. Created on start, deleted on exit. |

## 3.13. Stage 2 limitations

- **No moving.** The plan is already built ([3.16](#316-move-plan)), moving is stage 4 ([3.2](#32-what-comes-in-later-stages)).
- **Files without a published version** are checked only with the `fullcontrol` role on the site and the SharePoint `Sites.Selected` permission ([1.5, step 2](#step-2-app-in-entra)); with the `write` role SharePoint answers 403 and the step is skipped with a note. Checked-out files are determined from the listing and do not need these rights.
- **The coverage check** compares names case-insensitively and does not check the conflict "a file with the same name as a folder".
- **Access to the window** — only on the local machine; there are no roles and no Entra sign-in for the GUI.

## 3.14. If something goes wrong

| Situation | Cause and what to do |
|---|---|
| "Certificate … not found in the store" | The app is running under a different account than the one the certificate was created under, or the wrong store is selected. |
| "no private key" | The selected store has only an imported `.cer` without the key. Select the store where the certificate was created with `New-SelfSignedCertificate`, or create a new certificate. |
| "No access to the private key" | The certificate is in `LocalMachine`, and the app's account has not been given Read on the key (Manage Private Keys). |
| `AADSTS700027`, `AADSTS700016` | The certificate has not been uploaded to the Entra app, or the Client ID or tenant is wrong. |
| There is a token, but `403` on the library | No admin consent to Sites.Selected, or access to the site has not been granted. |
| Site search finds nothing | Normal for Sites.Selected — enter the site address. |
| "library … not found on the site" | Wrong library name; the message lists the existing ones. |
| The note "Files without a published version were not checked…" | The app has been granted the `write` role, and for this list SharePoint requires `fullcontrol`; or there is no SharePoint `Sites.Selected` permission ([1.5](#15-setting-up-access-to-sharepoint-once)). The listing and the checked-out files are complete; whether to grant `fullcontrol` is the administrator's decision. |
| "This profile has no completed inventory of the source yet" | The coverage check needs a completed inventory of the same source. Run inventory; after changing the source — too. |
| The run is "interrupted: File listing…" | A page of the listing could not be received several times in a row. Read the reason, remove it, **Resume run** — from the same page. |
| "possible SPO blocking" | SharePoint has been answering only 503 for a long time. Don't resume right away; wait, lower the RU budget, work off-hours. |
| Many 429 | Lower the RU budget, check the license range in the connection, find out whether other heavy apps are working in parallel (migration, backup). |
| CLI: "Host unavailable" | The app is not running under this account, or it was started with a different `--data` (pass the same one to the CLI), or an explicit `--host` points elsewhere. |
| CLI: "No access key for this instance" | The app at this address belongs to another user or another data folder. Start the app under your own account and remove `--host`, or pass the same `--data`. |
| The window doesn't open, starting again does nothing | The app is already running (perhaps in the tray). Double-click the icon next to the clock. |
| Anything unclear | `spo-mover export <run> -o …` and `logs\host-*.jsonl` from the data folder — to the developer. |

## 3.15. Mapping coverage check

**Profile → Mapping → Check coverage…** or `spo-mover map check <profile>`. Takes the profile's saved mapping and the latest completed inventory of the same source. Changes nothing in SharePoint; the result for every file is saved in the database for the reports.

**Cards:**

| Card | What it shows | What to do |
|---|---|---|
| Moved | Files and size that the mapping sends to the target libraries. | — |
| Kept by the "-" rule | Files under "keep in source" rows. | — |
| No rule (stays) | Files not covered by any row. | If that is not intended — add rows; the list of folders below and the "No rule" report. |
| Name conflicts | Files that would land on the same path in the target library: two source files (`conflict`) or a source file and a file already there (`exists`). | Change the path in the mapping or rename the files. Moving does not overwrite a conflict, it stops on it. |
| Paths too long | Files whose path after the move (from the site root) would exceed 400 characters. | A shorter path in the mapping. |
| Checked out | Checked-out files in the folders being moved. | Ask the owners to release them before the wave. |

**Tables:** by target library (files and size, "not listed" — the library was added after the inventory); no rule — the largest folders (two levels); mapping rows — how many files and how much data each covers, with a warning for rows that match no file.

**Reports** (CSV or XLSX):

| Report | What it contains |
|---|---|
| Mapping rows (`coverage`) | `row`, `source`, `library`, `path`, `files`, `bytes` |
| No rule (`unmapped`) | `folder`, `files`, `bytes` |
| Problems (`problems`) | `kind` (`conflict`, `exists`, `long_path`, `checked_out`, `unpublished`), `source_path`, `library`, `new_path`, `detail` |
| Where each file goes (`preview`) | `source_path`, `size`, `rule`, `library` (`-` — kept by a rule, empty — no rule), `new_path` |

For 1.5 million files the check takes up to a minute; the `preview` report is the largest (CSV).

## 3.16. Move plan

**In the window:** **Profiles → [profile] → Plan…** or **Plan and move → Plan…**. Buttons: **Build plan**, choosing a **snapshot** (every build is a separate plan), **Reports** (CSV/XLSX), **Move…**. Cards: batches, what is moved and what stays, folders to create, conflicts, check-outs in the batches, forecast, the largest batch; comparison with the previous plan. Tabs:

- **Waves** — the mapping rows with batches, files, size and forecast; a click — the batches of that wave.
- **Batches** — a filter by wave, kind and path text; for a set of files — the list of files with their new paths.
- **Source tree** — the library's folders (expanded one at a time) with a mark of what happens to each: "batch N: whole folder" and where to, "in batch N", "split" (the folder is created in the target), "stays", "partly moved", "no rule"; the mapping row number and the batches of the folder's files.
- **Folders to create**, **Conflicts**, **Changes** (compared to the previous plan).

If the inventory is newer than the plan, the page warns about it.

**Command line:** `spo-mover plan build <profile> [-o <folder>]`. Takes the profile's mapping and the latest completed inventory of the same source, together with the target libraries. Splits everything that is moved into **batches** and saves the plan as a snapshot: every build is a new numbered plan, the previous ones are kept. Changes nothing in SharePoint. While the mapping has errors, the plan is not built.

**How it is split** (PLN-2):

- **A whole folder** — if its subtree (files and folders together) has no more than `plan.maxBatchItems` items (1,000 by default), there are no mapping rows inside with a different target, and the target folder does not exist yet. SharePoint moves such a batch with one request. The limit must be less than 5,000: SharePoint does not move a folder with a larger subtree.
- **Otherwise** the target folder is created in advance, every subfolder is planned separately by the same rules, and the files of the folder itself become batches of **files** (up to `maxBatchItems` per batch).
- A folder is **not** moved whole if its target folder already exists in the target library or is needed by another batch. This happens when several sources go into one folder, or one row leads inside the target of another. Then the contents are merged through the created folder.
- **Conflict** — a file whose new path is already taken (by a file in the target or by another source file). It is not planned; it stays in the source until a decision is made.
- Empty folders are moved as batches without files.

**Waves** (PLN-3): one wave per mapping row, in the order of the rows. **Forecast** — the net moving time: files ÷ (`plan.filesPerSecond` × `plan.parallel`). By default 2.2 files/s — the speed of moving real documents on a test site ([API_SPIKE.md](../docs/API_SPIKE.md), item 37) — and 4 batches at once. Both numbers are refined in the pilot. Time for check-outs, retries and throttling is not included in the forecast.

**Parameters in the profile** (JSON, the `plan` section):

| Parameter | Default | What it sets |
|---|---|---|
| `maxBatchItems` | 1000 | The largest batch in items (from 1 to 4,999). |
| `parallel` | 4 | How many batches at once — for the forecast. |
| `filesPerSecond` | 2.2 | The speed of one batch — for the forecast (measured on `fs-test` with real documents). |

**Comparison with the previous plan** (PLN-4). If the profile already had a plan, the summary shows how many batches were added, removed and changed and how the number of files changed, and the `changes` report lists those batches. Batches are matched by what they move and where. The usual order before a wave: inventory with changes only → `plan build` → review the changes → approval.

**Reports** (CSV or XLSX; with `-o` — all six):

| Report | What it contains |
|---|---|
| Batches (`batches`) | `seq`, `wave`, `kind` (`folder` — a whole folder, `files` — files of one folder), `rule`, `source`, `library`, `target`, `files`, `bytes`, `items`, `checked_out` |
| Waves (`waves`) | `wave`, `rule`, `title`, `batches`, `files`, `bytes`, `forecast_hours` |
| Folders to create (`folders`) | `library`, `path` — in the order of creation, parents first |
| Conflicts (`conflicts`) | `source_path`, `library`, `new_path`, `reason` |
| Files of file batches (`files`) | `seq`, `source_path`, `new_path`, `size` (files of folder batches are not listed: that is the whole subtree) |
| Changes (`changes`) | Compared to the profile's previous plan: `change` (`added`, `removed`, `changed`), `kind`, `source`, `library`, `target`, `files_before`, `files_after`, `bytes_before`, `bytes_after` |

For a library with 65 thousand files the plan is built in less than a second. The plan does not see changes in SharePoint after the inventory: before a wave, update the inventory (changes only) and build the plan again.

## 3.17. Moving (apply)

**In the window:** the plan page → **Move…**. The confirmation window shows exactly what will change (files, size, batches, folders to create, forecast), checks the target libraries ([3.18](#318-checking-the-target-libraries)), warns about check-outs, conflicts, the age of the inventory and changes to Office documents. The **Move** button is available only after ticking "I have reviewed the plan…". After the start the run page opens:

- **Overview:** "Moved", "Batches done" (with a breakdown: running, partial, deferred, failed), "Verified", move speed, load on SharePoint; the **Waves** table with the progress of each; a breakdown by target library.
- **Batches:** every batch with its state, number of attempts, duration, reason and the time of the next retry; a filter by state.
- **Files**, **Errors and attention** — as for the inventory, with the statuses `moved`, `verified`, `deferred`, `failed:*`.
- When finished: **Verify…** (an inventory with changes only and verification in one click; or against an existing inventory made after the move), **Retry failed…** (choosing the error classes; conflicts are not selected by default), **Delete empty folders…** (after verification, [3.19](#319-deleting-empty-source-folders-exe-7)), **Reports → Rules for SPOReplaceLink — CSV**, **Verification — CSV** and **Pilot report — TXT** ([3.20](#320-pilot-report)).

Pause and stop do not cut off the batches that are running: they run to the end.

**Command line:** `spo-mover apply <profile> [--plan N] [--wait]` — **changes SharePoint**. Moves the batches of the plan ([3.16](#316-move-plan)): without `--plan` — of the profile's newest plan. The plan decides what goes where; the profile gives the connection, concurrency and retries. A profile without errors is required.

**Order before a wave:** inventory with changes only → `plan build` → review the reports and changes → warn the owners (see "Office documents" below) → `apply --wait`.

**What happens:**

1. The folders from the plan's `folders` report are created (parents first; an existing folder is not an error).
2. Batches go `plan.parallel` at a time, within a wave — from the largest to the smallest.
   - **A folder batch** — one SharePoint `MoveFolderByPath` request: the folder with its whole subtree. Then it is checked that the folder is no longer in the source. If something is left (a locked file, more than 5,000 items, the folder already exists in the target), the rest is moved item by item to the same target.
   - **A file batch** — every file with a separate `MoveFileByPath` request, with a new name if the mapping renames it.
3. Creation and modification dates and authors are preserved. A file with the same name in the target **is not overwritten** (`failed:conflict`).
4. Locked files (checked out, open for editing) are deferred (`deferred`). The batch tries them again every `retry.lockedWaveMinutes` minutes until `retry.lockedLimitMinutes` has passed; after that they get `failed:locked`. SharePoint refuses files open for editing in Word/Excel for the web (423); they are moved only if the profile has `move.bypassSharedLocks: true` (tested on a tenant: the user notices nothing, their changes are kept). A folder with such a file is not moved whole — the batch goes item by item.
5. If no answer came to a move request (timeout, connection drop), it **is not retried blindly**: first it is checked where the source and the target are now. The same after the app crashes or the computer is turned off: unfinished batches continue with that check.

**Batch state:** `done` — everything is in the target; `partial` — some files were not moved (see the run's file table, the "Needs attention" filter); `deferred` — waiting for locked files; `failed:<class>`. The run completes when all batches have a final state. Pause and stop do not cut off the batches that are running: they run to the end, and new ones do not start.

**The run's file table** contains every file of the plan with its status (`moved`, `deferred`, `failed:conflict`, …), so filters, search and reports work as for the inventory.

**What remains after the move:**

- Empty folders in the source (folders whose contents were moved). Deleting them is a separate run after verification ([3.19](#319-deleting-empty-source-folders-exe-7)).
- **Office documents change.** When moving between libraries, SharePoint writes the document properties into the file. In `.xls`, `.xlsx`, `.doc`, `.pptx` the size grows by a few KB; the content, dates and authors are the same, and no new version appears in the history. On `fs-test` this affected a third of the files. If byte-for-byte integrity matters (signatures, hashes in records), warn the owners before the wave; a site administrator can turn this off.

**Profile parameters** (JSON, the `move` section):

| Parameter | Default | What it sets |
|---|---|---|
| `bypassSharedLocks` | false | Move files open for co-authoring. |
| `secondsPerItem` | 0.5 | How many seconds per batch item to wait for the answer to a folder move (on top of `performance.requestTimeoutSeconds`). Measured ≈ 0.15–0.45. |

**After the move:**

- **Verification** — `spo-mover verify <run> --update`. First the inventory is updated with changes only, then every moved file is checked: it must be in the target at the new path and not be in the source. The result is `verified` or `failed:verify` with a reason. New files that appeared in the source under moved folders after the plan are shown separately: they need a new plan. Verification goes by paths, not by size: the size of Office documents changes (see above). On `fs-test` 3,233 files were verified in 29 s including the update.
- **Retry** — `spo-mover retry <run> --class conflict` (or `--class locked`, `--file 123`, with no conditions — all failed and deferred). First remove the cause (rename the file in the target, ask to release the locked one). The batches of these files go back to the queue, the run resumes and tries only the selected files.
- **Rules for SPOReplaceLink** — `spo-mover export-rules <run> -o rules.csv`, then "Rule templates → Import rules" in SPOReplaceLink. A folder moved whole gives one rule "old address → new". Files moved one by one — a rule for each. What was not moved (conflict, lock) gets no rules, so links to it stay unchanged.

**Speed:** on `fs-test` one folder batch moves ≈ 2.2 files/s (real documents of 270–390 KB); 4 batches at once — 6–9 files/s; 3,233 files — 6–8.5 min. There was no throttling. For your own library — the pilot report ([3.20](#320-pilot-report)).

## 3.18. Checking the target libraries

Target libraries are created manually, and SharePoint silently drops what the target cannot store. The check compares the source library with every target **before the move** and changes nothing.

**Where:** the plan page → **Check libraries…**; the **Move…** window does the same automatically (the "Target libraries" section); the move run itself writes the result into the notes of the run page at the start. Command line: `spo-mover check-targets <profile> [-o <file.csv>]` (code `1` if there are warnings).

| What is checked | Why it matters | Level |
|---|---|---|
| A source column that the target doesn't have | Its value in the moved files will be lost. Add the same site column to the target. | warning |
| The same column, a different type | The value may not carry over, or carry over differently. | warning |
| A required target column that the source doesn't have | Files will be without a value ("required info"). | warning |
| A source content type that the target doesn't have | Files will get the default type; its columns will lose their values. | warning |
| "Require check out" in the target | Moved files may stay checked out and be invisible to others. Turn it off for the time of the move. | warning |
| Content approval in the target (not in the source) | Files will wait for approval; readers won't see them. | warning |
| Minor versions (drafts) in the target (not in the source) | Files may become drafts invisible to readers. | warning |
| Versioning is off in the target | New changes of the moved files won't have versions. | info |

The columns users fill in are compared (not hidden, not read-only, added to the library). Microsoft 365 service columns (image tags, retention labels and so on) are ignored: they differ between libraries without anyone's involvement. Warnings do not block the move — whoever starts it decides. The SharePoint REST permission (Sites.Selected) on the site is needed, as for the move.

## 3.19. Deleting empty source folders (EXE-7)

After the move, the source keeps the folders the files left (folders split into batches, and those moved item by item). Deleting them is a separate run in the `cleanup` mode, only after verification.

**Where:** the move run page after **Verify…** → **Delete empty folders…**. Command line: `spo-mover cleanup-folders <apply run> [--yes] [--wait]`.

**Which folders:** only those above the moved files, within the mapping row that moved them (never above the rule's folder and never the library root). Folders outside the mapping are not touched.

**How:** from the deepest to the top. Every folder is checked again at that moment:

| Status | What it is |
|---|---|
| `deleted` | The folder was empty and went to the **site recycle bin** (it can be restored from there, 93 days). |
| `skipped:notempty` | Something is left in the folder — a file kept by a "keep in source" rule, a conflict, a locked or a new file. The folder stays; the comment says how many files and folders it holds. |
| `skipped:notfound` | The folder is already gone: moved together with a batch folder (without a request to SharePoint) or deleted by someone. |
| `failed:<class>` | Failed (access, a temporary error after three attempts). The run can be resumed. |

Without verification, or with unconfirmed files, the run is not created: an empty folder is only one that files really left. Pause, stop and resume — as for any run. The page shows "Deleted (to recycle bin)", "Left, not empty", "Already gone"; the **Files** tab — every folder with its status.

## 3.20. Pilot report

For a move run: **Reports → Pilot report — TXT** or `spo-mover pilot-report <run> [-o <file.txt>]`. Sections:

1. **What was moved** — plan files, moved, verified; batches by state; by target library.
2. **Speed** — the median files/s per batch (batches of 20 files or more), the overall speed over the moving time (pauses and stops are not counted) and the **recommended `plan.filesPerSecond`** for the profile.
3. **Load on SharePoint** — RU, requests, RU per 1,000 files, 429/503 and whether `plan.parallel` can be raised.
4. **Locks, conflicts, errors** — attempts with locked files, deferred, `failed:locked`, name conflicts, other errors.
5. **Verification** — confirmed / not / not verified yet.
6. **Forecast for the rest** — how many files remain in the source according to the latest inventory, and how long that will take at the current and at double concurrency.
7. **Check manually** — a short list after a wave.

After the pilot, put the measured speed into the profile (the "Planning" tab): the plan forecast for the next waves will become accurate.
