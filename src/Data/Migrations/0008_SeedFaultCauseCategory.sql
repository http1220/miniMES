CREATE TABLE dbo.FAULT_CAUSE_CATEGORY (
    CauseCategoryId TINYINT NOT NULL,
    Name            NVARCHAR(50) NOT NULL,

    CONSTRAINT PK_FAULT_CAUSE_CATEGORY PRIMARY KEY CLUSTERED (CauseCategoryId)
);

INSERT INTO dbo.FAULT_CAUSE_CATEGORY (CauseCategoryId, Name)
VALUES (1, N'기계'),
       (2, N'재료'),
       (3, N'계측'),
       (4, N'전기'),
       (5, N'외부 영향'),
       (6, N'기타');

ALTER TABLE dbo.FAULT WITH CHECK
    ADD CONSTRAINT FK_FAULT_CauseCategory
    FOREIGN KEY (CauseCategory)
    REFERENCES dbo.FAULT_CAUSE_CATEGORY (CauseCategoryId);
