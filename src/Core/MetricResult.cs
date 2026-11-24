using System;

namespace Core
{
    public enum MetricUnavailableReason
    {
        NoFaults,
        NoClosedFaults,
        ZeroObservedTime,
        StructureError,
        ChildUnavailable
    }

    public sealed class MetricResult<T>
    {
        private readonly T value;

        public bool HasValue => !Reason.HasValue;
        public T Value => HasValue ? value : throw new InvalidOperationException("Metric is unavailable.");
        public MetricUnavailableReason? Reason { get; }
        public string Detail { get; }

        private MetricResult(T value, MetricUnavailableReason? reason, string detail)
        {
            this.value = value;
            Reason = reason;
            Detail = detail;
        }

        public static MetricResult<T> Of(T value)
        {
            return new MetricResult<T>(value, null, null);
        }

        public static MetricResult<T> Unavailable(MetricUnavailableReason reason, string detail)
        {
            return new MetricResult<T>(default(T), reason, detail);
        }
    }
}
