﻿namespace Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector.Implementation.QuickPulse
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Runtime.Serialization.Json;
    using System.Threading;

    using Helpers;

    using Microsoft.ApplicationInsights.Extensibility.Filtering;
    using Microsoft.ApplicationInsights.Extensibility.Implementation.Tracing;
    using Microsoft.ManagementServices.RealTimeDataProcessing.QuickPulseService;

    /// <summary>
    /// Service client for QPS service.
    /// </summary>
    internal sealed class QuickPulseServiceClient : IQuickPulseServiceClient
    {
        private readonly string instanceName;

        private readonly string streamId;

        private readonly string machineName;

        private readonly string version;

        private readonly TimeSpan timeout = TimeSpan.FromSeconds(3);

        private readonly Clock timeProvider;

        private readonly bool isWebApp;

        private readonly int processorCount;

        private readonly DataContractJsonSerializer serializerDataPoint = new DataContractJsonSerializer(typeof(MonitoringDataPoint));

        private readonly DataContractJsonSerializer serializerDataPointArray = new DataContractJsonSerializer(typeof(MonitoringDataPoint[]));

        private readonly DataContractJsonSerializer deserializerServerResponse = new DataContractJsonSerializer(typeof(CollectionConfigurationInfo));

        private readonly Dictionary<string, string> authOpaqueHeaderValues = new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly HttpClient httpClient = new HttpClient();

        public QuickPulseServiceClient(
            Uri serviceUri,
            string instanceName,
            string streamId,
            string machineName,
            string version,
            Clock timeProvider,
            bool isWebApp,
            int processorCount,
            TimeSpan? timeout = null)
        {
            this.ServiceUri = serviceUri;
            this.instanceName = instanceName;
            this.streamId = streamId;
            this.machineName = machineName;
            this.version = version;
            this.timeProvider = timeProvider;
            this.isWebApp = isWebApp;
            this.processorCount = processorCount;
            this.timeout = timeout ?? this.timeout;

            foreach (string headerName in QuickPulseConstants.XMsQpsAuthOpaqueHeaderNames)
            {
                this.authOpaqueHeaderValues.Add(headerName, null);
            }
        }

        public Uri ServiceUri { get; }

        public bool? Ping(
            string instrumentationKey,
            DateTimeOffset timestamp,
            string configurationETag,
            string authApiKey,
            out CollectionConfigurationInfo configurationInfo)
        {
            var requestUri = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/ping?ikey={1}",
                this.ServiceUri.AbsoluteUri.TrimEnd('/'),
                Uri.EscapeUriString(instrumentationKey));

            return this.SendRequest(
                requestUri,
                true,
                configurationETag,
                authApiKey,
                out configurationInfo,
                requestStream => this.WritePingData(timestamp, requestStream));
        }

        public bool? SubmitSamples(
            IEnumerable<QuickPulseDataSample> samples,
            string instrumentationKey,
            string configurationETag,
            string authApiKey,
            out CollectionConfigurationInfo configurationInfo,
            CollectionConfigurationError[] collectionConfigurationErrors)
        {
            var requestUri = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/post?ikey={1}",
                this.ServiceUri.AbsoluteUri.TrimEnd('/'),
                Uri.EscapeUriString(instrumentationKey));

            return this.SendRequest(
                requestUri,
                false,
                configurationETag,
                authApiKey,
                out configurationInfo,
                requestStream => this.WriteSamples(samples, instrumentationKey, requestStream, collectionConfigurationErrors));
        }

        private bool? SendRequest(
            string requestUri,
            bool includeIdentityHeaders,
            string configurationETag,
            string authApiKey,
            out CollectionConfigurationInfo configurationInfo,
            Action<Stream> onWriteRequestBody)
        {
            try
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, requestUri);
                this.AddHeaders(request, includeIdentityHeaders, configurationETag, authApiKey);

                using (MemoryStream stream = new MemoryStream())
                {
                    onWriteRequestBody(stream);
                    stream.Flush();
                    ArraySegment<byte> buffer = stream.TryGetBuffer(out buffer) ? buffer : new ArraySegment<byte>();
                    request.Content = new ByteArrayContent(buffer.Array, buffer.Offset, buffer.Count);

                    HttpResponseMessage response = this.httpClient.SendAsync(request, new CancellationTokenSource(this.timeout).Token).GetAwaiter().GetResult();
                    if (response == null)
                    {
                        configurationInfo = null;
                        return null;
                    }

                    return this.ProcessResponse(response, configurationETag, out configurationInfo);
                }
            }
            catch (Exception e)
            {
                QuickPulseEventSource.Log.ServiceCommunicationFailedEvent(e.ToInvariantString());
            }

            configurationInfo = null;
            return null;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times", Justification = "Dispose is known to perform safely on Stream and StreamReader types.")]
        private bool? ProcessResponse(HttpResponseMessage response, string configurationETag, out CollectionConfigurationInfo configurationInfo)
        {
            configurationInfo = null;

            bool isSubscribed;
            if (!bool.TryParse(response.Headers.GetValuesSafe(QuickPulseConstants.XMsQpsSubscribedHeaderName).FirstOrDefault(), out isSubscribed))
            {
                // could not parse the isSubscribed value

                // read the response out to avoid issues with TCP connections not being freed up
                try
                {
                    response.Content.LoadIntoBufferAsync().GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // we did our best, we don't care about the outcome anyway
                }

                return null;
            }

            foreach (string headerName in QuickPulseConstants.XMsQpsAuthOpaqueHeaderNames)
            {
                this.authOpaqueHeaderValues[headerName] = response.Headers.GetValuesSafe(headerName).FirstOrDefault();
            }

            string configurationETagHeaderValue = response.Headers.GetValuesSafe(QuickPulseConstants.XMsQpsConfigurationETagHeaderName).FirstOrDefault();

            try
            {
                using (Stream responseStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                {
                    if (isSubscribed && !string.IsNullOrEmpty(configurationETagHeaderValue)
                        && !string.Equals(configurationETagHeaderValue, configurationETag, StringComparison.Ordinal) && responseStream != null)
                    {
                        configurationInfo = this.deserializerServerResponse.ReadObject(responseStream) as CollectionConfigurationInfo;
                    }
                }
            }
            catch (Exception e)
            {
                // couldn't read or deserialize the response
                QuickPulseEventSource.Log.ServiceCommunicationFailedEvent(e.ToInvariantString());
            }

            return isSubscribed;
        }

        private static double Round(double value)
        {
            return Math.Round(value, 4, MidpointRounding.AwayFromZero);
        }

        private void WritePingData(DateTimeOffset timestamp, Stream stream)
        {
            var dataPoint = new MonitoringDataPoint
            {
                Version = this.version,
                InvariantVersion = MonitoringDataPoint.CurrentInvariantVersion,
                // InstrumentationKey = instrumentationKey, // ikey is currently set in query string parameter
                Instance = this.instanceName,
                StreamId = this.streamId,
                MachineName = this.machineName,
                Timestamp = timestamp.UtcDateTime,
                IsWebApp = this.isWebApp,
                PerformanceCollectionSupported = this.isWebApp,
                ProcessorCount = this.processorCount
            };

            this.serializerDataPoint.WriteObject(stream, dataPoint);
        }

        private void WriteSamples(IEnumerable<QuickPulseDataSample> samples, string instrumentationKey, Stream stream, CollectionConfigurationError[] errors)
        {
            var monitoringPoints = new List<MonitoringDataPoint>();

            foreach (var sample in samples)
            {
                var metricPoints = new List<MetricPoint>();

                metricPoints.AddRange(CreateDefaultMetrics(sample));

                metricPoints.AddRange(
                    sample.PerfCountersLookup.Select(counter => new MetricPoint { Name = counter.Key, Value = Round(counter.Value), Weight = 1 }));

                metricPoints.AddRange(CreateCalculatedMetrics(sample));

                ITelemetryDocument[] documents = sample.TelemetryDocuments.ToArray();
                Array.Reverse(documents);

                ProcessCpuData[] topCpuProcesses =
                    sample.TopCpuData.Select(p => new ProcessCpuData() { ProcessName = p.Item1, CpuPercentage = p.Item2 }).ToArray();

                var dataPoint = new MonitoringDataPoint
                {
                    Version = this.version,
                    InvariantVersion = MonitoringDataPoint.CurrentInvariantVersion,
                    InstrumentationKey = instrumentationKey,
                    Instance = this.instanceName,
                    StreamId = this.streamId,
                    MachineName = this.machineName,
                    Timestamp = sample.EndTimestamp.UtcDateTime,
                    IsWebApp = this.isWebApp,
                    PerformanceCollectionSupported = this.isWebApp,
                    ProcessorCount = this.processorCount,
                    Metrics = metricPoints.ToArray(),
                    Documents = documents,
                    GlobalDocumentQuotaReached = sample.GlobalDocumentQuotaReached,
                    TopCpuProcesses = topCpuProcesses.Length > 0 ? topCpuProcesses : null,
                    TopCpuDataAccessDenied = sample.TopCpuDataAccessDenied,
                    CollectionConfigurationErrors = errors
                };

                monitoringPoints.Add(dataPoint);
            }

            this.serializerDataPointArray.WriteObject(stream, monitoringPoints.ToArray());
        }

        private static IEnumerable<MetricPoint> CreateCalculatedMetrics(QuickPulseDataSample sample)
        {
            var metrics = new List<MetricPoint>();

            foreach (AccumulatedValues metricAccumulatedValues in sample.CollectionConfigurationAccumulator.MetricAccumulators.Values)
            {
                try
                {
                    MetricPoint metricPoint = new MetricPoint
                    {
                        Name = metricAccumulatedValues.MetricId,
                        Value = metricAccumulatedValues.CalculateAggregation(out long count),
                        Weight = (int)count
                    };

                    metrics.Add(metricPoint);
                }
                catch (Exception e)
                {
                    // skip this metric
                    QuickPulseEventSource.Log.UnknownErrorEvent(e.ToString());
                }
            }

            return metrics;
        }

        private static IEnumerable<MetricPoint> CreateDefaultMetrics(QuickPulseDataSample sample)
        {
            return new[]
            {
                new MetricPoint { Name = @"\ApplicationInsights\Requests/Sec", Value = Round(sample.AIRequestsPerSecond), Weight = 1 },
                new MetricPoint
                {
                    Name = @"\ApplicationInsights\Request Duration",
                    Value = Round(sample.AIRequestDurationAveInMs),
                    Weight = sample.AIRequests
                },
                new MetricPoint { Name = @"\ApplicationInsights\Requests Failed/Sec", Value = Round(sample.AIRequestsFailedPerSecond), Weight = 1 },
                new MetricPoint
                {
                    Name = @"\ApplicationInsights\Requests Succeeded/Sec",
                    Value = Round(sample.AIRequestsSucceededPerSecond),
                    Weight = 1
                },
                new MetricPoint { Name = @"\ApplicationInsights\Dependency Calls/Sec", Value = Round(sample.AIDependencyCallsPerSecond), Weight = 1 },
                new MetricPoint
                {
                    Name = @"\ApplicationInsights\Dependency Call Duration",
                    Value = Round(sample.AIDependencyCallDurationAveInMs),
                    Weight = sample.AIDependencyCalls
                },
                new MetricPoint
                {
                    Name = @"\ApplicationInsights\Dependency Calls Failed/Sec",
                    Value = Round(sample.AIDependencyCallsFailedPerSecond),
                    Weight = 1
                },
                new MetricPoint
                {
                    Name = @"\ApplicationInsights\Dependency Calls Succeeded/Sec",
                    Value = Round(sample.AIDependencyCallsSucceededPerSecond),
                    Weight = 1
                },
                new MetricPoint { Name = @"\ApplicationInsights\Exceptions/Sec", Value = Round(sample.AIExceptionsPerSecond), Weight = 1 }
            };
        }

        private void AddHeaders(HttpRequestMessage request, bool includeIdentityHeaders, string configurationETag, string authApiKey)
        {
            request.Headers.TryAddWithoutValidation(QuickPulseConstants.XMsQpsTransmissionTimeHeaderName, this.timeProvider.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));

            request.Headers.TryAddWithoutValidation(QuickPulseConstants.XMsQpsConfigurationETagHeaderName, configurationETag);

            request.Headers.TryAddWithoutValidation(QuickPulseConstants.XMsQpsAuthApiKeyHeaderName, authApiKey ?? string.Empty);
            foreach (string headerName in QuickPulseConstants.XMsQpsAuthOpaqueHeaderNames)
            {
                request.Headers.TryAddWithoutValidation(headerName, this.authOpaqueHeaderValues[headerName]);
            }

            if (includeIdentityHeaders)
            {
                request.Headers.TryAddWithoutValidation(QuickPulseConstants.XMsQpsInstanceNameHeaderName, this.instanceName);
                request.Headers.TryAddWithoutValidation(QuickPulseConstants.XMsQpsStreamIdHeaderName, this.streamId);
                request.Headers.TryAddWithoutValidation(QuickPulseConstants.XMsQpsMachineNameHeaderName, this.machineName);
                request.Headers.TryAddWithoutValidation(QuickPulseConstants.XMsQpsInvariantVersionHeaderName,
                    MonitoringDataPoint.CurrentInvariantVersion.ToString(CultureInfo.InvariantCulture));
            }
        }
    }
}