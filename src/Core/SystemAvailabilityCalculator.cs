using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Core
{
    public static class SystemAvailabilityCalculator
    {
        // Available equipment inputs are fractions in [0, 1].
        public static MetricResult<double> Calculate(int systemId, IReadOnlyList<SystemNodeInput> allNodes,
            IReadOnlyDictionary<int, MetricResult<double>> equipmentAvailability)
        {
            if (allNodes == null)
                throw new ArgumentNullException(nameof(allNodes));
            if (equipmentAvailability == null)
                throw new ArgumentNullException(nameof(equipmentAvailability));

            var nodes = new Dictionary<int, SystemNodeInput>();
            foreach (var node in allNodes)
            {
                if (node.SystemId != systemId)
                    continue;
                if (nodes.ContainsKey(node.NodeId))
                    return StructureError("duplicate_node", node.NodeId);

                nodes.Add(node.NodeId, node);
            }

            if (nodes.Count == 0)
                return MetricResult<double>.Unavailable(MetricUnavailableReason.StructureError,
                    "no_nodes system=" + systemId.ToString(CultureInfo.InvariantCulture));

            var foreignIds = new HashSet<int>();
            foreach (var node in allNodes)
            {
                if (node.SystemId == systemId)
                    continue;
                if (node.ParentNodeId.HasValue && nodes.ContainsKey(node.ParentNodeId.Value))
                    return StructureError("cross_system_parent", node.NodeId);

                foreignIds.Add(node.NodeId);
            }

            SystemNodeInput root = null;
            var equipmentIds = new HashSet<int>();
            var children = new Dictionary<int, List<SystemNodeInput>>();
            foreach (var node in nodes.Values)
            {
                bool isEquipment = node.NodeType == SystemNodeType.Equipment;
                if (!isEquipment && node.NodeType != SystemNodeType.Series && node.NodeType != SystemNodeType.Parallel)
                    return StructureError("invalid_node_type", node.NodeId);
                if (isEquipment != node.EquipmentId.HasValue)
                    return StructureError("invalid_equipment_id", node.NodeId);
                if (isEquipment && !equipmentIds.Add(node.EquipmentId.Value))
                    return StructureError("duplicate_equipment", node.NodeId);

                if (!node.ParentNodeId.HasValue)
                {
                    if (root != null)
                        return StructureError("multiple_roots", node.NodeId);

                    root = node;
                    continue;
                }

                int parentId = node.ParentNodeId.Value;
                if (!nodes.ContainsKey(parentId))
                    return StructureError(foreignIds.Contains(parentId) ? "cross_system_parent" : "missing_parent",
                        node.NodeId);

                if (!children.TryGetValue(parentId, out var siblings))
                {
                    siblings = new List<SystemNodeInput>();
                    children.Add(parentId, siblings);
                }

                siblings.Add(node);
            }

            if (root == null)
                return StructureError("no_root", nodes.Values.First().NodeId);

            foreach (var node in nodes.Values)
            {
                bool hasChildren = children.ContainsKey(node.NodeId);
                if (hasChildren && node.NodeType == SystemNodeType.Equipment)
                    return StructureError("equipment_has_children", node.NodeId);
                if (!hasChildren && node.NodeType != SystemNodeType.Equipment)
                    return StructureError("group_has_no_children", node.NodeId);
            }

            var visited = new HashSet<int>();
            var pending = new Stack<SystemNodeInput>();
            var bottomUp = new Stack<SystemNodeInput>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (!visited.Add(node.NodeId))
                    return StructureError("cycle", node.NodeId);

                bottomUp.Push(node);
                if (children.TryGetValue(node.NodeId, out var group))
                {
                    group.Sort(CompareChildren);
                    foreach (var child in group)
                        pending.Push(child);
                }
            }

            // With one root and one existing parent per other node, a disconnected
            // component must contain a cycle.
            if (visited.Count != nodes.Count)
            {
                int nodeId = nodes.Keys.First(id => !visited.Contains(id));
                while (visited.Add(nodeId))
                    nodeId = nodes[nodeId].ParentNodeId.Value;

                return StructureError("cycle", nodeId);
            }

            return Evaluate(root.NodeId, bottomUp, children, equipmentAvailability);
        }

        private static int CompareChildren(SystemNodeInput left, SystemNodeInput right)
        {
            int order = left.SortOrder.CompareTo(right.SortOrder);
            return order != 0 ? order : left.NodeId.CompareTo(right.NodeId);
        }

        private static MetricResult<double> Evaluate(int rootId, Stack<SystemNodeInput> bottomUp,
            Dictionary<int, List<SystemNodeInput>> children,
            IReadOnlyDictionary<int, MetricResult<double>> equipmentAvailability)
        {
            var results = new Dictionary<int, MetricResult<double>>();
            while (bottomUp.Count > 0)
            {
                var node = bottomUp.Pop();
                if (node.NodeType == SystemNodeType.Equipment)
                {
                    int equipmentId = node.EquipmentId.Value;
                    results.Add(node.NodeId, equipmentAvailability.TryGetValue(equipmentId, out var metric)
                        ? metric
                        : ChildUnavailable(EquipmentDetail(equipmentId)));
                    continue;
                }

                results.Add(node.NodeId, Combine(node.NodeType, children[node.NodeId], results));
            }

            return results[rootId];
        }

        private static MetricResult<double> Combine(SystemNodeType type, List<SystemNodeInput> children,
            Dictionary<int, MetricResult<double>> results)
        {
            bool parallel = type == SystemNodeType.Parallel;
            double product = 1;
            foreach (var child in children)
            {
                var metric = results[child.NodeId];
                if (!metric.HasValue)
                {
                    if (child.NodeType != SystemNodeType.Equipment ||
                        (metric.Reason == MetricUnavailableReason.ChildUnavailable && !string.IsNullOrEmpty(metric.Detail)))
                        return ChildUnavailable(metric.Detail);

                    return ChildUnavailable(EquipmentDetail(child.EquipmentId.Value));
                }

                product *= parallel ? 1 - metric.Value : metric.Value;
            }

            return MetricResult<double>.Of(parallel ? 1 - product : product);
        }

        private static string EquipmentDetail(int id)
        {
            return "equipment=" + id.ToString(CultureInfo.InvariantCulture);
        }

        private static MetricResult<double> ChildUnavailable(string detail)
        {
            return MetricResult<double>.Unavailable(MetricUnavailableReason.ChildUnavailable, detail);
        }

        private static MetricResult<double> StructureError(string rule, int nodeId)
        {
            return MetricResult<double>.Unavailable(MetricUnavailableReason.StructureError,
                rule + " node=" + nodeId.ToString(CultureInfo.InvariantCulture));
        }
    }
}
