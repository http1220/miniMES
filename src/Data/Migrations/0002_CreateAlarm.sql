CREATE TABLE dbo.ALARM (
    AlarmId             INT IDENTITY(1,1) NOT NULL,
    EquipmentId         INT NOT NULL,
    Cause               TINYINT NOT NULL,
    RaisedAtUtc         DATETIME2(3) NOT NULL,
    AcknowledgedAtUtc   DATETIME2(3) NULL,
    AcknowledgedBy      INT NULL,
    ClearedAtUtc        DATETIME2(3) NULL,

    CONSTRAINT PK_ALARM PRIMARY KEY (AlarmId),
    CONSTRAINT CK_ALARM_Cause CHECK (Cause IN (1, 2, 3))
);

-- Cause: 1 = 통신 이상, 2 = 온도 초과, 3 = 진동 초과
-- 장비별 동일 원인의 활성 알람은 하나만 허용
CREATE UNIQUE INDEX UX_ALARM_Equipment_Cause_Active
    ON dbo.ALARM (EquipmentId, Cause)
    WHERE ClearedAtUtc IS NULL;