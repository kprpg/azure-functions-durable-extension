// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;

namespace Microsoft.Azure.WebJobs.Extensions.DurableTask
{
    internal class EventGridLifeCycleNotificationHelper : ILifeCycleNotificationHelper
    {
        private readonly DurableTaskOptions options;
        private readonly EndToEndTraceHelper traceHelper;
        private readonly bool useTrace;
        private readonly string eventGridKeyValue;
        private readonly string eventGridTopicEndpoint;
        private readonly OrchestrationRuntimeStatus[] eventGridPublishEventTypes;
        private static HttpClient httpClient = null;
        private static HttpMessageHandler httpMessageHandler = null;

        public EventGridLifeCycleNotificationHelper(
            DurableTaskOptions options,
            INameResolver nameResolver,
            EndToEndTraceHelper traceHelper)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.traceHelper = traceHelper ?? throw new ArgumentNullException(nameof(traceHelper));

            if (nameResolver == null)
            {
                throw new ArgumentNullException(nameof(nameResolver));
            }

            if (options.Notifications == null)
            {
                throw new ArgumentNullException(nameof(options.Notifications));
            }

            var eventGridNotificationsConfig = options.Notifications.EventGrid ?? throw new ArgumentNullException(nameof(options.Notifications.EventGrid));

            this.eventGridKeyValue = nameResolver.Resolve(eventGridNotificationsConfig.KeySettingName);
            this.eventGridTopicEndpoint = eventGridNotificationsConfig.TopicEndpoint;

            if (nameResolver.TryResolveWholeString(eventGridNotificationsConfig.TopicEndpoint, out var endpoint))
            {
                this.eventGridTopicEndpoint = endpoint;
            }

            if (!string.IsNullOrEmpty(this.eventGridTopicEndpoint))
            {
                if (!string.IsNullOrEmpty(eventGridNotificationsConfig.KeySettingName))
                {
                    this.useTrace = true;

                    var retryStatusCode = eventGridNotificationsConfig.PublishRetryHttpStatus?
                                              .Where(x => Enum.IsDefined(typeof(HttpStatusCode), x))
                                              .Select(x => (HttpStatusCode)x)
                                              .ToArray()
                                          ?? Array.Empty<HttpStatusCode>();

                    if (eventGridNotificationsConfig.PublishEventTypes == null || eventGridNotificationsConfig.PublishEventTypes.Length == 0)
                    {
                        this.eventGridPublishEventTypes = (OrchestrationRuntimeStatus[])Enum.GetValues(typeof(OrchestrationRuntimeStatus));
                    }
                    else
                    {
                        var startedIndex = Array.FindIndex(eventGridNotificationsConfig.PublishEventTypes, x => x == "Started");
                        if (startedIndex > -1)
                        {
                            eventGridNotificationsConfig.PublishEventTypes[startedIndex] = OrchestrationRuntimeStatus.Running.ToString();
                        }

                        OrchestrationRuntimeStatus ParseAndvalidateEvents(string @event)
                        {
                            var success = Enum.TryParse(@event, out OrchestrationRuntimeStatus @enum);
                            if (success)
                            {
                                switch (@enum)
                                {
                                    case OrchestrationRuntimeStatus.Canceled:
                                    case OrchestrationRuntimeStatus.ContinuedAsNew:
                                    case OrchestrationRuntimeStatus.Pending:
                                        success = false;
                                        break;
                                    default:
                                        break;
                                }
                            }

                            if (!success)
                            {
                                throw new ArgumentException("Failed to start lifecycle notification feature. Unsupported event types detected in 'EventGridPublishEventTypes'. You may only specify one or more of the following 'Started', 'Completed', 'Failed', 'Terminated'.");
                            }

                            return @enum;
                        }

                        this.eventGridPublishEventTypes = eventGridNotificationsConfig.PublishEventTypes.Select(x => ParseAndvalidateEvents(x)).ToArray();
                    }

                    // Currently, we support Event Grid Custom Topic for notify the lifecycle event of an orchestrator.
                    // For more detail about the Event Grid, please refer this document.
                    // Post to custom topic for Azure Event Grid
                    // https://docs.microsoft.com/en-us/azure/event-grid/post-to-custom-topic
                    this.HttpMessageHandler = options.NotificationHandler ?? new HttpRetryMessageHandler(
                        new HttpClientHandler(),
                        eventGridNotificationsConfig.PublishRetryCount,
                        eventGridNotificationsConfig.PublishRetryInterval,
                        retryStatusCode);

                    if (string.IsNullOrEmpty(this.eventGridKeyValue))
                    {
                        throw new ArgumentException($"Failed to start lifecycle notification feature. Please check the configuration values for {eventGridNotificationsConfig.KeySettingName} on AppSettings.");
                    }
                }
                else
                {
                    throw new ArgumentException($"Failed to start lifecycle notification feature. Please check the configuration values for {eventGridNotificationsConfig.TopicEndpoint} and {eventGridNotificationsConfig.KeySettingName}.");
                }
            }
        }

        public string EventGridKeyValue => this.eventGridKeyValue;

        public string EventGridTopicEndpoint => this.eventGridTopicEndpoint;

        public HttpMessageHandler HttpMessageHandler
        {
            get => httpMessageHandler;
            set
            {
                httpClient?.Dispose();
                httpMessageHandler = value;
                httpClient = new HttpClient(httpMessageHandler);
                httpClient.DefaultRequestHeaders.Add("aeg-sas-key", this.eventGridKeyValue);
            }
        }

        private async Task SendNotificationAsync(
            EventGridEvent[] eventGridEventArray,
            string hubName,
            string functionName,
            string instanceId,
            string reason,
            FunctionState functionState)
        {
            string json = JsonConvert.SerializeObject(eventGridEventArray);
            StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
            Stopwatch stopWatch = Stopwatch.StartNew();

            // Details about the Event Grid REST API
            // https://docs.microsoft.com/en-us/rest/api/eventgrid/
            HttpResponseMessage result = null;
            try
            {
                result = await httpClient.PostAsync(this.eventGridTopicEndpoint, content);
            }
            catch (Exception e)
            {
                this.traceHelper.EventGridException(
                    hubName,
                    functionName,
                    functionState,
                    instanceId,
                    e.StackTrace,
                    e,
                    reason,
                    stopWatch.ElapsedMilliseconds);
                return;
            }

            using (result)
            {
                var body = await result.Content.ReadAsStringAsync();
                if (result.IsSuccessStatusCode)
                {
                    this.traceHelper.EventGridSuccess(
                        hubName,
                        functionName,
                        functionState,
                        instanceId,
                        body,
                        result.StatusCode,
                        reason,
                        stopWatch.ElapsedMilliseconds);
                }
                else
                {
                    this.traceHelper.EventGridFailed(
                        hubName,
                        functionName,
                        functionState,
                        instanceId,
                        body,
                        result.StatusCode,
                        reason,
                        stopWatch.ElapsedMilliseconds);
                }
            }
        }

        public async Task OrchestratorStartingAsync(
            string hubName,
            string functionName,
            string instanceId,
            bool isReplay)
        {
            if (!this.useTrace)
            {
                return;
            }

            if (!this.eventGridPublishEventTypes.Contains(OrchestrationRuntimeStatus.Running))
            {
                return;
            }

            EventGridEvent[] sendObject = this.CreateEventGridEvent(
                hubName,
                functionName,
                instanceId,
                "",
                OrchestrationRuntimeStatus.Running);
            await this.SendNotificationAsync(sendObject, hubName, functionName, instanceId, "", FunctionState.Started);
        }

        public async Task OrchestratorCompletedAsync(
            string hubName,
            string functionName,
            string instanceId,
            bool continuedAsNew,
            bool isReplay)
        {
            if (!this.useTrace)
            {
                return;
            }

            if (!this.eventGridPublishEventTypes.Contains(OrchestrationRuntimeStatus.Completed))
            {
                return;
            }

            EventGridEvent[] sendObject = this.CreateEventGridEvent(
                hubName,
                functionName,
                instanceId,
                "",
                OrchestrationRuntimeStatus.Completed);
            await this.SendNotificationAsync(sendObject, hubName, functionName, instanceId, "", FunctionState.Completed);
        }

        public async Task OrchestratorFailedAsync(
            string hubName,
            string functionName,
            string instanceId,
            string reason,
            bool isReplay)
        {
            if (!this.useTrace)
            {
                return;
            }

            if (!this.eventGridPublishEventTypes.Contains(OrchestrationRuntimeStatus.Failed))
            {
                return;
            }

            EventGridEvent[] sendObject = this.CreateEventGridEvent(
                hubName,
                functionName,
                instanceId,
                reason,
                OrchestrationRuntimeStatus.Failed);
            await this.SendNotificationAsync(sendObject, hubName, functionName, instanceId, reason, FunctionState.Failed);
        }

        public async Task OrchestratorTerminatedAsync(
            string hubName,
            string functionName,
            string instanceId,
            string reason)
        {
            if (!this.useTrace)
            {
                return;
            }

            if (!this.eventGridPublishEventTypes.Contains(OrchestrationRuntimeStatus.Terminated))
            {
                return;
            }

            EventGridEvent[] sendObject = this.CreateEventGridEvent(
                hubName,
                functionName,
                instanceId,
                reason,
                OrchestrationRuntimeStatus.Terminated);
            await this.SendNotificationAsync(sendObject, hubName, functionName, instanceId, reason, FunctionState.Terminated);
        }

        private EventGridEvent[] CreateEventGridEvent(
            string hubName,
            string functionName,
            string instanceId,
            string reason,
            OrchestrationRuntimeStatus orchestrationRuntimeStatus)
        {
            return new[]
            {
                new EventGridEvent
                {
                    Id = Guid.NewGuid().ToString(),
                    EventType = "orchestratorEvent",
                    Subject = $"durable/orchestrator/{orchestrationRuntimeStatus}",
                    EventTime = DateTime.UtcNow,
                    Data = new EventGridPayload
                    {
                        HubName = hubName,
                        FunctionName = functionName,
                        InstanceId = instanceId,
                        Reason = reason,
                        RuntimeStatus = orchestrationRuntimeStatus.ToString(),
                    },
                    DataVersion = "1.0",
                },
            };
        }

        private class EventGridPayload
        {
            public EventGridPayload() { }

            [JsonProperty(PropertyName = "hubName")]
            public string HubName { get; set; }

            [JsonProperty(PropertyName = "functionName")]
            public string FunctionName { get; set; }

            [JsonProperty(PropertyName = "instanceId")]
            public string InstanceId { get; set; }

            [JsonProperty(PropertyName = "reason")]
            public string Reason { get; set; }

            [JsonProperty(PropertyName = "runtimeStatus")]
            public string RuntimeStatus { get; set; }
        }

        private class EventGridEvent
        {
            public EventGridEvent() { }

            [JsonProperty(PropertyName = "id")]
            public string Id { get; set; }

            [JsonProperty(PropertyName = "topic")]
            public string Topic { get; set; }

            [JsonProperty(PropertyName = "subject")]
            public string Subject { get; set; }

            [JsonProperty(PropertyName = "data")]
            public EventGridPayload Data { get; set; }

            [JsonProperty(PropertyName = "eventType")]
            public string EventType { get; set; }

            [JsonProperty(PropertyName = "eventTime")]
            public DateTime EventTime { get; set; }

            [JsonProperty(PropertyName = "metadataVersion")]
            public string MetadataVersion { get; }

            [JsonProperty(PropertyName = "dataVersion")]
            public string DataVersion { get; set; }
        }

        internal class HttpRetryMessageHandler : DelegatingHandler
        {
            public HttpRetryMessageHandler(HttpMessageHandler messageHandler, int maxRetryCount, TimeSpan retryWaitSpan, HttpStatusCode[] retryTargetStatusCode)
                : base(messageHandler)
            {
                this.MaxRetryCount = maxRetryCount;
                this.RetryWaitSpan = retryWaitSpan;
                this.RetryTargetStatus = retryTargetStatusCode;
            }

            public int MaxRetryCount { get; }

            public TimeSpan RetryWaitSpan { get; }

            public HttpStatusCode[] RetryTargetStatus { get; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var tryCount = 0;
                Exception lastException = null;
                HttpResponseMessage response = null;
                do
                {
                    try
                    {
                        response = await base.SendAsync(request, cancellationToken);
                        if (response.IsSuccessStatusCode)
                        {
                            return response;
                        }
                        else if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                        {
                            if (this.RetryTargetStatus.All(x => x != response.StatusCode))
                            {
                                return response;
                            }
                        }
                    }
                    catch (HttpRequestException e)
                    {
                        lastException = e;
                    }

                    tryCount++;

                    await Task.Delay(this.RetryWaitSpan, cancellationToken);
                }
                while (this.MaxRetryCount >= tryCount);

                if (response != null)
                {
                    return response;
                }
                else
                {
                    ExceptionDispatchInfo.Capture(lastException).Throw();
                    return null;
                }
            }
        }
    }
}
