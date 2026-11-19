-- Least-privilege SQL logins for the Q+ Helpdesk portal. Run once as a DBA (sysadmin) on the SQL Server.
-- Replace both passwords before running, and keep them out of source control: they go into the deploy agent's
-- encrypted Environment settings of the helpdesk application (ConnectionStrings__Helpdesk / ConnectionStrings__Legacy).
--
--   qa_helpdesk_app     owns the helpdesk database (QAHelpdesk). It can create the [hd] schema on first start.
--                       Once the schema is in place you may drop it to db_datareader + db_datawriter + EXECUTE
--                       and set Database:AutoMigrate=false; new schema versions are then run by a DBA.
--   qa_helpdesk_legacy  may only READ the four legacy tables the portal needs. It cannot see anything else in
--                       TraacsSupportDB (finance, partners, settlements...) and cannot change anything.

USE master;
GO
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'QAHelpdesk')
    CREATE DATABASE QAHelpdesk;
GO
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'qa_helpdesk_app')
    CREATE LOGIN qa_helpdesk_app WITH PASSWORD = N'<CHANGE-ME strong password 1>', CHECK_POLICY = ON;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'qa_helpdesk_legacy')
    CREATE LOGIN qa_helpdesk_legacy WITH PASSWORD = N'<CHANGE-ME strong password 2>', CHECK_POLICY = ON;
GO

USE QAHelpdesk;
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'qa_helpdesk_app')
    CREATE USER qa_helpdesk_app FOR LOGIN qa_helpdesk_app;
ALTER ROLE db_owner ADD MEMBER qa_helpdesk_app;
GO

-- The legacy database: change the name if yours differs
USE TraacsSupportDB;
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'qa_helpdesk_legacy')
    CREATE USER qa_helpdesk_legacy FOR LOGIN qa_helpdesk_legacy;
GRANT SELECT ON dbo.AspNetUsers         TO qa_helpdesk_legacy;
GRANT SELECT ON dbo.AspNetRoles         TO qa_helpdesk_legacy;
GRANT SELECT ON dbo.AspNetUserRoles     TO qa_helpdesk_legacy;
GRANT SELECT ON dbo.AppTraacsUserTypes  TO qa_helpdesk_legacy;
DENY INSERT, UPDATE, DELETE, ALTER ON SCHEMA::dbo TO qa_helpdesk_legacy;
GO
