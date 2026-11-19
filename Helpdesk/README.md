Q+ Helpdesk

Tickets from every Q+ CMR user, worked on by IT from the Git client, watched by management on a read-only dashboard.
How it is built and why: docs\ARCHITECTURE.md.

    Web portal (ASP.NET 9)    Q+ CMR users raise and follow tickets; management sees the dashboard
    Git client, Helpdesk tab  IT reviews, executes and completes tickets, edits the form, keeps the to-do list
    Terminal client, menu H   the same as the Helpdesk tab

Folder

    QualityAviationHelpdesk\         the portal (Program.cs, Pages\, Services\, Security\, Endpoints\, wwwroot\)
    QualityAviationHelpdesk.Tests\   unit tests (workflow, fields, audit chain, legacy password hashes, attachments)
    db\migrations\                   the database schema, applied by the portal at start-up (or by a DBA)
    db\admin\create-logins.sql       least-privilege SQL logins (run once as a DBA)

🗄️ 1. Databases (once, as a DBA)

Edit the two passwords in db\admin\create-logins.sql and run it on the SQL Server that holds TraacsSupportDB. It creates:

    QAHelpdesk            the helpdesk's own database
    qa_helpdesk_app       owns QAHelpdesk (creates the schema on first start)
    qa_helpdesk_legacy    may only SELECT AspNetUsers, AspNetRoles, AspNetUserRoles and AppTraacsUserTypes in TraacsSupportDB

The portal never writes to TraacsSupportDB.

🚀 2. Deploy it with the deploy agent

1. In the client: Push the Helpdesk folder as a new repository, e.g. qa-helpdesk.
2. Deployments → New application:

    Name           helpdesk                (the client finds the helpdesk by this name; see Helpdesk:AppName)
    Repository     <you>/qa-helpdesk
    Kind           dotnet
    Project        QualityAviationHelpdesk/QualityAviationHelpdesk.csproj
    Public port    e.g. 8443, HTTPS on     (the client refuses to send its login over plain HTTP)
    Health path    /health
    Run tests      yes

3. Environment (stored encrypted on the server, never in the repository):

    ConnectionStrings__Helpdesk   Server=<sql>;Database=QAHelpdesk;User Id=qa_helpdesk_app;Password=...;Encrypt=True;TrustServerCertificate=True
    ConnectionStrings__Legacy     Server=<sql>;Database=TraacsSupportDB;User Id=qa_helpdesk_legacy;Password=...;ApplicationIntent=ReadOnly;Encrypt=True;TrustServerCertificate=True

   GatePass:AgentUrl (https://127.0.0.1:5003) and GatePass:AgentThumbprint (the live server certificate) are already set
   in appsettings.json. If the certificate is ever replaced, set GatePass__AgentThumbprint to the new thumbprint.

4. Deploy. The pipeline restores, checks security, builds, runs the tests, publishes, starts the new release beside the old
   one and switches only when /health answers. On first start the portal creates its tables.
5. Open the public port in the server firewall (admin prompt):

    netsh advfirewall firewall add rule name="Q+ Helpdesk" dir=in action=allow protocol=TCP localport=8443

Staff open https://<server>:8443/ and sign in with their Q+ CMR user name (or e-mail) and password. The server
certificate is the internal self-signed one: browsers warn until it is trusted on the PCs (install it via group policy,
or replace it with a company-issued certificate in the agent's TlsSettings).

👥 3. Who may do what

    Requester    every active Q+ CMR user (Portal:RequesterRoles to restrict)
    Executive    Q+ CMR roles in Portal:ExecutiveRoles, or users in Portal:ExecutiveUsers: the read-only dashboard
                 default: Administrator, Admin, Corporate Admin, Corporate Supervisor, Corporate Ticketing Supervisor,
                          Aeroglobe Supervisor, User Activity Viewer, Git User
    IT           every Git client login (the repository API's users)
    IT lead      deploy-agent administrators (QualityAviationDeployAgent.exe admin add <login>) and GatePass:ItLeads:
                 also form fields, settings and the audit check

A change in Q+ CMR (role added or removed, account locked, password reset) takes effect in the portal within a minute,
without anyone signing in again. Change these lists in the Environment, e.g. Portal__ExecutiveRoles__0=Administrator.

🎫 4. A ticket's life

    New → Accepted → In progress ⇄ On hold → Resolved → Closed
          └─ Rejected (reason shown to the requester)       └─ Reopened by the requester within Settings: reopen days

Resolve and reject need a note the requester reads. A resolved ticket closes by itself after Settings: auto-close days.
Every step, note, edit and file is in the ticket's history and the audit trail; the person who resolves a ticket is the
one the dashboard credits.

🔧 5. Settings at run time (Git client → Helpdesk → Form & settings, IT leads)

    Form fields       text, multi-line, number, date, choice, multi-choice, tick box; for the requester or IT only;
                      for all ticket types or some; required or not; order; hide (values are kept)
    Ticket types      the list on the web form
    Targets           hours to resolve per priority P1 to P4 (wall-clock hours from when the ticket was raised)
    Reopen / close    days a requester may reopen; days until resolved tickets close by themselves (0 = never)
    Files             largest attachment (MB); at most 5 per upload; images, PDF, Office, zip, text, e-mail

🛡️ 6. Security notes

- Nothing secret is in the repository. Connection strings live in the agent's encrypted Environment.
- The audit trail is chained by SHA-256; the dashboard shows whether it is intact, IT leads can verify it any time.
- Attachments are checked by their contents and never opened by the client; images are previewed inside the client.
- Files and cookie keys live in the application's data folder (QA_DATA_DIR, kept across releases). Back it up with
  the QAHelpdesk database.

🧪 7. Developing

    dotnet test QualityAviationHelpdesk.sln -p:NuGetAudit=false

appsettings.Development.json points at a local SQL Server: QAHelpdesk_Dev (created empty; the schema is applied at
start-up) and a copy of the Q+ CMR database as the legacy source.
