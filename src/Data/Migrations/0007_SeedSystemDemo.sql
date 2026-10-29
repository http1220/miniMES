INSERT INTO dbo.EQUIPMENT (EquipmentId, Name)
VALUES (1, N'설비 1'), (2, N'설비 2'), (3, N'설비 3'),
       (4, N'설비 4'), (5, N'설비 5');

DECLARE @System1Id INT;
DECLARE @System2Id INT;
DECLARE @RootNodeId INT;
DECLARE @ParallelNodeId INT;
DECLARE @SerialNodeId INT;

INSERT INTO dbo.[SYSTEM] (Name) VALUES (N'시스템 1');
SET @System1Id = SCOPE_IDENTITY();

INSERT INTO dbo.[SYSTEM] (Name) VALUES (N'시스템 2');
SET @System2Id = SCOPE_IDENTITY();

-- System 1: serial root.
INSERT INTO dbo.SYSTEM_NODE (SystemId, ParentNodeId, NodeType, EquipmentId, SortOrder)
VALUES (@System1Id, NULL, 2, NULL, 0);
SET @RootNodeId = SCOPE_IDENTITY();

INSERT INTO dbo.SYSTEM_NODE (SystemId, ParentNodeId, NodeType, EquipmentId, SortOrder)
VALUES (@System1Id, @RootNodeId, 1, 1, 0);

INSERT INTO dbo.SYSTEM_NODE (SystemId, ParentNodeId, NodeType, EquipmentId, SortOrder)
VALUES (@System1Id, @RootNodeId, 3, NULL, 1);
SET @ParallelNodeId = SCOPE_IDENTITY();

INSERT INTO dbo.SYSTEM_NODE (SystemId, ParentNodeId, NodeType, EquipmentId, SortOrder)
VALUES (@System1Id, @ParallelNodeId, 2, NULL, 0);
SET @SerialNodeId = SCOPE_IDENTITY();

INSERT INTO dbo.SYSTEM_NODE (SystemId, ParentNodeId, NodeType, EquipmentId, SortOrder)
VALUES (@System1Id, @SerialNodeId, 1, 2, 0);

INSERT INTO dbo.SYSTEM_NODE (SystemId, ParentNodeId, NodeType, EquipmentId, SortOrder)
VALUES (@System1Id, @SerialNodeId, 1, 3, 1);

INSERT INTO dbo.SYSTEM_NODE (SystemId, ParentNodeId, NodeType, EquipmentId, SortOrder)
VALUES (@System1Id, @ParallelNodeId, 1, 4, 1);

-- System 2: equipment 5 is itself the root.
INSERT INTO dbo.SYSTEM_NODE (SystemId, ParentNodeId, NodeType, EquipmentId, SortOrder)
VALUES (@System2Id, NULL, 1, 5, 0);
