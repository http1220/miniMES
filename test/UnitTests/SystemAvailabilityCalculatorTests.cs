using System;
using System.Collections.Generic;
using System.Linq;
using Core;
using Xunit;
using static Core.SystemNodeType;

namespace UnitTests
{
    public class SystemAvailabilityCalculatorTests
    {
        // 직렬 두 설비는 0.9 × 0.8 = 0.72다.
        [Fact]
        public void TwoEquipmentInSeriesMultiplyAvailabilities()
        {
            Assert.Equal(0.72, Calculate(TwoChildren(Series), Values(0.9, 0.8)).Value, 12);
        }

        // 병렬 두 설비는 1 - (0.1 × 0.2) = 0.98이다.
        [Fact]
        public void TwoEquipmentInParallelMultiplyUnavailabilities()
        {
            Assert.Equal(0.98, Calculate(TwoChildren(Parallel), Values(0.9, 0.8)).Value, 12);
        }

        // 시드 구조에서 내부 직렬 0.8 × 0.9 = 0.72, 병렬 1 - 0.28 × 0.5 = 0.86, 최상위 0.9 × 0.86 = 0.774다.
        [Fact]
        public void SeedDemoStructureCombinesNestedSeriesAndParallel()
        {
            var nodes = new[] { Node(1, null, Series), Node(2, 1, Equipment, 1),
                Node(3, 1, Parallel, sort: 1), Node(4, 3, Series),
                Node(5, 4, Equipment, 2), Node(6, 4, Equipment, 3, sort: 1),
                Node(7, 3, Equipment, 4, sort: 1), Node(8, null, Equipment, 5, system: 2) };
            var values = Values(0.9, 0.8, 0.9, 0.5, 0.61);

            Assert.Equal(0.774, Calculate(nodes, values).Value, 12);
            Assert.Equal(0.61, SystemAvailabilityCalculator.Calculate(2, nodes, values).Value);
        }

        // 그룹이 없으면 설비 1의 0.73을 그대로 사용한다.
        [Fact]
        public void SingleEquipmentReturnsItsMetricUnchanged()
        {
            var values = Values(0.73);
            var nodes = new[] { Node(1, null, Equipment, 1) };

            Assert.Same(values[1], Calculate(nodes, values));
            var unavailable = MetricResult<double>.Unavailable(MetricUnavailableReason.ZeroObservedTime,
                "zero_observed_time");
            values[1] = unavailable;
            Assert.Same(unavailable, Calculate(nodes, values));
        }

        // 설비 2의 관측 시간이 0이면 0.8인 설비 1이 있어도 직렬·병렬 모두 산출 불가다.
        [Theory]
        [InlineData(Series)]
        [InlineData(Parallel)]
        public void UnavailableChildMarksGroupUnavailable(SystemNodeType group)
        {
            var values = Values(0.8);
            values[2] = MetricResult<double>.Unavailable(MetricUnavailableReason.ZeroObservedTime,
                "zero_observed_time");

            AssertUnavailableEquipment(2, Calculate(TwoChildren(group), values));
        }

        // 루트에서 순서 0인 병렬 그룹을 먼저 본다. 그 안에서는 순서 동률인 노드 4·5 중 노드 4의 설비 3이 원인이다.
        [Fact]
        public void NestedUnavailableUsesFirstCauseBySortOrderThenNodeId()
        {
            var nodes = new[] { Node(1, null, Series), Node(2, 1, Equipment, 1, sort: 1),
                Node(3, 1, Parallel), Node(5, 3, Equipment, 2), Node(4, 3, Equipment, 3),
                Node(6, 3, Equipment, 4, sort: 1) };
            var values = new Dictionary<int, MetricResult<double>>
            {
                [3] = MetricResult<double>.Unavailable(MetricUnavailableReason.ZeroObservedTime,
                    "zero_observed_time"),
                [4] = MetricResult<double>.Of(1.0)
            };

            AssertUnavailableEquipment(3, Calculate(nodes, values));
        }

        // 설비 1만 0.8로 제공하면 사전에 없는 설비 2 때문에 직렬 전체가 산출 불가다.
        [Fact]
        public void MissingEquipmentReportsItsId()
        {
            AssertUnavailableEquipment(2, Calculate(TwoChildren(Series), Values(0.8)));
        }

        // 10분 내내 관측한 각 설비의 가용도는 (10-0)/10=1이므로 두 설비 직렬도 1이다.
        [Fact]
        public void EquipmentWithoutFaultsComposesToOne()
        {
            var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var duration = TimeSpan.FromMinutes(10);
            var availability = ReliabilityCalculator.Calculate(from, from + duration, duration,
                new[] { new ReliabilitySample(from, 1) }, new ReliabilityFault[0]).Availability;
            var values = new Dictionary<int, MetricResult<double>> { [1] = availability, [2] = availability };

            Assert.Equal(1.0, Calculate(TwoChildren(Series), values).Value);
        }

        // 이 시스템에 속한 노드가 0개라 루트도 0개다.
        [Fact]
        public void EmptySystemIsStructureError()
        {
            AssertStructureError("no_nodes system=1", Calculate(new[] { Node(1, null, Equipment, 1, system: 2) }));
        }

        // 부모 없는 설비 노드가 2개이므로 루트가 정확히 1개여야 한다는 조건을 위반한다.
        [Fact]
        public void MultipleRootsAreStructureError()
        {
            AssertStructureError("multiple_roots node=", Calculate(new[] { Node(1, null, Equipment, 1),
                Node(2, null, Equipment, 2) }));
        }

        // 두 직렬 노드만 서로를 부모로 지정하면 루트가 0개여서 즉시 산출을 중단한다.
        [Fact]
        public void RootlessCycleTerminatesWithStructureError()
        {
            AssertStructureError("no_root node=", Calculate(new[] { Node(1, 2, Series), Node(2, 1, Series) }));
        }

        // 루트 설비 1은 유효하지만 별도 성분 2 ↔ 3이 순환한다. 순환 노드 ID는 2 또는 3이다.
        [Fact]
        public void DisconnectedCycleIsStructureErrorEvenWithValidRoot()
        {
            var result = Calculate(new[] { Node(1, null, Equipment, 1), Node(4, 2, Equipment, 2),
                Node(2, 3, Series), Node(3, 2, Parallel) }, Values(0.8, 0.9));

            AssertStructureError("cycle node=", result);
            Assert.Contains(result.Detail, new[] { "cycle node=2", "cycle node=3" });
        }

        // 직렬 그룹의 두 잎이 모두 설비 1을 가리키면 설비 1이 2번 등장한다.
        [Fact]
        public void DuplicateEquipmentIsStructureError()
        {
            AssertStructureError("duplicate_equipment node=", Calculate(new[] { Node(1, null, Series),
                Node(2, 1, Equipment, 1), Node(3, 1, Equipment, 1) }));
        }

        // 직렬 또는 병렬 그룹의 자식이 0개면 유효한 설비 조합을 만들 수 없다.
        [Theory]
        [InlineData(Series)]
        [InlineData(Parallel)]
        public void EmptyGroupIsStructureError(SystemNodeType group)
        {
            AssertStructureError("group_has_no_children node=1", Calculate(new[] { Node(1, null, group) }));
        }

        // 시스템 2의 부모에 시스템 1의 자식이 연결된다. 어느 쪽에서 계산해도 위반 간선은 노드 2다.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void CrossSystemChildIsStructureError(int systemId)
        {
            var nodes = new[] { Node(1, null, Equipment, 1), Node(2, 3, Equipment, 2),
                Node(3, null, Series, system: 2), Node(4, 3, Equipment, 3, system: 2) };

            AssertStructureError("cross_system_parent node=2",
                SystemAvailabilityCalculator.Calculate(systemId, nodes, Values(0.8, 0.9, 1.0)));
        }

        // 설비 노드는 설비 ID 1개가 필요하고 직렬·병렬 노드는 설비 ID 0개여야 한다.
        [Theory]
        [InlineData(Equipment, null)]
        [InlineData(Series, 1)]
        [InlineData(Parallel, 1)]
        public void EquipmentIdMustMatchNodeType(SystemNodeType type, int? equipmentId)
        {
            AssertStructureError("invalid_equipment_id node=1", Calculate(new[] { Node(1, null, type, equipmentId) }));
        }

        // 설비 노드 1은 잎이어야 하지만 자식 2가 하나 붙어 있어 산출 불가다.
        [Fact]
        public void EquipmentWithChildrenIsStructureError()
        {
            AssertStructureError("equipment_has_children node=1", Calculate(new[] { Node(1, null, Equipment, 1),
                Node(2, 1, Equipment, 2) }));
        }

        // 참조한 부모 99가 목록에 없으므로 노드 2의 간선을 구성할 수 없다.
        [Fact]
        public void MissingParentIsStructureError()
        {
            AssertStructureError("missing_parent node=2", Calculate(new[] { Node(1, null, Equipment, 1),
                Node(2, 99, Equipment, 2) }));
        }

        // 별도 시스템의 빈 병렬 그룹은 산출 불가이고, 정상 시스템은 설비 1의 0.8을 유지한다.
        [Fact]
        public void UnrelatedMalformedSystemDoesNotAffectValidSystem()
        {
            var nodes = new[] { Node(1, null, Equipment, 1), Node(2, null, Parallel, system: 2) };
            var values = Values(0.8);

            Assert.Equal(0.8, Calculate(nodes, values).Value);
            AssertStructureError("group_has_no_children node=2",
                SystemAvailabilityCalculator.Calculate(2, nodes, values));
        }

        // 단일 자식 직렬 그룹은 자식 값을 그대로 전달하므로 10000개를 중첩해도 잎 값 0.75다.
        [Fact]
        public void DeepValidTreeCanBeEvaluated()
        {
            const int depth = 10000;
            var nodes = Enumerable.Range(1, depth)
                .Select(id => Node(id, id == 1 ? (int?)null : id - 1, Series))
                .Concat(new[] { Node(depth + 1, depth, Equipment, 1) }).ToArray();

            Assert.Equal(0.75, Calculate(nodes, Values(0.75)).Value);
        }

        private static void AssertStructureError(string detailPrefix, MetricResult<double> result)
        {
            Assert.False(result.HasValue);
            Assert.Equal(MetricUnavailableReason.StructureError, result.Reason);
            Assert.StartsWith(detailPrefix, result.Detail);
        }

        private static void AssertUnavailableEquipment(int equipment, MetricResult<double> result)
        {
            Assert.False(result.HasValue);
            Assert.Equal(MetricUnavailableReason.ChildUnavailable, result.Reason);
            Assert.Equal("equipment=" + equipment, result.Detail);
        }

        private static MetricResult<double> Calculate(SystemNodeInput[] nodes,
            Dictionary<int, MetricResult<double>> values = null)
        {
            return SystemAvailabilityCalculator.Calculate(1, nodes, values ?? Values());
        }

        private static Dictionary<int, MetricResult<double>> Values(params double[] values)
        {
            return values.Select((value, index) => new { Id = index + 1, Metric = MetricResult<double>.Of(value) })
                .ToDictionary(entry => entry.Id, entry => entry.Metric);
        }

        private static SystemNodeInput[] TwoChildren(SystemNodeType group)
        {
            return new[] { Node(1, null, group), Node(2, 1, Equipment, 1), Node(3, 1, Equipment, 2, sort: 1) };
        }

        private static SystemNodeInput Node(int id, int? parent, SystemNodeType type,
            int? equipment = null, int sort = 0, int system = 1)
        {
            return new SystemNodeInput(id, system, parent, type, equipment, sort);
        }
    }
}
