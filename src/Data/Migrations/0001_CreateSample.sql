CREATE TABLE SAMPLE (
    RunId            UNIQUEIDENTIFIER NOT NULL,
    SequenceNumber   BIGINT NOT NULL,
    EquipmentId      INT NOT NULL,
    CollectedAtUtc   DATETIME2(3) NOT NULL,
    RecordedAtUtc    DATETIME2(3) NOT NULL DEFAULT GETUTCDATE(),
    Temperature      DECIMAL(5, 2),
    Vibration        DECIMAL(5, 2),             -- FLOAT에서 DECIMAL(5, 2)로 변경
    StatusCode       TINYINT,
    
    -- 1. 기본키(PK)를 비클러스터드(NONCLUSTERED)로 지정하여 고유성 보장
    CONSTRAINT PK_SAMPLE PRIMARY KEY NONCLUSTERED (EquipmentId, RunId, SequenceNumber)
);

-- 2. 장비별 수집 시간(EquipmentId, CollectedAtUtc) 기준의 클러스터드 인덱스 생성
CREATE CLUSTERED INDEX IX_SAMPLE_Equipment_CollectedAt 
ON SAMPLE (EquipmentId, CollectedAtUtc);
