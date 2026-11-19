QA Data Transfer & Backup System (Core)

Internal code repository, backup and deployment system for Quality Aviation. A hosted API stores versioned snapshots of project folders; a deploy agent on the same server builds, tests and runs applications from them; a Windows client pushes, pulls, clones and switches versions and acts as the deployment control panel, either from a terminal menu or from a Windows application.

🏗️ System Architecture

1. Server-Side (API) — API/QualityAviationCodeRepositoryManagmentApi

ASP.NET Core (.NET 9). Stores every repository as plain folders on disk:

    <BasePath>\<owner>\<repo>\.qarepinfo          repository descriptor
    <BasePath>\<owner>\<repo>\v20261013_200559\   one folder per version, files + .metadata.json
    <DataPath>\users.json                         accounts (salted PBKDF2 hashes, no passwords)
    <DataPath>\keys\                              keys that sign login tokens (machine-encrypted)
    <DataPath>\server.pfx                         HTTPS certificate (created on first run)

2. Deploy agent — Deploy/QualityAviationDeployAgent

A separate Windows service on the same server (the API itself is not changed). It reads the API's repository store, uses the same logins and the same HTTPS certificate, and runs the CI/CD pipeline, the deployed applications, their traffic counting, logs and incidents. Details under "🚀 Deployments" below.

3. Shared — Shared/QualityAviationRepositoryShared

The file hashing (manifests) and the deployment data contract, compiled into both the agent and the client so the two always agree.

4. Client — Console/QualityAviationCodeRepositoryConsoleClient

One exe. On every start it asks how you want to work:

    1  Terminal               menu-driven, also usable over SSH / remote shell
    2  Windows application    the same tasks in a window

Start it with --terminal or --gui to skip the question (shortcuts, scripts). Both modes run the same code (Services/RepoOperations.cs), so they always behave the same.

🚀 Tasks (both modes)

View repositories, Create, Push, Pull latest, Clone, View history, Switch/Downgrade version, Open in VS Code / File Explorer / Visual Studio, Change password, Logout, Deployments (the control panel; menu D in the terminal, the Deployments tab in the Windows application), and Helpdesk (menu H, the Helpdesk tab; see below).

Only changed files travel when the deploy agent runs on the server: both sides describe every file by its SHA-256 hash, so a push sends only the files that differ from the server's latest version (plus the list of removed ones), and a pull, clone or version switch fetches only the files that differ from what is already in the folder. The server rebuilds the full version from the previous one and refuses it unless it matches the PC's files exactly, and the repository API stores it as usual. Without the agent, or if someone pushed in between, the whole folder is sent as before.

Push skips bin, obj, .vs, .git and anything listed in .qaignore. A pushed or cloned folder gets a .qarepo file that remembers which repository it belongs to, so later pushes and pulls go back to the same place.

Opening a repository: select it in the list and the bar above its versions shows where its copy is on this PC, with three buttons: VS Code (opens the folder), File Explorer (opens the folder) and Visual Studio (loads the solution; without one the project file; with several it asks which). The same is under the right mouse button, and under O in the terminal. The client remembers the last folder each repository was pushed from, pulled into or cloned into on this PC (per Windows user, in %LocalAppData%\QARepository\folders.json). For a repository it has not seen on this PC yet it offers to clone it, or to point to a copy made earlier; "Change folder..." switches to another copy. VS Code and Visual Studio are found where their installers put them; a button says "(not installed)" when they are missing.

🎫 Helpdesk (Q+ Helpdesk) — Helpdesk/

A web portal, deployed through the deploy agent like any other application, where every Q+ CMR user raises tickets
(complaints, automation requests, new requirements) with their priority, screenshots and documents. Name, e-mail and
department come from Q+ CMR user management; the portal only reads that database. Management gets a read-only
productivity dashboard.

IT works the tickets from the client: the Helpdesk tab (and menu H in the terminal) with

    Tickets            the queue (open, overdue, by status, by person, search) and each ticket's steps: accept, reject,
                       take/assign, start, hold, resolve, close, reopen, notes (internal or for the requester), time,
                       files, edit details and form fields, full history
    To-do              the team's shared list: who is doing what, blocked, done; archived, never deleted
    Form & settings    the web form's fields, ticket types and resolution targets, changed at run time (IT leads)
    Audit              the tamper-evident trail and its check (IT leads)

No second login: the client sends the same gate pass it uses here, and the helpdesk asks the deploy agent who it
belongs to. The client finds the helpdesk through the agent (the application named Helpdesk:AppName, default "helpdesk",
published with HTTPS). Changes made by anyone show up in every client within a second.
Setting it up: Helpdesk\README.md. Design and decisions: Helpdesk\docs\ARCHITECTURE.md.

🤖 Claude skill (automating the terminal client)

Skills\qa-repository\SKILL.md teaches Claude Code how to drive the terminal client: list, versions, pull, clone, switch, push. Its fixed rule: a push (commit) happens only at the end of a development sprint, and only after the user says yes to that push. Claude never signs in or handles a password; the user signs in once in the client and Claude uses that saved login.
The skill ships inside the client (claude-skill\qa-repository). To install it for Claude on a PC (PowerShell, in the client folder):

    Copy-Item -Recurse -Force .\claude-skill\qa-repository "$env:USERPROFILE\.claude\skills\"

Run the same command again after a client update that changes the skill. Tell Claude where the client is once, e.g. a line in CLAUDE.md: QA client: D:\Tools\QAClient\QualityAviationCodeRepositoryConsoleClient.exe

🔄 Automatic client updates

The client updates itself from a repository that holds the published client. After every sign-in it looks at the newest version of that repository; if this PC runs an older one, it fetches only the files that differ (compared by hash through the deploy agent; without the agent the whole version), swaps them in, says so, and restarts signed in. A failed or impossible update never stops the client: it says why and keeps running the current version.

Setting it up (once):

1. publish.cmd client
2. In the client, as qaim: Push, choose the dist\client folder, name the new repository qa-client. Done: dist\client now carries a .qarepo, so from now on a Push of that folder always goes to qaim/qa-client.
3. Copy dist\client to each PC one last time by hand. From then on the PCs update themselves.

Releasing a new client: publish.cmd client, then Push dist\client (message e.g. "client: open in VS Code"). Every PC takes it at its next sign-in. Going back to an older client: push that build again, it becomes the newest version.

    Update:Repository    owner/name of the client repository (default qaim/qa-client); empty = no automatic updates
    Update:Publishers    the logins whose versions are installed; empty = the repository's owner only

Anyone may push to any repository, so a version pushed by someone not in Publishers is never installed. appsettings.json travels with the client, so a setting changed in dist\client and pushed reaches every PC.
The client must be in a folder its user can write to (e.g. D:\Tools\QAClient, not C:\Program Files). The installed version is shown in the window title and recorded in the folder's .qarepo; the files that were replaced wait in .qaupdate\old-* until the next start removes them.
A copy that runs from a build folder (bin\Debug, bin\Release) never updates itself, and "--no-update" skips the check for one start, e.g. to try a new build in dist\client before pushing it.

🖥️ Hosting the API

1. Build the hand-over folders (once, on a dev PC):

    publish.cmd

   This creates dist\api, dist\agent and dist\client. All are self-contained: no .NET install needed to run them.
   "publish.cmd client agent" builds only those two (the live API is then left alone).

2. Copy dist\api to the server and edit appsettings.json there:

    Urls                              address and port to listen on (default https://0.0.0.0:5001)
    DiscoveryPort                     UDP port on which clients find the server (default 5002, 0 = off)
    RepositorySettings:BasePath       where repositories are stored
    RepositorySettings:DataPath       where users, keys and the certificate are stored
    RepositorySettings:MaxUploadMB    largest push accepted (default 500)

3. Create the accounts (run on the server, in the api folder):

    QualityAviationCodeRepositoryManagmentApi.exe user set <username> [email]     create a user / reset a password
    QualityAviationCodeRepositoryManagmentApi.exe user remove <username>
    QualityAviationCodeRepositoryManagmentApi.exe user list
    QualityAviationCodeRepositoryManagmentApi.exe user import users.csv           lines of Username,Password,Email

   Changes apply immediately, the API does not need a restart. Resetting or removing a user also ends their open sessions.
   Moving from the old users.xlsx: save it as CSV, run "user import", then delete both files.

4. Get the certificate thumbprint the clients must trust:

    QualityAviationCodeRepositoryManagmentApi.exe cert

5. Run it. Either start the exe directly, or install it as a Windows service so it starts with the machine (admin prompt):

    sc.exe create "QA Repository API" binPath= "C:\QARepo\api\QualityAviationCodeRepositoryManagmentApi.exe" start= auto
    sc.exe start "QA Repository API"

6. Open the two ports in the server firewall (admin prompt). If Windows asks "allow access?" the first time the API starts, choosing Allow does the same thing.

    netsh advfirewall firewall add rule name="QA Repository API" dir=in action=allow protocol=TCP localport=5001
    netsh advfirewall firewall add rule name="QA Repository discovery" dir=in action=allow protocol=UDP localport=5002

To use a company-issued certificate instead of the generated one, set TlsSettings:CertificatePath and TlsSettings:CertificatePassword.
Keep the DataPath folder readable only by administrators and the account the API runs as. Back up BasePath and DataPath together.
When updating the API later, replace everything in the folder except appsettings.json.

💻 Setting up the client

Copy dist\client to the PC. One setting in appsettings.json matters:

    ApiSettings:CertificateThumbprint    the value printed by "cert" on the server. This is how the client recognises the server.

Then run QualityAviationCodeRepositoryConsoleClient.exe and log in. The client finds the server by itself; no address is needed.

    ApiSettings:ServerPort               the server's HTTPS port (default 5001)
    ApiSettings:SearchNetworks           normally empty; a network to search in addition to the PC's own, e.g. "10.0.1.0/24"
    ApiSettings:DiscoveryPort            UDP port of the server's search service (default 5002, 0 = do not use)
    ApiSettings:BaseUrls                 normally empty, see below

🔎 How the client finds the server

The server is searched for, not looked up from a list of addresses. Every address is asked in two ways at once, and the client connects to the first machine that proves it is the server:

    by certificate    an HTTPS handshake on ServerPort. Needs nothing special on the server, so it works with every version of the API.
    by question       a small UDP packet on DiscoveryPort that a server running the search service answers. Faster when available.

1. Quick round: this PC and the address where the server was last time (remembered per Windows user in %LocalAppData%\QARepository\server.txt), plus one broadcast per connected network. In daily use this is answered in milliseconds and nothing more is sent.
2. Full round, when the quick round found nothing: every address of every network, one by one. The status line shows the ranges, e.g. "10.0.248.1 to 10.0.251.254 (1022 addresses)".

What the full round covers:

    networks named in SearchNetworks            first; private ranges only, up to /20 (4094 addresses) each
    the network of every connected adapter      whole pool up to 1022 addresses (/22); for larger networks the 1022 nearest, plus the broadcast
    a VPN link (no pool of its own)             the 254 addresses around the address the VPN gave this PC
    around each adapter's gateway and DNS       254 addresses each, because a VPN may hand out addresses from another pool than the servers use
    public internet addresses                   never searched
    one search                                  at most 4096 addresses, about 5 seconds when nothing is found

The one-by-one search passes through a VPN (a broadcast does not), so a PC outside the office finds the server the same way as one inside.
A server in a network the PC is not part of (another VLAN or site) is only found if that network is named in SearchNetworks.
To a network monitor the full round looks like a scan of one port. Tell IT it is expected; it only happens when the server is not where it was last time.

The search cannot be hijacked. By certificate, the machine has to present exactly the pinned certificate and prove it holds its private key, which is what an HTTPS handshake does. By question, the client puts a fresh random number in the packet and only believes an answer signed with that certificate's key over that number. Machines that fail are sent nothing else. If a QA Repository server with a different certificate is found, the client refuses it and shows its address and thumbprint so the setting can be checked.

If the server gets a new address, clients find it again by themselves on their next start.
ApiSettings:BaseUrls is normally empty. It exists only as a way to name one exact address.

🚀 Deployments (CI/CD)

The deploy agent turns a version of a repository into a running application and keeps it running.

Pipeline (one deployment at a time, shown as nodes in the control panel):

    Source     the version is copied out of the store; every file is checked against the hash recorded when it was pushed
    Restore    NuGet packages (.NET applications)
    Security   credentials in the source (passwords, keys, tokens, private keys) and NuGet packages with known vulnerabilities;
               "report" lists them, "block" also stops the deployment on critical/high findings
    Build      dotnet build; every compiler error and warning is collected with code, file and line
    Test       every test project in the repository (dotnet test); failed tests are listed with their message
    Publish    a sealed release folder: the hash of every file is recorded, so changes made on the server later are detected
    Deploy     the release is started beside the live one on a private port; it gets the traffic only once it answers
    Verify     it is watched under real traffic for 5 seconds; if it does not hold up, the previous release takes over again

If any stage fails, the live application is not touched: no downtime, nothing half-deployed. A rollback skips straight to Deploy with a release that was built before, so it takes seconds. The last 5 releases are kept.

Applications are published on their own port by the agent, which forwards each request to the live release. That is what makes switching releases without a dropped request possible, and lets the agent count users (distinct client addresses), requests per minute, server errors and response times per application. While an application is offline its port shows a "temporarily unavailable" page.

Running applications are watched: a crash is restarted automatically (with growing pauses; after 5 crashes in 5 minutes it stops trying), an application that stops answering its health check is restarted after a minute, and many server errors are reported. Each of these opens an incident, which closes itself when the problem goes away; people can acknowledge, resolve and add notes, and everything lands on the incident's timeline. Every action is in the audit trail (who, what, when, from which address).

ASP.NET Core and other .NET applications need no extra settings: the project is found in the repository (or named in Project), ASPNETCORE_URLS is set for it, and forwarded headers are switched on so the application sees the user's real address. Other kinds of applications use the "custom" kind with build, test, publish and start commands.

Settings that must not be in the repository (connection strings, API keys) are set per application under Environment: stored encrypted on the server, given to the application as environment variables (ConnectionStrings__Default etc.), never shown again. Files placed in <WorkPath>\apps\<app>\config\ are copied into every release (for an appsettings.Production.json, for example). Each application also gets a data folder that outlives releases (QA_DATA_DIR).

Who may do what:

    administrators    named on the server with "QualityAviationDeployAgent.exe admin add <login>"; register applications,
                      edit their settings and environment, optimize storage, read the audit trail
    deployers         listed per application; deploy, roll back, start, stop, restart, read logs, handle incidents
    everyone else     sees the applications, their state, traffic and pipeline results

Installing the agent on the server (the API keeps running; nothing about it changes):

1. Install the .NET SDK on the server (the version your applications target, e.g. 9.0) so it can build them. The server needs access to nuget.org, or to your own package feed, to restore packages.
2. Copy dist\agent to the server, e.g. D:\LocalRepoApi\agent, and check appsettings.json there:

    Urls                          the agent's own address (default https://0.0.0.0:5003)
    AgentSettings:RepositoriesPath    the API's RepositorySettings:BasePath
    AgentSettings:DataPath            the API's RepositorySettings:DataPath (logins, login keys and certificate are shared)
    AgentSettings:WorkPath            where builds, releases, logs and metrics go (default C:\CodeManagement\Deploy)
    AgentSettings:RepositoryApiUrl    the API on this machine (default https://127.0.0.1:5001)

3. Name the administrators:

    QualityAviationDeployAgent.exe admin add qaim

4. Run it as a Windows service. Builds run code from the repositories, so give the service its own Windows account with no admin rights: read access to RepositoriesPath and DataPath, full access to WorkPath.

    sc.exe create "QA Deploy Agent" binPath= "D:\LocalRepoApi\agent\QualityAviationDeployAgent.exe" start= auto obj= .\QADeploy password= <password>
    sc.exe failure "QA Deploy Agent" reset= 86400 actions= restart/5000/restart/5000/restart/5000
    sc.exe start "QA Deploy Agent"

5. Open the agent's port and the port of every application you register (admin prompt):

    netsh advfirewall firewall add rule name="QA Deploy Agent" dir=in action=allow protocol=TCP localport=5003
    netsh advfirewall firewall add rule name="QA app sample" dir=in action=allow protocol=TCP localport=8081

6. In the client, open Deployments, choose "New application" (or N in the terminal), pick the repository, the public port and who may deploy it, then Deploy.

Storage: every version is a full copy, so unchanged files exist once per version. "Optimize storage" (administrators) keeps one copy of each identical file and turns the others into NTFS hard links: nothing changes for the API, pushes or pulls, only the disk space is shared. It first shows what it would save.

🛡️ Security

Identification: the server is recognised by its certificate, never by its address. Finding it and connecting to it both require proof of that certificate (see above).
Transport: HTTPS only. The client refuses http:// addresses (except localhost) and only talks to a server whose certificate matches the pinned thumbprint.
Login: username + password over that channel. The server answers with a signed, encrypted gate pass that expires (AuthSettings:TokenHours, default 7 days) and is sent with every request.
Passwords: stored only as salted PBKDF2 hashes. Login attempts are limited to 10 per minute per address.
Saved session: kept on the PC encrypted with the Windows account (DPAPI) under %LocalAppData%\QARepository.
Storage: owner, repository, version and archive entry names are validated before they touch the disk, so a request cannot read or write outside BasePath. Uploads are size-limited, compressed and unpacked size both.
Deployments: builds run only for registered applications, in a folder of their own, with credentials removed from the environment they inherit. Environment values are encrypted with keys only the agent's Windows account can open. Releases are sealed by hash; the agent reports files changed on the server afterwards. Pushed versions are checked against their recorded hashes before they are built.
Access model: every logged-in user can read all repositories and add versions to existing ones (Direct-to-Source); each version records who pushed it. A repository can only be created under your own name. Versions are never modified or deleted by the API.

🌐 Network failures

At start-up every candidate address gets one quick attempt (5 second limit each), so a server that is down or a missing VPN is reported within seconds, with a Retry choice.
During work every request is retried with growing pauses (ApiSettings:MaxRetries), moving on to the fallback addresses. Each step is shown, so a wait never looks like a hang.
A transfer that stops moving for TransferStallSeconds is aborted and retried; a slow but moving transfer is left alone.
A push becomes a version in a single step on the server, so a broken upload leaves nothing behind, and repeating a push that already arrived does not create a duplicate version.
A pull only touches the local folder after the whole archive has arrived.
An expired or revoked login asks you to log in again instead of failing the task silently.

🔌 API endpoints

    GET  /api/health                                         no login needed
    POST /api/auth/login                                     { username, password }
    GET  /api/auth/validate
    POST /api/auth/change-password                           { currentPassword, newPassword }
    GET  /api/repository/list
    POST /api/repository/create                              { repoName, description }
    POST /api/repository/push?repoName=&owner=&message=      body: the zip archive
    GET  /api/repository/pull?owner=&repoName=&versionName=  zip; X-Version header names the version sent
    GET  /api/repository/clone?owner=&repoName=
    GET  /api/repository/versions?owner=&repoName=
    GET  /api/repository/exists?owner=&repoName=

All except health and login need the header: Authorization: Bearer <gatePass>

⚖️ License

This project is licensed under the MIT License - see the LICENSE file for details.
