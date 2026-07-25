/*
  REVIEW DRAFT ONLY - DO NOT APPLY.

  SQL Server catalog for immutable operational-dashboard definitions and
  whitelist registries. Business dashboard rows and attachment files remain in
  their existing systems of record.
*/

CREATE TABLE dbo.OperationalDashboard
(
    OperationalDashboardId bigint IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_OperationalDashboard PRIMARY KEY,
    DashboardCode varchar(100) NOT NULL,
    ScreenId varchar(150) NOT NULL,
    Title nvarchar(200) NOT NULL,
    LegacyFallbackScreenId varchar(150) NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_OperationalDashboard_IsEnabled DEFAULT (1),
    CreatedAtUtc datetime2(3) NOT NULL
        CONSTRAINT DF_OperationalDashboard_CreatedAtUtc DEFAULT (sysutcdatetime()),
    CreatedBy nvarchar(200) NOT NULL,
    CONSTRAINT UQ_OperationalDashboard_DashboardCode UNIQUE (DashboardCode),
    CONSTRAINT UQ_OperationalDashboard_ScreenId UNIQUE (ScreenId)
);

CREATE TABLE dbo.OperationalDashboardVersion
(
    OperationalDashboardVersionId bigint IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_OperationalDashboardVersion PRIMARY KEY,
    OperationalDashboardId bigint NOT NULL,
    DefinitionVersion varchar(32) NOT NULL,
    RendererSchemaVersion int NOT NULL,
    MinRendererVersion int NOT NULL,
    DataSourceCode varchar(100) NOT NULL,
    DefinitionJson nvarchar(max) NOT NULL,
    DefinitionSha256 char(64) NOT NULL,
    PublicationStatus varchar(20) NOT NULL,
    PublishedAtUtc datetime2(3) NULL,
    PublishedBy nvarchar(200) NULL,
    SupersededAtUtc datetime2(3) NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT FK_OperationalDashboardVersion_Dashboard
        FOREIGN KEY (OperationalDashboardId)
        REFERENCES dbo.OperationalDashboard (OperationalDashboardId),
    CONSTRAINT UQ_OperationalDashboardVersion
        UNIQUE (OperationalDashboardId, DefinitionVersion),
    CONSTRAINT CK_OperationalDashboardVersion_RendererSchema
        CHECK (RendererSchemaVersion > 0),
    CONSTRAINT CK_OperationalDashboardVersion_MinRenderer
        CHECK (MinRendererVersion > 0),
    CONSTRAINT CK_OperationalDashboardVersion_Status
        CHECK (PublicationStatus IN ('Draft', 'Published', 'Superseded', 'Retired')),
    CONSTRAINT CK_OperationalDashboardVersion_DefinitionJson
        CHECK (ISJSON(DefinitionJson) = 1)
);

CREATE UNIQUE INDEX UX_OperationalDashboardVersion_OnePublished
ON dbo.OperationalDashboardVersion (OperationalDashboardId)
WHERE PublicationStatus = 'Published';

CREATE TABLE dbo.OperationalDashboardCapability
(
    OperationalDashboardVersionId bigint NOT NULL,
    CapabilityCode varchar(100) NOT NULL,
    IsRequired bit NOT NULL CONSTRAINT DF_OperationalDashboardCapability_Required DEFAULT (1),
    CONSTRAINT PK_OperationalDashboardCapability
        PRIMARY KEY (OperationalDashboardVersionId, CapabilityCode),
    CONSTRAINT FK_OperationalDashboardCapability_Version
        FOREIGN KEY (OperationalDashboardVersionId)
        REFERENCES dbo.OperationalDashboardVersion (OperationalDashboardVersionId)
);

CREATE TABLE dbo.DashboardDataSourceRegistry
(
    DataSourceCode varchar(100) NOT NULL
        CONSTRAINT PK_DashboardDataSourceRegistry PRIMARY KEY,
    HandlerKey varchar(150) NOT NULL,
    AuthorizationPolicy varchar(150) NOT NULL,
    TimeoutSeconds smallint NOT NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_DashboardDataSourceRegistry_Enabled DEFAULT (0),
    Description nvarchar(500) NULL,
    UpdatedAtUtc datetime2(3) NOT NULL
        CONSTRAINT DF_DashboardDataSourceRegistry_Updated DEFAULT (sysutcdatetime()),
    UpdatedBy nvarchar(200) NOT NULL,
    CONSTRAINT UQ_DashboardDataSourceRegistry_HandlerKey UNIQUE (HandlerKey),
    CONSTRAINT CK_DashboardDataSourceRegistry_Timeout
        CHECK (TimeoutSeconds BETWEEN 1 AND 120)
);

CREATE TABLE dbo.DashboardOptionSourceRegistry
(
    OptionSourceCode varchar(100) NOT NULL
        CONSTRAINT PK_DashboardOptionSourceRegistry PRIMARY KEY,
    HandlerKey varchar(150) NOT NULL,
    AuthorizationPolicy varchar(150) NOT NULL,
    TimeoutSeconds smallint NOT NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_DashboardOptionSourceRegistry_Enabled DEFAULT (0),
    Description nvarchar(500) NULL,
    UpdatedAtUtc datetime2(3) NOT NULL
        CONSTRAINT DF_DashboardOptionSourceRegistry_Updated DEFAULT (sysutcdatetime()),
    UpdatedBy nvarchar(200) NOT NULL,
    CONSTRAINT UQ_DashboardOptionSourceRegistry_HandlerKey UNIQUE (HandlerKey),
    CONSTRAINT CK_DashboardOptionSourceRegistry_Timeout
        CHECK (TimeoutSeconds BETWEEN 1 AND 120)
);

CREATE TABLE dbo.DashboardActionRegistry
(
    ActionCode varchar(100) NOT NULL
        CONSTRAINT PK_DashboardActionRegistry PRIMARY KEY,
    ActionKind varchar(30) NOT NULL,
    HandlerKey varchar(150) NULL,
    NavigationCode varchar(100) NULL,
    AuthorizationPolicy varchar(150) NOT NULL,
    InputSchemaJson nvarchar(max) NOT NULL,
    IsMutation bit NOT NULL,
    RequiresIdempotencyKey bit NOT NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_DashboardActionRegistry_Enabled DEFAULT (0),
    Description nvarchar(500) NULL,
    UpdatedAtUtc datetime2(3) NOT NULL
        CONSTRAINT DF_DashboardActionRegistry_Updated DEFAULT (sysutcdatetime()),
    UpdatedBy nvarchar(200) NOT NULL,
    CONSTRAINT CK_DashboardActionRegistry_ActionKind
        CHECK (ActionKind IN ('serverAction', 'clientNavigation', 'clientDialog')),
    CONSTRAINT CK_DashboardActionRegistry_Target
        CHECK (
            (ActionKind = 'serverAction' AND HandlerKey IS NOT NULL AND NavigationCode IS NULL) OR
            (ActionKind = 'clientNavigation' AND HandlerKey IS NULL AND NavigationCode IS NOT NULL) OR
            (ActionKind = 'clientDialog' AND HandlerKey IS NULL AND NavigationCode IS NULL)
        ),
    CONSTRAINT CK_DashboardActionRegistry_Mutation
        CHECK (IsMutation = 0 OR ActionKind = 'serverAction'),
    CONSTRAINT CK_DashboardActionRegistry_Idempotency
        CHECK (RequiresIdempotencyKey = 0 OR IsMutation = 1),
    CONSTRAINT CK_DashboardActionRegistry_InputSchemaJson
        CHECK (ISJSON(InputSchemaJson) = 1)
);

CREATE UNIQUE INDEX UX_DashboardActionRegistry_HandlerKey
ON dbo.DashboardActionRegistry (HandlerKey)
WHERE HandlerKey IS NOT NULL;

CREATE UNIQUE INDEX UX_DashboardActionRegistry_NavigationCode
ON dbo.DashboardActionRegistry (NavigationCode)
WHERE NavigationCode IS NOT NULL;

CREATE TABLE dbo.DashboardNavigationRegistry
(
    NavigationCode varchar(100) NOT NULL
        CONSTRAINT PK_DashboardNavigationRegistry PRIMARY KEY,
    ClientHandlerKey varchar(150) NOT NULL,
    ArgumentSchemaJson nvarchar(max) NOT NULL,
    IsEnabled bit NOT NULL CONSTRAINT DF_DashboardNavigationRegistry_Enabled DEFAULT (0),
    Description nvarchar(500) NULL,
    UpdatedAtUtc datetime2(3) NOT NULL
        CONSTRAINT DF_DashboardNavigationRegistry_Updated DEFAULT (sysutcdatetime()),
    UpdatedBy nvarchar(200) NOT NULL,
    CONSTRAINT UQ_DashboardNavigationRegistry_ClientHandlerKey
        UNIQUE (ClientHandlerKey),
    CONSTRAINT CK_DashboardNavigationRegistry_ArgumentSchemaJson
        CHECK (ISJSON(ArgumentSchemaJson) = 1)
);

ALTER TABLE dbo.DashboardActionRegistry
ADD CONSTRAINT FK_DashboardActionRegistry_Navigation
    FOREIGN KEY (NavigationCode)
    REFERENCES dbo.DashboardNavigationRegistry (NavigationCode);

CREATE TABLE dbo.OperationalDashboardAction
(
    OperationalDashboardVersionId bigint NOT NULL,
    ActionCode varchar(100) NOT NULL,
    Placement varchar(40) NOT NULL,
    DisplayOrder int NOT NULL,
    VisibilityRuleCode varchar(100) NULL,
    SuccessEffect varchar(40) NOT NULL,
    CONSTRAINT PK_OperationalDashboardAction
        PRIMARY KEY (OperationalDashboardVersionId, ActionCode),
    CONSTRAINT FK_OperationalDashboardAction_Version
        FOREIGN KEY (OperationalDashboardVersionId)
        REFERENCES dbo.OperationalDashboardVersion (OperationalDashboardVersionId),
    CONSTRAINT FK_OperationalDashboardAction_Registry
        FOREIGN KEY (ActionCode)
        REFERENCES dbo.DashboardActionRegistry (ActionCode),
    CONSTRAINT CK_OperationalDashboardAction_Placement
        CHECK (Placement IN ('rowTap', 'rowTrailing', 'cardFooter', 'titleTap', 'dialog')),
    CONSTRAINT CK_OperationalDashboardAction_SuccessEffect
        CHECK (SuccessEffect IN
            ('none', 'refreshDashboard', 'refreshRow', 'localRowPatch', 'clientNavigation'))
);

CREATE TABLE dbo.OperationalDashboardActionPermission
(
    OperationalDashboardVersionId bigint NOT NULL,
    ActionCode varchar(100) NOT NULL,
    SourceType varchar(20) NOT NULL CONSTRAINT DF_OperationalDashboardActionPermission_Source DEFAULT ('*'),
    PermissionCode varchar(100) NOT NULL,
    CONSTRAINT PK_OperationalDashboardActionPermission
        PRIMARY KEY
            (OperationalDashboardVersionId, ActionCode, SourceType, PermissionCode),
    CONSTRAINT FK_OperationalDashboardActionPermission_Action
        FOREIGN KEY (OperationalDashboardVersionId, ActionCode)
        REFERENCES dbo.OperationalDashboardAction
            (OperationalDashboardVersionId, ActionCode)
);

CREATE TABLE dbo.OperationalDashboardPublicationAudit
(
    OperationalDashboardPublicationAuditId bigint IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_OperationalDashboardPublicationAudit PRIMARY KEY,
    OperationalDashboardVersionId bigint NOT NULL,
    EventType varchar(30) NOT NULL,
    OccurredAtUtc datetime2(3) NOT NULL
        CONSTRAINT DF_OperationalDashboardPublicationAudit_Occurred DEFAULT (sysutcdatetime()),
    Actor nvarchar(200) NOT NULL,
    Reason nvarchar(1000) NULL,
    CONSTRAINT FK_OperationalDashboardPublicationAudit_Version
        FOREIGN KEY (OperationalDashboardVersionId)
        REFERENCES dbo.OperationalDashboardVersion (OperationalDashboardVersionId),
    CONSTRAINT CK_OperationalDashboardPublicationAudit_EventType
        CHECK (EventType IN ('Created', 'Published', 'Superseded', 'RolledBack', 'Retired'))
);

/*
  Deliberately absent:
  - raw SQL or stored-procedure names in definition rows;
  - arbitrary URLs, .NET type names, method names, or scripts;
  - tenant connection strings;
  - copies of CR01/GN25 attachment binaries;
  - business dashboard row caches.

  HandlerKey values identify implementations compiled and registered in the
  service. Enabling a registry row does not create a new executable target.
*/
