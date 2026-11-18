using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using Server;
using Xunit;

namespace UnitTests
{
    public sealed class ServerSettingsConnectionTests
    {
        private const string Connection = "Data Source=example-host;Initial Catalog=example;Integrated Security=true";

        private static Dictionary<string, string> Values(string connection)
        {
            return new Dictionary<string, string>
            {
                ["SERVER_URL"] = "https://+:5443/",
                ["DB_CONNECTION_STRING"] = connection,
                ["COLLECTOR_API_KEY"] = "test-collector-key-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                ["DASHBOARD_API_KEY"] = "test-dashboard-key-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            };
        }

        private static void AssertOriginalProperties(SqlConnectionStringBuilder actual)
        {
            var original = new SqlConnectionStringBuilder(Connection);
            Assert.Equal(PoolBlockingPeriod.NeverBlock, actual.PoolBlockingPeriod);
            Assert.Equal(original.DataSource, actual.DataSource);
            Assert.Equal(original.InitialCatalog, actual.InitialCatalog);
            Assert.Equal(original.IntegratedSecurity, actual.IntegratedSecurity);
            Assert.Equal(original.Pooling, actual.Pooling);
        }

        [Fact]
        public void ConnectionWithoutBlockingPeriod_PreservesOtherProperties()
        {
            ServerSettings settings;
            Assert.Empty(ServerSettings.Validate(Values(Connection), out settings));
            AssertOriginalProperties(new SqlConnectionStringBuilder(settings.ConnectionString));
        }

        [Fact]
        public void ConnectionWithAlwaysBlock_UsesNeverBlock()
        {
            ServerSettings settings;
            Assert.Empty(ServerSettings.Validate(Values(Connection + ";Pool Blocking Period=AlwaysBlock"), out settings));
            Assert.Equal(PoolBlockingPeriod.NeverBlock,
                new SqlConnectionStringBuilder(settings.ConnectionString).PoolBlockingPeriod);
        }

        [Fact]
        public void ConnectionWithPoolingDisabled_KeepsPoolingDisabled()
        {
            ServerSettings settings;
            Assert.Empty(ServerSettings.Validate(Values(Connection + ";Pooling=false"), out settings));
            var builder = new SqlConnectionStringBuilder(settings.ConnectionString);
            Assert.False(builder.Pooling);
            Assert.Equal(PoolBlockingPeriod.NeverBlock, builder.PoolBlockingPeriod);
        }

        [Fact]
        public void MigrationOnlyConnection_PreservesOtherProperties()
        {
            ServerSettings settings;
            Assert.Empty(ServerSettings.Validate(Values(Connection), out settings, migrationOnly: true));
            AssertOriginalProperties(new SqlConnectionStringBuilder(settings.ConnectionString));
        }

        [Fact]
        public void InvalidConnection_ReturnsExistingError()
        {
            ServerSettings settings;
            var errors = ServerSettings.Validate(Values("invalid-connection-fragment"), out settings);
            Assert.Equal("DB_CONNECTION_STRING must be a valid SQL connection string.", Assert.Single(errors));
        }
    }
}
