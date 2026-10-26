CREATE TABLE dbo.FAULT (
    FaultId             INT IDENTITY(1,1) NOT NULL,
    AlarmId             INT NOT NULL,
    Status              TINYINT NOT NULL,
    RegisteredAtUtc     DATETIME2(3) NOT NULL,
    CauseCategory       TINYINT NOT NULL,
    ActionText          NVARCHAR(2000) NULL,
    CompletedAtUtc      DATETIME2(3) NULL,
    CancelReason        NVARCHAR(1000) NULL,
    Version             ROWVERSION NOT NULL,

    CONSTRAINT PK_FAULT PRIMARY KEY CLUSTERED (FaultId),
    CONSTRAINT UQ_FAULT_AlarmId UNIQUE (AlarmId),
    CONSTRAINT FK_FAULT_ALARM FOREIGN KEY (AlarmId)
        REFERENCES dbo.ALARM (AlarmId),
    CONSTRAINT CK_FAULT_Status CHECK (Status IN (1, 2, 3, 4))
);

-- Add the CauseCategory foreign key when the D-073 lookup table is defined.
CREATE TABLE dbo.FAULT_HISTORY (
    FaultHistoryId      INT IDENTITY(1,1) NOT NULL,
    FaultId             INT NOT NULL,
    ChangedByUserId     INT NOT NULL,
    ChangedAtUtc        DATETIME2(3) NOT NULL,
    PreviousStatus     TINYINT NOT NULL,
    NewStatus          TINYINT NOT NULL,
    Reason             NVARCHAR(1000) NULL,

    CONSTRAINT PK_FAULT_HISTORY PRIMARY KEY CLUSTERED (FaultHistoryId),
    CONSTRAINT FK_FAULT_HISTORY_FAULT FOREIGN KEY (FaultId)
        REFERENCES dbo.FAULT (FaultId),
    CONSTRAINT CK_FAULT_HISTORY_PreviousStatus CHECK (PreviousStatus IN (1, 2, 3, 4)),
    CONSTRAINT CK_FAULT_HISTORY_NewStatus CHECK (NewStatus IN (1, 2, 3, 4))
);

-- Add the ChangedByUserId foreign key when the user table is defined.
