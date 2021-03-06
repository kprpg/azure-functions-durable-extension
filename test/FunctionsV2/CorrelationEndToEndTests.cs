// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DurableTask.AzureStorage;
using DurableTask.Core;
using DurableTask.Core.Settings;
using FluentAssertions.Collections;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility.Implementation;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.DurableTask.Tests;
using Microsoft.Azure.WebJobs.Host.TestCommon;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Azure.WebJobs.Extensions.DurableTask.Tests
{
    [Collection("Non-Parallel Collection")]
    public class CorrelationEndToEndTests
    {
        private const string TestSiteName = "TestSite";
        private readonly ITestOutputHelper output;
        private readonly TestLoggerProvider loggerProvider;

        public CorrelationEndToEndTests(ITestOutputHelper output)
        {
            this.output = output;
            this.loggerProvider = new TestLoggerProvider(output);
        }

        [Theory]
        [Trait("Category", PlatformSpecificHelpers.TestCategory)]
        [InlineData(false, "W3CTraceContext")]
        [InlineData(true, "HttpCorrelationProtocol")]
        [InlineData(true, "W3CTraceContext")]
        [InlineData(false, "HttpCorrelationProtocol")]
        public async Task SingleOrchestration_With_Activity(bool extendedSessions, string protocol)
        {
            string[] orchestrationFunctionNames =
            {
                nameof(TestOrchestrations.SayHelloWithActivity),
            };

            var result = await
                this.ExecuteOrchestrationWithExceptionAsync(
                    orchestrationFunctionNames,
                    "SingleOrchestration",
                    "world",
                    extendedSessions,
                    protocol);
            var actual = result.Item1;
            Assert.Equal(5, actual.Count);
            Assert.Empty(result.Item2);
            Assert.Equal(
                new (Type, string)[]
                {
                    (typeof(RequestTelemetry), $"{TraceConstants.Client}: "),
                    (typeof(DependencyTelemetry), TraceConstants.Client),
                    (typeof(RequestTelemetry), $"{TraceConstants.Orchestrator} SayHelloWithActivity"),
                    (typeof(DependencyTelemetry), $"{TraceConstants.Orchestrator} Hello"),
                    (typeof(RequestTelemetry), $"{TraceConstants.Activity} Hello"),
                }.ToList(), actual.Select(x => (x.GetType(), x.Name)).ToList());
        }

        [Theory]
        [Trait("Category", PlatformSpecificHelpers.TestCategory)]
        [InlineData(false, "W3CTraceContext")]
        [InlineData(true, "HttpCorrelationProtocol")]
        [InlineData(true, "W3CTraceContext")]
        [InlineData(false, "HttpCorrelationProtocol")]
        public async Task CheckOperationName_RequestTelemetry_SingleOrchestration(bool extendedSessions, string protocol)
        {
            string[] orchestrationFunctionNames =
            {
                nameof(TestOrchestrations.SayHelloWithActivity),
            };

            var result = await
                this.ExecuteOrchestrationWithExceptionAsync(
                    orchestrationFunctionNames,
                    "SingleOrchestration",
                    "world",
                    extendedSessions,
                    protocol);

            var traceTelemetry = result.Item1;

            // Using actual.First() because there's only one Request Telemetry where the name is "DtActivity Hello"
            RequestTelemetry dtActivityReqTelemetry = traceTelemetry.First(x => x.GetType() == typeof(RequestTelemetry) && x.Name.Contains(TraceConstants.Activity)) as RequestTelemetry;
            Assert.Equal("Hello", dtActivityReqTelemetry.Context.Operation.Name);
        }

        [Theory]
        [Trait("Category", PlatformSpecificHelpers.TestCategory)]
        [InlineData(false, "W3CTraceContext")]
        [InlineData(true, "HttpCorrelationProtocol")]
        [InlineData(true, "W3CTraceContext")]
        [InlineData(false, "HttpCorrelationProtocol")]
        public async Task CheckCloudRoleName_RequestTelemetry_SingleOrchestration(bool extendedSessions, string protocol)
        {
            string[] orchestrationFunctionNames =
            {
                nameof(TestOrchestrations.SayHelloWithActivity),
            };

            var result = await
                this.ExecuteOrchestrationWithExceptionAsync(
                    orchestrationFunctionNames,
                    "SingleOrchestration",
                    "world",
                    extendedSessions,
                    protocol);

            var traceTelemetry = result.Item1;

            // Comparing cloud role name with testSiteName.toLower() to match the lowercase app name convention
            List<OperationTelemetry> requestTelemetryWithCloudRoleNamesList = traceTelemetry.Where(x => x.GetType() == typeof(RequestTelemetry) && x.Context.Cloud.RoleName.Equals(TestSiteName.ToLower())).ToList();
            Assert.NotEmpty(requestTelemetryWithCloudRoleNamesList);
        }

        /*
        [Theory]
        [Trait("Category", PlatformSpecificHelpers.TestCategory)]
        [InlineData(false, "W3CTraceContext")]
        [InlineData(true, "HttpCorrelationProtocol")]
        [InlineData(true, "W3CTraceContext")]
        [InlineData(false, "HttpCorrelationProtocol")]
        public async Task AllOrchestrationActivityActions(bool extendedSessions, string protocol)
        {
            string[] orchestrationFunctionNames =
            {
                nameof(TestOrchestrations.AllOrchestratorActivityActions),
            };

            var counterEntityId = new EntityId("Counter", Guid.NewGuid().ToString());

            var result = await
                this.ExecuteOrchestrationWithExceptionAsync(
                    orchestrationFunctionNames,
                    nameof(this.AllOrchestrationActivityActions),
                    counterEntityId,
                    extendedSessions,
                    protocol);
            var actual = result.Item1;
            Assert.Equal(15, actual.Count);
            // TODO: This part of the test is failing. This is commented out temporarily since Correlation Tracing is a WIP.
            // Assert.Single(result.Item2); // Error inside of HttpActivity since the request set to null.
            Assert.Equal(
                new (Type, string)[]
                {
                    (typeof(RequestTelemetry), $"{TraceConstants.Client}: "),  // start orchestration
                    (typeof(DependencyTelemetry), TraceConstants.Client),
                    (typeof(RequestTelemetry), $"{TraceConstants.Orchestrator} AllOrchestratorActivityActions"), // Orchestrator started
                    (typeof(DependencyTelemetry), $"{TraceConstants.Orchestrator} Hello"),
                    (typeof(RequestTelemetry), $"{TraceConstants.Activity} Hello"), // Activity Hello Started
                    (typeof(DependencyTelemetry), $"{TraceConstants.Orchestrator} Hello"),
                    (typeof(RequestTelemetry), $"{TraceConstants.Activity} Hello"),  // Activity Hello Started
                    (typeof(DependencyTelemetry), $"{TraceConstants.Orchestrator} SayHelloInline"),
                    (typeof(RequestTelemetry), $"{TraceConstants.Orchestrator} SayHelloInline"),  // SubOrchestrator SayHelloInline Started
                    (typeof(DependencyTelemetry), $"{TraceConstants.Orchestrator} SayHelloWithActivity"),
                    (typeof(RequestTelemetry), $"{TraceConstants.Orchestrator} SayHelloWithActivity"), // SubOrchestrator SayHelloWithActivity Started
                    (typeof(DependencyTelemetry), $"{TraceConstants.Orchestrator} Hello"),
                    (typeof(RequestTelemetry), $"{TraceConstants.Activity} Hello"), // Activity Hello Started by SubOrchestrator SayHelloWithActivity
                    (typeof(DependencyTelemetry), $"{TraceConstants.Orchestrator} BuiltIn::HttpActivity"),
                    (typeof(RequestTelemetry), $"{TraceConstants.Activity} BuiltIn::HttpActivity"),  // HttpActivity Started
                }.ToList(), actual.Select(x => (x.GetType(), x.Name)).ToList());
        }
        */

        internal async Task<Tuple<List<OperationTelemetry>, List<ExceptionTelemetry>>>
            ExecuteOrchestrationWithExceptionAsync(
                string[] orchestratorFunctionNames,
                string testName,
                object input,
                bool extendedSessions,
                string protocol)
        {
            ConcurrentQueue<ITelemetry> sendItems = new ConcurrentQueue<ITelemetry>();
            TraceOptions traceOptions = new TraceOptions()
            {
                DistributedTracingEnabled = true,
                DistributedTracingProtocol = protocol,
            };
            DurableTaskOptions options = new DurableTaskOptions();
            options.Tracing = traceOptions;
            var sendAction = new Action<ITelemetry>(
                delegate(ITelemetry telemetry) { sendItems.Enqueue(telemetry); });

            string siteNameEnvironmentVarName = "WEBSITE_SITE_NAME";
            string siteNameEnvironmentVarValue = TestSiteName;
            var mockNameResolver = GetNameResolverMock(new[] { (siteNameEnvironmentVarName, siteNameEnvironmentVarValue) });

            using (var host = TestHelpers.GetJobHost(
                this.loggerProvider,
                testName,
                extendedSessions,
                options: options,
                nameResolver: mockNameResolver.Object,
                onSend: sendAction))
            {
                await host.StartAsync();
                var client = await host.StartOrchestratorAsync(orchestratorFunctionNames[0], input, this.output);
                var status = await client.WaitForCompletionAsync(this.output, timeout: TimeSpan.FromSeconds(90));
                await host.StopAsync();
            }

            var sendItemList = this.ConvertTo(sendItems);
            var operationTelemetryList = sendItemList.OfType<OperationTelemetry>();
            var exceptionTelemetryList = sendItemList.OfType<ExceptionTelemetry>().ToList();
            var result = this.FilterOperationTelemetry(operationTelemetryList).ToList();
            return new Tuple<List<OperationTelemetry>, List<ExceptionTelemetry>>(result.CorrelationSort(), exceptionTelemetryList);
        }

         /*
         * End to end test that checks if a warning is logged when distributed tracing is
         * enabled, but APPINSIGHTS_INSTRUMENTATIONKEY isn't set. The test also checks
         * that the warning isn't logged when the environment variable is set.
         */
        [Theory]
        [Trait("Category", PlatformSpecificHelpers.TestCategory)]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void TelemetryClientSetup_AppInsightsInstrumentationKey_Warning(bool keyIsSet, bool extendedSessions)
        {
            TraceOptions traceOptions = new TraceOptions()
            {
                DistributedTracingEnabled = true,
                DistributedTracingProtocol = "W3CTraceContext",
            };

            DurableTaskOptions options = new DurableTaskOptions();
            options.Tracing = traceOptions;

            string environmentVariableName = "APPINSIGHTS_INSTRUMENTATIONKEY";
            string environmentVariableValue = "test key";
            var mockNameResolver = keyIsSet
                ? GetNameResolverMock(new[] { (environmentVariableName, environmentVariableValue) })
                : GetNameResolverMock(new[] { (environmentVariableName, string.Empty) });

            using (var host = TestHelpers.GetJobHost(
                this.loggerProvider,
                "SingleOrchestration",
                extendedSessions,
                nameResolver: mockNameResolver.Object,
                options: options))
            {
                string warningMessage = "'APPINSIGHTS_INSTRUMENTATIONKEY' isn't defined in the current environment variables, but Distributed Tracing is enabled. Please set 'APPINSIGHTS_INSTRUMENTATIONKEY' to use Distributed Tracing.";
                var warningLogMessage = this.loggerProvider.GetAllLogMessages().Where(l => l.FormattedMessage.StartsWith(warningMessage));

                string settingUpTelemetryClientMessage = "Setting up the telemetry client...";
                var settingUpTelemetryClientLogMessage = this.loggerProvider.GetAllLogMessages().Where(l => l.FormattedMessage.StartsWith(settingUpTelemetryClientMessage));

                string readingInstrumentationKeyMessage = "Reading APPINSIGHTS_INSTRUMENTATIONKEY...";
                var readingInstrumentationKeyLogMessage = this.loggerProvider.GetAllLogMessages().Where(l => l.FormattedMessage.StartsWith(readingInstrumentationKeyMessage));

                if (keyIsSet)
                {
                    Assert.Single(settingUpTelemetryClientLogMessage);
                    Assert.Single(readingInstrumentationKeyLogMessage);
                    Assert.Empty(warningLogMessage);
                }
                else
                {
                    Assert.Single(settingUpTelemetryClientLogMessage);
                    Assert.Empty(readingInstrumentationKeyLogMessage);
                    Assert.Single(warningLogMessage);
                }
            }
        }

        private static Mock<INameResolver> GetNameResolverMock((string Key, string Value)[] settings)
        {
            var mock = new Mock<INameResolver>();
            foreach (var setting in settings)
            {
                mock.Setup(x => x.Resolve(setting.Key)).Returns(setting.Value);
            }

            return mock;
        }

        private IEnumerable<OperationTelemetry> FilterOperationTelemetry(IEnumerable<OperationTelemetry> operationTelemetries)
        {
            return operationTelemetries.Where(
                p => p.Name.Contains(TraceConstants.Activity) || p.Name.Contains(TraceConstants.Orchestrator) || p.Name.Contains(TraceConstants.Client) || p.Name.Contains("Operation"));
        }

        private List<ITelemetry> ConvertTo(ConcurrentQueue<ITelemetry> queue)
        {
            var converted = new List<ITelemetry>();
            while (!queue.IsEmpty)
            {
                ITelemetry x;
                if (queue.TryDequeue(out x))
                {
                    converted.Add(x);
                }
            }

            return converted;
        }
    }

#pragma warning disable SA1402
    public static class ListExtensions
    {
        public static List<OperationTelemetry> CorrelationSort(this List<OperationTelemetry> telemetries)
        {
            var result = new List<OperationTelemetry>();
            if (telemetries.Count == 0)
            {
                return result;
            }

            // Sort by the timestamp
            var sortedTelemetries = telemetries.OrderBy(p => p.Timestamp.Ticks).ToList();

            // pick the first one as the parent. remove it from the list.
            var parent = sortedTelemetries.First();
            result.Add(parent);
            sortedTelemetries.RemoveOperationTelemetry(parent);

            // find the child recursively and remove the child and pass it as a parameter
            var sortedList = GetCorrelationSortedList(parent, sortedTelemetries);
            result.AddRange(sortedList);
            return result;
        }

        public static bool RemoveOperationTelemetry(this List<OperationTelemetry> telemetries, OperationTelemetry telemetry)
        {
            int index = -1;
            for (var i = 0; i < telemetries.Count; i++)
            {
                if (telemetries[i].Id == telemetry.Id)
                {
                    index = i;
                }
            }

            if (index == -1)
            {
                return false;
            }

            telemetries.RemoveAt(index);
            return true;
        }

        private static List<OperationTelemetry> GetCorrelationSortedList(OperationTelemetry parent, List<OperationTelemetry> current)
        {
            var result = new List<OperationTelemetry>();
            if (current.Count != 0)
            {
                foreach (var some in current)
                {
                    if (parent.Id == some.Context.Operation.ParentId)
                    {
                        Console.WriteLine("match");
                    }
                }

                IOrderedEnumerable<OperationTelemetry> nexts = current.Where(p => p.Context.Operation.ParentId == parent.Id).OrderBy(p => p.Timestamp.Ticks);
                foreach (OperationTelemetry next in nexts)
                {
                    current.RemoveOperationTelemetry(next);
                    result.Add(next);
                    var childResult = GetCorrelationSortedList(next, current);
                    result.AddRange(childResult);
                }
            }

            return result;
        }
    }

    [CollectionDefinition("Non-Parallel Collection", DisableParallelization = true)]
    public class NonParallelCollectionDefinitionClass
    {
    }
#pragma warning restore SA1402
}
