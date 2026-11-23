using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Collector;
using Core;
using Xunit;

namespace UnitTests
{
    public class CollectorSettingsTests
    {
        private const string TestServerUrl = "https://change-me:5443";
        private const string TestApiKey = "change-me-change-me-change-me-01";

        [Fact]
        public void Validate_DefaultConfiguration_ConvertsSettings()
        {
            var values = new Dictionary<string, string>
            {
                ["PollIntervalMilliseconds"] = "1000",
                ["ReadTimeoutMilliseconds"] = "500",
                ["JudgeIntervalMilliseconds"] = "1000",
                ["CommunicationFaultMilliseconds"] = "5000",
                ["CommunicationRecoverySuccessCount"] = "3",
                ["ThresholdConsecutiveCount"] = "3",
                ["BackoffMaximumMilliseconds"] = "10000",
                ["BackoffMaximumJitterFraction"] = "0.2",
                ["SampleBufferCapacity"] = "3000",
                ["AlarmQueueCapacity"] = "3000",
                ["LogDirectory"] = "",
                ["SERVER_BASE_URL"] = TestServerUrl,
                ["COLLECTOR_API_KEY"] = TestApiKey
            };
            for (var id = 1; id <= 5; id++)
            {
                var prefix = "Equipment." + id + ".";
                values[prefix + "Host"] = "127.0.0.1";
                values[prefix + "Port"] = (15020 + id).ToString(CultureInfo.InvariantCulture);
                values[prefix + "UnitId"] = "1";
                values[prefix + "StartAddress"] = ((id - 1) * 100).ToString(CultureInfo.InvariantCulture);
                values[prefix + "WordOrder"] = id % 2 == 1 ? "ABCD" : "CDAB";
                values[prefix + "TemperatureUpperLimit"] = "80";
                values[prefix + "VibrationUpperLimit"] = "10";
                values[prefix + "TemperatureMinimum"] = "-40";
                values[prefix + "TemperatureMaximum"] = "200";
                values[prefix + "VibrationMinimum"] = "0";
                values[prefix + "VibrationMaximum"] = "100";
            }

            Assert.Empty(CollectorSettings.Validate(values, out var settings));
            Assert.Equal(TimeSpan.FromMilliseconds(500), settings.ReadTimeout);
            Assert.Equal(TimeSpan.FromSeconds(5), settings.CommunicationFault);
            Assert.Equal(0.2, settings.BackoffMaximumJitterFraction);
            Assert.Equal(3000, settings.AlarmQueueCapacity);
            Assert.Equal(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"), settings.LogDirectory);
            Assert.Equal(5, settings.Equipment.Length);
            Assert.Equal(15025, settings.Equipment[4].Port);
            Assert.Equal((ushort)100, settings.Equipment[1].StartAddress);
            Assert.Equal(RegisterWordOrder.CDAB, settings.Equipment[1].WordOrder);
            Assert.Equal(-40f, settings.Equipment[0].TemperatureMinimum);
        }

        [Theory]
        [InlineData("PollIntervalMilliseconds", " 1000", "PollIntervalMilliseconds")]
        [InlineData("ReadTimeoutMilliseconds", null, "ReadTimeoutMilliseconds")]
        [InlineData("Equipment.1.TemperatureUpperLimit", "80,0", "Equipment.1.TemperatureUpperLimit")]
        [InlineData("Equipment.1.VibrationMaximum", "Infinity", "Equipment.1.VibrationMaximum")]
        [InlineData("Equipment.1.TemperatureMaximum", "200 ", "Equipment.1.TemperatureMaximum")]
        [InlineData("Equipment.1.WordOrder", "abcd", "Equipment.1.WordOrder")]
        [InlineData("Equipment.1.WordOrder", "0", "Equipment.1.WordOrder")]
        [InlineData("Equipment.1.UnitId", "-1", "Equipment.1.UnitId")]
        [InlineData("Equipment.1.UnitId", "256", "Equipment.1.UnitId")]
        [InlineData("CommunicationRecoverySuccessCount", "0", "CommunicationRecoverySuccessCount")]
        [InlineData("ThresholdConsecutiveCount", "0", "ThresholdConsecutiveCount")]
        [InlineData("ReadTimeoutMilliseconds", "1000", "ReadTimeoutMilliseconds")]
        [InlineData("CommunicationFaultMilliseconds", "1000", "CommunicationFaultMilliseconds")]
        [InlineData("BackoffMaximumMilliseconds", "14001", "BackoffMaximumMilliseconds")]
        [InlineData("BackoffMaximumJitterFraction", "0.4001", "BackoffMaximumJitterFraction")]
        [InlineData("Equipment.1.TemperatureMinimum", "200", "Equipment.1.TemperatureMinimum")]
        [InlineData("Equipment.1.VibrationMaximum", "0", "Equipment.1.VibrationMaximum")]
        [InlineData("Equipment.1.TemperatureUpperLimit", "-41", "Equipment.1.TemperatureUpperLimit")]
        [InlineData("Equipment.1.VibrationUpperLimit", "101", "Equipment.1.VibrationUpperLimit")]
        [InlineData("SampleBufferCapacity", "0", "SampleBufferCapacity")]
        [InlineData("AlarmQueueCapacity", "0", "AlarmQueueCapacity")]
        [InlineData("BackoffMaximumJitterFraction", "-0.01", "BackoffMaximumJitterFraction")]
        [InlineData("BackoffMaximumJitterFraction", "NaN", "BackoffMaximumJitterFraction")]
        [InlineData("BackoffMaximumJitterFraction", "Infinity", "BackoffMaximumJitterFraction")]
        [InlineData("Equipment.1.StartAddress", "-1", "Equipment.1.StartAddress")]
        [InlineData("Equipment.1.StartAddress", "65532", "Equipment.1.StartAddress")]
        [InlineData("Equipment.1.Port", "0", "Equipment.1.Port")]
        [InlineData("Equipment.1.Port", "65536", "Equipment.1.Port")]
        [InlineData("Equipment.2.Port", "15021", "Equipment.2.Port")]
        [InlineData("Equipment.1.Host", " ", "Equipment.1.Host")]
        [InlineData("BackoffMaximumMilliseconds", "999", "BackoffMaximumMilliseconds")]
        [InlineData("JudgeIntervalMilliseconds", "0", "JudgeIntervalMilliseconds")]
        public void Validate_SingleViolation_ReturnsKey(string key, string value, string expectedKey)
        {
            var values = ValidServerSettings();
            values[key] = value;
            if (key == "BackoffMaximumMilliseconds" && value == "14001")
                values["BackoffMaximumJitterFraction"] = "0";

            var errors = CollectorSettings.Validate(values, out var settings);
            Assert.Null(settings);
            Assert.Contains(expectedKey, Assert.Single(errors));
        }

        [Theory]
        [InlineData("ReadTimeoutMilliseconds", "999")]
        [InlineData("CommunicationFaultMilliseconds", "1001")]
        [InlineData("BackoffMaximumJitterFraction", "0.4")]
        [InlineData("BackoffMaximumJitterFraction", "0")]
        [InlineData("BackoffMaximumMilliseconds", "1000")]
        [InlineData("CommunicationRecoverySuccessCount", "1")]
        [InlineData("ThresholdConsecutiveCount", "1")]
        [InlineData("SampleBufferCapacity", "1")]
        [InlineData("AlarmQueueCapacity", "1")]
        [InlineData("JudgeIntervalMilliseconds", "1")]
        [InlineData("Equipment.1.Port", "1")]
        [InlineData("Equipment.1.Port", "65535")]
        [InlineData("Equipment.1.UnitId", "0")]
        [InlineData("Equipment.1.UnitId", "255")]
        [InlineData("Equipment.1.StartAddress", "0")]
        [InlineData("Equipment.1.StartAddress", "65531")]
        [InlineData("Equipment.1.TemperatureUpperLimit", "-40")]
        [InlineData("Equipment.1.TemperatureUpperLimit", "200")]
        [InlineData("Equipment.1.VibrationUpperLimit", "0")]
        [InlineData("Equipment.1.VibrationUpperLimit", "100")]
        public void Validate_InclusiveBoundary_Accepts(string key, string value)
        {
            var values = ValidServerSettings();
            values[key] = value;

            Assert.Empty(CollectorSettings.Validate(values, out var settings));
            Assert.NotNull(settings);
        }

        [Fact]
        public void Validate_IndependentViolations_ReportsAll()
        {
            var errors = CollectorSettings.Validate(new Dictionary<string, string>
            {
                ["SampleBufferCapacity"] = "0",
                ["JudgeIntervalMilliseconds"] = "-1",
                ["Equipment.2.WordOrder"] = "BADC",
                ["Equipment.4.Port"] = "65536",
                ["SERVER_BASE_URL"] = TestServerUrl,
                ["COLLECTOR_API_KEY"] = TestApiKey
            }, out var settings);

            Assert.Equal(4, errors.Count);
            Assert.Null(settings);
        }

        [Fact]
        public void Validate_ShortIntervalsAndSamePortOnDifferentHosts_Accepts()
        {
            var values = new Dictionary<string, string>
            {
                ["PollIntervalMilliseconds"] = "20",
                ["ReadTimeoutMilliseconds"] = "10",
                ["CommunicationFaultMilliseconds"] = "100",
                ["BackoffMaximumMilliseconds"] = "1000",
                ["Equipment.2.Port"] = "15021",
                ["Equipment.2.Host"] = "localhost",
                ["SERVER_BASE_URL"] = TestServerUrl,
                ["COLLECTOR_API_KEY"] = TestApiKey
            };
            Assert.Empty(CollectorSettings.Validate(values, out var settings));
            Assert.Equal(TimeSpan.FromMilliseconds(20), settings.PollInterval);
            Assert.Equal("localhost", settings.Equipment[1].Host);
        }

        [Fact]
        public void Validate_NewDefaults_ConvertsTimeoutsAndServerSettings()
        {
            Assert.Equal(32, TestApiKey.Length);
            Assert.Empty(CollectorSettings.Validate(ValidServerSettings(), out var settings));

            Assert.Equal(TimeSpan.FromMilliseconds(2000), settings.HeartbeatInterval);
            Assert.Equal(TimeSpan.FromSeconds(10), settings.ServerStoppedTimeout);
            Assert.Equal(TimeSpan.FromMilliseconds(5000), settings.HttpRequestTimeout);
            Assert.Equal(TimeSpan.FromMilliseconds(10000), settings.ShutdownTimeout);
            Assert.Equal(TimeSpan.FromMilliseconds(1000), settings.ClockJumpThreshold);
            Assert.Equal(new Uri(TestServerUrl), settings.ServerBaseUrl);
            Assert.Equal(TestApiKey, settings.ApiKey);
            Assert.DoesNotContain(TestApiKey, settings.ToString());
        }

        [Theory]
        [InlineData((string)null)]
        [InlineData(" ")]
        [InlineData("http://change-me:5443")]
        [InlineData("/relative")]
        [InlineData("https://:5443")]
        public void Validate_InvalidServerBaseUrl_ReturnsKeyWithoutValue(string url)
        {
            var values = ValidServerSettings();
            values.Remove("SERVER_BASE_URL");
            if (url != null)
                values["SERVER_BASE_URL"] = url;

            var errors = CollectorSettings.Validate(values, out var settings);

            Assert.Null(settings);
            var error = Assert.Single(errors);
            Assert.Contains("SERVER_BASE_URL", error);
            if (!string.IsNullOrWhiteSpace(url))
                Assert.DoesNotContain(url, error);
        }

        [Theory]
        [InlineData((string)null)]
        [InlineData(" ")]
        [InlineData("change-me-change-me-change-me-0")]
        public void Validate_MissingBlankOrShortApiKey_ReturnsKey(string apiKey)
        {
            var values = ValidServerSettings();
            values.Remove("COLLECTOR_API_KEY");
            if (apiKey != null)
                values["COLLECTOR_API_KEY"] = apiKey;

            var errors = CollectorSettings.Validate(values, out var settings);

            Assert.Null(settings);
            var error = Assert.Single(errors);
            Assert.Contains("COLLECTOR_API_KEY", error);
            if (!string.IsNullOrWhiteSpace(apiKey))
                Assert.DoesNotContain(apiKey, error);
        }

        [Theory]
        [InlineData("HeartbeatIntervalMilliseconds")]
        [InlineData("ServerStoppedSeconds")]
        [InlineData("HttpRequestTimeoutMilliseconds")]
        [InlineData("ShutdownTimeoutMilliseconds")]
        [InlineData("ClockJumpThresholdMilliseconds")]
        public void Validate_NewNumericSettings_RejectZeroAndAcceptOne(string key)
        {
            var values = ValidServerSettings();
            values["HeartbeatIntervalMilliseconds"] = "1";
            values[key] = "0";

            Assert.Contains(key, Assert.Single(CollectorSettings.Validate(values, out var invalid)));
            Assert.Null(invalid);

            values[key] = "1";
            Assert.Empty(CollectorSettings.Validate(values, out var valid));
            Assert.NotNull(valid);
        }

        [Theory]
        [InlineData("9999", true)]
        [InlineData("10000", false)]
        [InlineData("12000", false)]
        public void Validate_HeartbeatMustBeStrictlyShorterThanStoppedTimeout(string heartbeat, bool accepted)
        {
            var values = ValidServerSettings();
            values["ServerStoppedSeconds"] = "10";
            values["HeartbeatIntervalMilliseconds"] = heartbeat;

            var errors = CollectorSettings.Validate(values, out var settings);

            if (accepted)
            {
                Assert.Empty(errors);
                Assert.Equal(TimeSpan.FromMilliseconds(9999), settings.HeartbeatInterval);
            }
            else
            {
                Assert.Null(settings);
                var error = Assert.Single(errors);
                Assert.Contains("HeartbeatIntervalMilliseconds", error);
                Assert.Contains("ServerStoppedSeconds", error);
            }
        }

        [Fact]
        public void Validate_NewViolations_AccumulatesErrorsWithoutValues()
        {
            const string invalidUrl = "http://change-me:5443/private";
            const string shortKey = "change-me-short";
            var values = ValidServerSettings();
            values["SERVER_BASE_URL"] = invalidUrl;
            values["COLLECTOR_API_KEY"] = shortKey;
            values["HeartbeatIntervalMilliseconds"] = "10000";
            values["HttpRequestTimeoutMilliseconds"] = "0";

            var errors = CollectorSettings.Validate(values, out var settings);

            Assert.Null(settings);
            Assert.Equal(4, errors.Count);
            Assert.Contains(errors, error => error.Contains("SERVER_BASE_URL"));
            Assert.Contains(errors, error => error.Contains("COLLECTOR_API_KEY"));
            Assert.Contains(errors, error => error.Contains("HeartbeatIntervalMilliseconds"));
            Assert.Contains(errors, error => error.Contains("HttpRequestTimeoutMilliseconds"));
            Assert.All(errors, error =>
            {
                Assert.DoesNotContain(shortKey, error);
                Assert.DoesNotContain(invalidUrl, error);
            });
        }

        private static Dictionary<string, string> ValidServerSettings()
        {
            return new Dictionary<string, string>
            {
                ["SERVER_BASE_URL"] = TestServerUrl,
                ["COLLECTOR_API_KEY"] = TestApiKey
            };
        }
    }
}
