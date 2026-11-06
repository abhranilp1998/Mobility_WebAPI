-- Run once using the approved database deployment identity, in the dedicated
-- database referenced by ConnectionStrings:DashboardSessions. The API identity
-- needs SELECT/INSERT/UPDATE on these tables, not schema-change permissions.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.MobilityApiSessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MobilityApiSessions
    (
        SessionId uniqueidentifier NOT NULL CONSTRAINT PK_MobilityApiSessions PRIMARY KEY,
        AccountSubject nvarchar(128) NOT NULL,
        ClientCode nvarchar(128) NOT NULL,
        Username nvarchar(256) NOT NULL,
        UserType int NOT NULL CONSTRAINT CK_MobilityApiSessions_UserType CHECK (UserType IN (0, 1)),
        DatabaseAlias nvarchar(128) NOT NULL,
        CustomersJson nvarchar(max) NOT NULL CONSTRAINT CK_MobilityApiSessions_Customers CHECK (ISJSON(CustomersJson)=1),
        CustomerId nvarchar(128) NOT NULL,
        AccessTokenHash binary(32) NOT NULL,
        CreatedUtc datetimeoffset(7) NOT NULL,
        AccessExpiresUtc datetimeoffset(7) NOT NULL,
        AbsoluteExpiresUtc datetimeoffset(7) NOT NULL,
        RevokedUtc datetimeoffset(7) NULL,
        CONSTRAINT CK_MobilityApiSessions_Expiry CHECK
          (CreatedUtc < AccessExpiresUtc AND AccessExpiresUtc <= AbsoluteExpiresUtc)
    );
    CREATE UNIQUE INDEX UX_MobilityApiSessions_AccessHash ON dbo.MobilityApiSessions(AccessTokenHash);
    CREATE INDEX IX_MobilityApiSessions_Account ON dbo.MobilityApiSessions(AccountSubject);
    CREATE INDEX IX_MobilityApiSessions_Expiry ON dbo.MobilityApiSessions(AbsoluteExpiresUtc);
END;

IF OBJECT_ID(N'dbo.MobilityApiRefreshTokens', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MobilityApiRefreshTokens
    (
        TokenHash binary(32) NOT NULL CONSTRAINT PK_MobilityApiRefreshTokens PRIMARY KEY,
        SessionId uniqueidentifier NOT NULL,
        ExpiresUtc datetimeoffset(7) NOT NULL,
        ConsumedUtc datetimeoffset(7) NULL,
        CONSTRAINT FK_MobilityApiRefreshTokens_Session FOREIGN KEY(SessionId)
          REFERENCES dbo.MobilityApiSessions(SessionId) ON DELETE CASCADE
    );
    CREATE INDEX IX_MobilityApiRefreshTokens_Session ON dbo.MobilityApiRefreshTokens(SessionId);
END;
COMMIT;

-- Retain consumed refresh hashes until the session's absolute expiry so replay
-- remains detectable. Schedule deletion of expired parent sessions using a
-- separate maintenance identity; child records are removed by the foreign key.
