CREATE TABLE dbo.ALARM_EVENT (
    RunId           UNIQUEIDENTIFIER NOT NULL,
    EventSequence   BIGINT NOT NULL,
    EquipmentId     INT NOT NULL,
    Cause           TINYINT NOT NULL,
    EventType       TINYINT NOT NULL,
    OccurredAtUtc   DATETIME2(3) NOT NULL,

    CONSTRAINT PK_ALARM_EVENT
        PRIMARY KEY CLUSTERED (RunId, EventSequence),

    -- 1 = 통신 이상, 2 = 온도 초과, 3 = 진동 초과
    CONSTRAINT CK_ALARM_EVENT_Cause
        CHECK (Cause IN (1, 2, 3)),

    -- 1 = 발생, 2 = 해제, 3 = 현재 상태 보고
    CONSTRAINT CK_ALARM_EVENT_EventType
        CHECK (EventType IN (1, 2, 3))
);