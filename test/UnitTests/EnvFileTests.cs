using Collector;
using Xunit;

namespace UnitTests
{
    public class EnvFileTests
    {
        [Fact]
        public void Parse_IgnoresBlankCommentAndUnassignedLines()
        {
            var values = EnvFile.Parse(new[] { "", "  ", "# KEY=ignored", "  # OTHER=ignored", "no separator" });

            Assert.Empty(values);
        }

        [Fact]
        public void Parse_TrimsKeyAndValueAndSplitsAtFirstEquals()
        {
            var values = EnvFile.Parse(new[] { "  KEY  =  left=right  " });

            Assert.Equal("left=right", values["KEY"]);
            Assert.Single(values);
        }

        [Fact]
        public void Parse_LastDuplicateWinsAndKeysAreCaseSensitive()
        {
            var values = EnvFile.Parse(new[] { "KEY=first", "key=lowercase", " KEY =last" });

            Assert.Equal(2, values.Count);
            Assert.Equal("last", values["KEY"]);
            Assert.Equal("lowercase", values["key"]);
        }
    }
}
