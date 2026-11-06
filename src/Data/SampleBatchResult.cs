namespace Data
{
    public sealed class SampleBatchResult
    {
        internal SampleBatchResult(int storedCount, int duplicateCount)
        {
            StoredCount = storedCount;
            DuplicateCount = duplicateCount;
        }

        public int StoredCount { get; }
        public int DuplicateCount { get; }
    }
}
