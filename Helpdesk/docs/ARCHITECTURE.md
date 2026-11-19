# Q+ Helpdesk — architecture

Status: built and tested end to end on a dev machine (2026-11-19). Not yet deployed on the server.

## 1. What it does

| # | Module | Who | Where |
|---|--------|-----|-------|
| 1 | Raise a ticket: type, summary, problem, priority/severity, run-time fields, screenshots/documents. Name, e-mail, department and user name come from Q+ CMR, not the form | every active Q+ CMR user | web portal |
| 2 | Review and execute: accept, reject, assign/take, start, hold, resolve, close, reopen, notes (internal or public), time logged, files, full history | IT team | Git client → Helpdesk tab (and terminal menu H) |
| 3 | Run-time form: add/edit/reorder/hide fields (requester or IT-only), edit ticket details and field values, ticket types, resolution targets, reopen/auto-close windows, file size | IT leads (fields, settings); all IT (ticket details) | Git client |
| 4 | Management dashboard: KPIs, raised vs resolved, backlog by priority, departments, types, team leaderboard credited by client user name, recent completions, audit-trail integrity. Read-only | Q+ CMR roles in `Portal:ExecutiveRoles` | web portal |
| 5 | IT to-do board: who is doing what, blocked, done; nothing deleted (archive), every change in the history; live for everyone | IT team | Git client |

## 2. Components

```
 Staff browsers (LAN)                          IT PCs: Git client (Windows app / terminal)
        │ HTTPS, server certificate                    │ HTTPS, pinned thumbprint, Bearer gate pass
        ▼                                              ▼
 ┌──────────── QA Deploy Agent (existing) ─────────────────────────────┐
 │ proxy :<public port>  ──►  Q+ Helpdesk process on 127.0.0.1:<private>│
 │ /api/deploy/overview  ◄── gate-pass check (who is this?)             │
 └──────────────────────────────────────────────────────────────────────┘
        │                                   │                         │
        ▼                                   ▼                         ▼
 QAHelpdesk DB [hd] (read/write)    TraacsSupportDB (SELECT only:   QA_DATA_DIR
 tickets, fields, to-do, audit,     AspNetUsers, AspNetRoles,       attachments, cookie keys
 change feed, settings              AspNetUserRoles, AppTraacsUserTypes)
```

The repository API is not touched. The portal is one ASP.NET 9 process (Razor Pages + minimal APIs), deployed and supervised
by the deploy agent like any other application, and can run as several copies at once (a new release starts beside the live one).

## 3. Zero trust, concretely

| Principle | How |
|-----------|-----|
| Every request is authenticated | Fallback policy denies anonymous access; only `/login`, `/health`, `/error`, `/denied` and static files are open |
| Identity comes from its source of truth | Web: password verified against the legacy ASP.NET Identity hash (V3, read-only). IT: gate pass checked by the deploy agent, which runs the repository API's own code on its own keys |
| Sessions keep proving themselves | Web session re-checked against Q+ CMR every 60 s (locked out, removed, password reset via SecurityStamp, roles changed) and ends after 12 h however active. Gate pass result cached 60 s at most. Live streams close every 5 min and reconnect through authentication again |
| Least privilege | Separate policies: requester, executive (read-only), IT, IT lead. Requesters only ever see their own tickets (someone else's answers 404, like a missing one), never internal notes, files or IT-only fields. The legacy login can SELECT four tables and nothing else (`db/admin/create-logins.sql`); the legacy DbContext refuses to save |
| Two credential types never mix | IT API accepts only Bearer gate passes (a browser cookie is ignored, so no CSRF); web pages accept only the cookie, with anti-forgery tokens on every POST |
| Assume breach | Hash-chained, append-only audit trail (SHA-256 over every event and the one before); verified every 15 min and on demand. Attachments checked by content (magic bytes), stored outside the web root under random names, served with `Content-Disposition: attachment` and a sandbox CSP. Strict CSP with no inline script anywhere. Cookie keys encrypted with DPAPI |
| Abuse limits | Per-address global limit, sign-in limit, per-account lockout (5 failures / 15 min, shared by all copies), per-user write and API limits, concurrent long-poll limit, limit on unknown gate passes sent to the agent |

## 4. Data and sync

- **Optimistic concurrency** on tickets, fields and to-dos (`rowversion`, sent back as `Version`): two people can never overwrite each other silently; the second gets a 409 and sees the newer state.
- **One write path** (`Services/Writes.cs`): the change, its audit events and its change-feed rows commit in one transaction. The feed and chain ends are extended under an application lock taken after the change is saved (milliseconds), so feed order equals commit order.
- **Live updates**: one loop per portal copy reads `hd.Changes` (immediately after a local write, else every second; picks up other copies' writes) into memory. IT clients long-poll `/api/it/changes` (answered within ~1 s of a change); browsers get server-sent events that only say "changed" — the page re-fetches with the viewer's own rights, so the stream never leaks data. A thousand open pages cost one query a second.
- **Caching**: settings and field definitions 10 s; legacy profiles 60 s; dashboard recomputed only when the change feed moved (or every 2 min); gate-pass checks 60 s.
- **Scale**: all lists are paged and indexed (`Status, Priority`, `RequesterId`, `Assignee`, `CreatedAt`, `ResolvedAt`); dashboard numbers are SQL `GROUP BY`s; pooled DbContexts; response compression. Generated SQL is compatible with SQL Server 2014+.

## 5. Decisions

### ADR-1: Users and roles stay in the legacy Q+ CMR database (read-only)

**Status:** Accepted · **Context:** Q+ CMR (.NET 5) already manages every employee, role and department (`AppTraacsUserTypes`).
**Decision:** Read them live; never copy or write them. Passwords are verified with the same ASP.NET Identity hasher.

| | Read legacy live (chosen) | Migrate users into the helpdesk |
|---|---|---|
| Complexity | Low | Medium (sync job, conflicts) |
| Security | One place to lock someone out; effective within 60 s | Two places; drift |
| Coupling | Depends on 4 legacy tables' shape | None after migration |

**Consequences:** a schema change to `AspNetUsers` in Q+ CMR must keep those columns. Requester details are copied onto each ticket when it is raised, so reports survive renames.

### ADR-2: IT signs in with the Git client's existing gate pass, checked through the deploy agent

**Context:** IT already logs in to the client; "every trail should be completed from there". The repository API is live and must not change.
**Decision:** The client sends its gate pass; the portal asks the agent (`/api/deploy/overview`, pinned certificate) who it belongs to, caching 60 s. IT leads = deploy-agent administrators + `GatePass:ItLeads`.

| | Agent check (chosen) | Read the API's key files | Second login |
|---|---|---|---|
| API change | None | None | None |
| Portal privileges | None extra | Read access to keys and users.json (password hashes) | None |
| Revocation | ≤ 60 s | Immediate | Separate |
| User effort | None | None | Two passwords |

**Revisit:** a tiny `GET /api/whoami` on the agent would make the check lighter than the overview call.

### ADR-3: Live sync with server-sent events and long polling (no SignalR)

SignalR needs a client package in the WinForms app (NuGet is unreliable here) and a JS library (no CDN on the LAN). SSE is native in browsers; long polling is plain HTTP for the client. Both wait on the same in-memory feed. **Revisit** if the portal ever runs on several servers behind a balancer with thousands of IT clients.

### ADR-4: Run-time fields as definitions + key/value rows

`hd.FieldDefinitions` (kind, options, audience, applies-to types, required, order) and `hd.TicketFieldValues (TicketId, FieldKey, Value)`. Server-side validation per definition; unknown or retired keys are dropped on input and kept on existing tickets. Chosen over a JSON column for indexability, simpler validation and SQL Server 2014 compatibility.

### ADR-5: A separate helpdesk database

All new tables in their own database (`QAHelpdesk`, schema `hd`), applied by numbered SQL scripts (`db/migrations`) that the app runs under a lock at start-up or a DBA runs by hand. The legacy database receives no new objects, and its login stays read-only.

## 6. What to revisit as it grows

- Business-hours resolution targets (now wall-clock hours).
- E-mail notifications to requesters (the legacy SMTP settings exist, but sending mail was out of scope).
- Archiving very old audit rows (the chain check walks every row; fine into the millions).
- Moving attachments to a file share if the server's data disk gets small.
