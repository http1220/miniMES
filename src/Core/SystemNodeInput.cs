namespace Core
{
    public enum SystemNodeType
    {
        Equipment = 1,
        Series = 2,
        Parallel = 3
    }

    public sealed class SystemNodeInput
    {
        public int NodeId { get; }
        public int SystemId { get; }
        public int? ParentNodeId { get; }
        public SystemNodeType NodeType { get; }
        public int? EquipmentId { get; }
        public int SortOrder { get; }

        public SystemNodeInput(int nodeId, int systemId, int? parentNodeId, SystemNodeType nodeType,
            int? equipmentId, int sortOrder)
        {
            NodeId = nodeId;
            SystemId = systemId;
            ParentNodeId = parentNodeId;
            NodeType = nodeType;
            EquipmentId = equipmentId;
            SortOrder = sortOrder;
        }
    }
}
