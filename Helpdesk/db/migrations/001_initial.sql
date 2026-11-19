-- Q+ Helpdesk, schema version 001.
-- Applied automatically by the portal at start-up (Database:AutoMigrate), or by a DBA running this file once.
-- Everything lives in the [hd] schema of the helpdesk's own database. The legacy Q+ CMR database is never written to.

IF SCHEMA_ID('hd') IS NULL EXEC('CREATE SCHEMA hd');
GO

-- Tickets raised by Q+ CMR users. Requester details are copied from the legacy user directory when the
-- ticket is raised, so a report still shows who asked even after the account is renamed or removed there.
CREATE TABLE hd.Tickets (
    Id                  int IDENTITY(1001, 1) NOT NULL CONSTRAINT PK_Tickets PRIMARY KEY,
    Type                nvarchar(60)   NOT NULL,
    Summary             nvarchar(200)  NOT NULL,
    Description         nvarchar(max)  NOT NULL,
    Priority            tinyint        NOT NULL CONSTRAINT CK_Tickets_Priority CHECK (Priority BETWEEN 1 AND 4),
    Status              nvarchar(20)   NOT NULL,
    RequesterId         nvarchar(450)  NOT NULL,
    RequesterUserName   nvarchar(256)  NOT NULL,
    RequesterName       nvarchar(256)  NOT NULL,
    RequesterEmail      nvarchar(256)  NULL,
    RequesterDepartment nvarchar(250)  NULL,
    Assignee            nvarchar(100)  NULL,
    CreatedAt           datetime2(3)   NOT NULL,
    UpdatedAt           datetime2(3)   NOT NULL,
    AcceptedAt          datetime2(3)   NULL,
    StartedAt           datetime2(3)   NULL,
    ResolvedAt          datetime2(3)   NULL,
    ResolvedBy          nvarchar(100)  NULL,
    ClosedAt            datetime2(3)   NULL,
    DueAt               datetime2(3)   NULL,
    Resolution          nvarchar(4000) NULL,
    StatusReason        nvarchar(1000) NULL,
    MinutesSpent        int            NOT NULL CONSTRAINT DF_Tickets_Minutes DEFAULT 0,
    ReopenCount         int            NOT NULL CONSTRAINT DF_Tickets_Reopen DEFAULT 0,
    RowVersion          rowversion     NOT NULL
);
CREATE INDEX IX_Tickets_Status    ON hd.Tickets (Status, Priority) INCLUDE (Assignee, CreatedAt, DueAt);
CREATE INDEX IX_Tickets_Requester ON hd.Tickets (RequesterId, CreatedAt DESC);
CREATE INDEX IX_Tickets_Assignee  ON hd.Tickets (Assignee, Status);
CREATE INDEX IX_Tickets_Created   ON hd.Tickets (CreatedAt) INCLUDE (RequesterDepartment, Type, Status, Priority);
CREATE INDEX IX_Tickets_Resolved  ON hd.Tickets (ResolvedAt) INCLUDE (ResolvedBy, DueAt, CreatedAt, ReopenCount);
GO

-- The form's extra fields, defined at run time from the IT client
CREATE TABLE hd.FieldDefinitions (
    Id        int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_FieldDefinitions PRIMARY KEY,
    [Key]     nvarchar(60)   NOT NULL CONSTRAINT UQ_FieldDefinitions_Key UNIQUE,
    Label     nvarchar(120)  NOT NULL,
    Kind      nvarchar(20)   NOT NULL,
    Options   nvarchar(2000) NULL,
    HelpText  nvarchar(300)  NULL,
    Required  bit            NOT NULL,
    Active    bit            NOT NULL,
    Audience  nvarchar(20)   NOT NULL,
    AppliesTo nvarchar(400)  NULL,
    MaxLength int            NOT NULL,
    SortOrder int            NOT NULL,
    UpdatedBy nvarchar(100)  NOT NULL,
    UpdatedAt datetime2(3)   NOT NULL,
    RowVersion rowversion    NOT NULL
);

CREATE TABLE hd.TicketFieldValues (
    TicketId int           NOT NULL CONSTRAINT FK_FieldValues_Ticket REFERENCES hd.Tickets (Id),
    FieldKey nvarchar(60)  NOT NULL,
    Value    nvarchar(4000) NOT NULL,
    CONSTRAINT PK_TicketFieldValues PRIMARY KEY (TicketId, FieldKey)
);
GO

CREATE TABLE hd.Comments (
    Id           int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Comments PRIMARY KEY,
    TicketId     int            NOT NULL CONSTRAINT FK_Comments_Ticket REFERENCES hd.Tickets (Id),
    AuthorKind   nvarchar(20)   NOT NULL,
    AuthorId     nvarchar(450)  NOT NULL,
    AuthorName   nvarchar(256)  NOT NULL,
    Body         nvarchar(4000) NOT NULL,
    Internal     bit            NOT NULL,
    MinutesSpent int            NOT NULL CONSTRAINT DF_Comments_Minutes DEFAULT 0,
    CreatedAt    datetime2(3)   NOT NULL
);
CREATE INDEX IX_Comments_Ticket ON hd.Comments (TicketId, Id);
CREATE INDEX IX_Comments_Work   ON hd.Comments (CreatedAt) INCLUDE (AuthorKind, AuthorId, MinutesSpent);

-- Files are kept on disk (the app's data folder); this is what is known about each one
CREATE TABLE hd.Attachments (
    Id             uniqueidentifier NOT NULL CONSTRAINT PK_Attachments PRIMARY KEY,
    TicketId       int            NOT NULL CONSTRAINT FK_Attachments_Ticket REFERENCES hd.Tickets (Id),
    CommentId      int            NULL,
    FileName       nvarchar(200)  NOT NULL,
    ContentType    nvarchar(100)  NOT NULL,
    SizeBytes      bigint         NOT NULL,
    Sha256         char(64)       NOT NULL,
    StoredPath     nvarchar(300)  NOT NULL,
    Internal       bit            NOT NULL,
    UploadedByKind nvarchar(20)   NOT NULL,
    UploadedById   nvarchar(450)  NOT NULL,
    UploadedByName nvarchar(256)  NOT NULL,
    CreatedAt      datetime2(3)   NOT NULL
);
CREATE INDEX IX_Attachments_Ticket ON hd.Attachments (TicketId);
GO

-- The IT team's shared to-do list. Items are archived, never deleted, so the record stays.
CREATE TABLE hd.TodoItems (
    Id          int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_TodoItems PRIMARY KEY,
    Title       nvarchar(200)  NOT NULL,
    Notes       nvarchar(4000) NULL,
    Owner       nvarchar(100)  NULL,
    Status      nvarchar(20)   NOT NULL,
    Priority    tinyint        NOT NULL CONSTRAINT CK_TodoItems_Priority CHECK (Priority BETWEEN 1 AND 4),
    DueOn       date           NULL,
    TicketId    int            NULL CONSTRAINT FK_TodoItems_Ticket REFERENCES hd.Tickets (Id),
    Archived    bit            NOT NULL CONSTRAINT DF_TodoItems_Archived DEFAULT 0,
    CreatedBy   nvarchar(100)  NOT NULL,
    CreatedAt   datetime2(3)   NOT NULL,
    UpdatedBy   nvarchar(100)  NOT NULL,
    UpdatedAt   datetime2(3)   NOT NULL,
    CompletedBy nvarchar(100)  NULL,
    CompletedAt datetime2(3)   NULL,
    RowVersion  rowversion     NOT NULL
);
CREATE INDEX IX_TodoItems_Board ON hd.TodoItems (Archived, Status, Owner);
CREATE INDEX IX_TodoItems_Done  ON hd.TodoItems (CompletedAt) INCLUDE (CompletedBy);
GO

CREATE TABLE hd.Settings (
    [Key]     nvarchar(60)   NOT NULL CONSTRAINT PK_Settings PRIMARY KEY,
    Value     nvarchar(2000) NOT NULL,
    UpdatedBy nvarchar(100)  NOT NULL,
    UpdatedAt datetime2(3)   NOT NULL
);

-- IT team members as the portal has seen them (client logins), for assignment lists and reports
CREATE TABLE hd.ItMembers (
    Username  nvarchar(100) NOT NULL CONSTRAINT PK_ItMembers PRIMARY KEY,
    IsLead    bit           NOT NULL,
    FirstSeen datetime2(3)  NOT NULL,
    LastSeen  datetime2(3)  NOT NULL
);
GO

-- Append-only audit trail. Every row carries the SHA-256 of the row before it, so a row that is edited or
-- deleted afterwards breaks the chain and the portal reports it.
CREATE TABLE hd.AuditEvents (
    Id                 bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_AuditEvents PRIMARY KEY,
    EntityType         nvarchar(20)   NOT NULL,
    EntityId           nvarchar(60)   NOT NULL,
    Action             nvarchar(40)   NOT NULL,
    ActorKind          nvarchar(20)   NOT NULL,
    ActorId            nvarchar(450)  NOT NULL,
    ActorName          nvarchar(256)  NOT NULL,
    Detail             nvarchar(4000) NULL,
    VisibleToRequester bit            NOT NULL,
    Ip                 nvarchar(64)   NULL,
    At                 datetime2(3)   NOT NULL,
    PrevHash           char(64)       NOT NULL,
    Hash               char(64)       NOT NULL
);
CREATE INDEX IX_AuditEvents_Entity ON hd.AuditEvents (EntityType, EntityId, Id);
GO

-- What changed, in order. Clients and browsers follow it to stay in sync; old rows are pruned.
CREATE TABLE hd.Changes (
    Seq      bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Changes PRIMARY KEY,
    Topic    nvarchar(20)  NOT NULL,
    EntityId nvarchar(60)  NOT NULL,
    At       datetime2(3)  NOT NULL
);

-- Failed sign-ins per account, shared by every running copy of the portal
CREATE TABLE hd.LoginThrottle (
    [Key]       nvarchar(300) NOT NULL CONSTRAINT PK_LoginThrottle PRIMARY KEY,
    Failures    int           NOT NULL,
    WindowStart datetime2(3)  NOT NULL,
    LockedUntil datetime2(3)  NULL
);
GO

-- Starting configuration. All of it can be changed from the IT client.
INSERT INTO hd.Settings ([Key], Value, UpdatedBy, UpdatedAt) VALUES
    (N'ticket.types', N'Complaint
Automation request
New requirement
Bug / error
Access request
Hardware / network
Other', N'system', SYSUTCDATETIME()),
    (N'sla.hours', N'4,24,72,168', N'system', SYSUTCDATETIME()),
    (N'reopen.days', N'7', N'system', SYSUTCDATETIME()),
    (N'autoclose.days', N'5', N'system', SYSUTCDATETIME()),
    (N'attachments.maxmb', N'10', N'system', SYSUTCDATETIME());

INSERT INTO hd.FieldDefinitions ([Key], Label, Kind, Options, HelpText, Required, Active, Audience, AppliesTo, MaxLength, SortOrder, UpdatedBy, UpdatedAt) VALUES
    (N'system', N'Application / system', N'select', N'Q+ CMR
Traacs
Email
Internet / network
Printer / scanner
Computer / laptop
Other', N'Where the problem is, or what the work is for.', 1, 1, N'requester', NULL, 200, 10, N'system', SYSUTCDATETIME()),
    (N'impact', N'Business impact', N'select', N'Blocks my work completely
Slows my work down
Minor inconvenience
Improvement, not urgent', N'How much this affects your work today.', 1, 1, N'requester', NULL, 200, 20, N'system', SYSUTCDATETIME()),
    (N'steps', N'Steps to reproduce', N'textarea', NULL, N'What you did, what you expected, and what happened instead.', 0, 1, N'requester', N'Bug / error
Complaint', 2000, 30, N'system', SYSUTCDATETIME()),
    (N'benefit', N'Expected benefit', N'textarea', NULL, N'Time saved, errors avoided, who else benefits.', 0, 1, N'requester', N'Automation request
New requirement', 2000, 40, N'system', SYSUTCDATETIME()),
    (N'contact', N'Contact number / extension', N'text', NULL, NULL, 0, 1, N'requester', NULL, 40, 50, N'system', SYSUTCDATETIME()),
    (N'root_cause', N'Root cause', N'textarea', NULL, N'Filled in by IT when the work is done.', 0, 1, N'it', NULL, 2000, 100, N'system', SYSUTCDATETIME()),
    (N'estimate_hours', N'Effort estimate (hours)', N'number', NULL, NULL, 0, 1, N'it', NULL, 20, 110, N'system', SYSUTCDATETIME()),
    (N'repository', N'Repository / release', N'text', NULL, N'Where the change lives, e.g. qaim/qa-client v20261119_101500.', 0, 1, N'it', NULL, 200, 120, N'system', SYSUTCDATETIME());
GO
