﻿namespace Microsoft.ApplicationInsights.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.ApplicationInsights.Common;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.DependencyCollector;
    using Microsoft.ApplicationInsights.DependencyCollector.Implementation;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.ApplicationInsights.Extensibility.Implementation;
    using Microsoft.ApplicationInsights.Extensibility.Implementation.ApplicationId;
    using Microsoft.ApplicationInsights.TestFramework;
    using Microsoft.ApplicationInsights.W3C;
    using Microsoft.ApplicationInsights.Web.TestFramework;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

#pragma warning disable 612, 618

    /// <summary>
    /// .NET Core specific tests that verify Http Dependencies are collected for outgoing request
    /// </summary>
    [TestClass]
    public class DependencyTrackingTelemetryModuleTestNetCore
    {
        private const string IKey = "F8474271-D231-45B6-8DD4-D344C309AE69";
        private const string FakeProfileApiEndpoint = "https://dc.services.visualstudio.com/v2/track";
        private const string localhostUrl = "http://localhost:5050";
        private const string expectedAppId = "someAppId";

        private readonly DictionaryApplicationIdProvider appIdProvider = new DictionaryApplicationIdProvider();
        private StubTelemetryChannel channel;
        private TelemetryConfiguration config;
        private List<DependencyTelemetry> sentTelemetry;

        private object request;
        private object response;
        private object responseHeaders;

        /// <summary>
        /// Initialize.
        /// </summary>
        [TestInitialize]
        public void Initialize()
        {
            this.sentTelemetry = new List<DependencyTelemetry>();
            this.request = null;
            this.response = null;
            this.responseHeaders = null;

            this.channel = new StubTelemetryChannel
            {
                OnSend = telemetry =>
                {
                    // The correlation id lookup service also makes http call, just make sure we skip that
                    DependencyTelemetry depTelemetry = telemetry as DependencyTelemetry;
                    if (depTelemetry != null)
                    {
                        this.sentTelemetry.Add(depTelemetry);
                        depTelemetry.TryGetOperationDetail(RemoteDependencyConstants.HttpRequestOperationDetailName, out this.request);
                        depTelemetry.TryGetOperationDetail(RemoteDependencyConstants.HttpResponseOperationDetailName, out this.response);
                        depTelemetry.TryGetOperationDetail(RemoteDependencyConstants.HttpResponseHeadersOperationDetailName, out this.responseHeaders);
                    }
                },
                EndpointAddress = FakeProfileApiEndpoint
            };

            this.appIdProvider.Defined = new Dictionary<string, string>
            {
                [IKey] = expectedAppId
            };

            this.config = new TelemetryConfiguration
            {
                InstrumentationKey = IKey,
                TelemetryChannel = this.channel,
                ApplicationIdProvider = this.appIdProvider
            };

            this.config.TelemetryInitializers.Add(new OperationCorrelationTelemetryInitializer());
        }

        /// <summary>
        /// Cleans up.
        /// </summary>
        [TestCleanup]
        public void Cleanup()
        {
            while (Activity.Current != null)
            {
                Activity.Current.Stop();
            }
        }

        /// <summary>
        /// Tests that dependency is collected properly when there is no parent activity.
        /// </summary>
        [TestMethod]
        [Timeout(5000)]
        public async Task TestDependencyCollectionWithLegacyHeaders()
        {
            using (var module = new DependencyTrackingTelemetryModule())
            {
                module.EnableLegacyCorrelationHeadersInjection = true;
                module.Initialize(this.config);

                var url = new Uri(localhostUrl);
                var request = new HttpRequestMessage(HttpMethod.Get, url);

                using (new LocalServer(localhostUrl))
                {
                    await new HttpClient().SendAsync(request);
                }

                // DiagnosticSource Response event is fired after SendAsync returns on netcoreapp1.*
                // let's wait until dependency is collected
                Assert.IsTrue(SpinWait.SpinUntil(() => this.sentTelemetry != null, TimeSpan.FromSeconds(1)));

                this.ValidateTelemetryForDiagnosticSource(this.sentTelemetry.Single(), url, request, true, "200", true);
            }
        }

        /// <summary>
        /// Tests that dependency is collected properly when there is no parent activity.
        /// </summary>
        [TestMethod]
        [Timeout(5000)]
        public async Task TestDependencyCollectionNoParentActivity()
        {
            using (var module = new DependencyTrackingTelemetryModule())
            {
                module.Initialize(this.config);

                var url = new Uri(localhostUrl);
                var request = new HttpRequestMessage(HttpMethod.Get, url);

                using (new LocalServer(localhostUrl))
                {
                    await new HttpClient().SendAsync(request);
                }

                // DiagnosticSource Response event is fired after SendAsync returns on netcoreapp1.*
                // let's wait until dependency is collected
                Assert.IsTrue(SpinWait.SpinUntil(() => this.sentTelemetry != null, TimeSpan.FromSeconds(1)));

                this.ValidateTelemetryForDiagnosticSource(this.sentTelemetry.Single(), url, request, true, "200", false);
            }
        }

        /// <summary>
        /// Tests that dependency is collected properly when there is parent activity.
        /// </summary>
        [TestMethod]
        [Timeout(5000)]
        public async Task TestDependencyCollectionWithParentActivity()
        {
            using (var module = new DependencyTrackingTelemetryModule())
            {
                module.Initialize(this.config);

                var parent = new Activity("parent").AddBaggage("k", "v").SetParentId("|guid.").Start();

                var url = new Uri(localhostUrl);
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                using (new LocalServer(localhostUrl))
                {
                    await new HttpClient().SendAsync(request);
                }

                // DiagnosticSource Response event is fired after SendAsync returns on netcoreapp1.*
                // let's wait until dependency is collected
                Assert.IsTrue(SpinWait.SpinUntil(() => this.sentTelemetry != null, TimeSpan.FromSeconds(1)));

                parent.Stop();

                this.ValidateTelemetryForDiagnosticSource(this.sentTelemetry.Single(), url, request, true, "200", false);

                Assert.AreEqual("k=v", request.Headers.GetValues(RequestResponseHeaders.CorrelationContextHeader).Single());
            }
        }

        /// <summary>
        /// Tests dependency collection when request procession causes exception (DNS issue).
        /// On .netcore1.1 and before, such dependencies are ot collected
        /// On .netcore2.0 they are collected, but there is no build infra to support it (https://github.com/Microsoft/ApplicationInsights-dotnet-server/issues/572)
        /// TODO: add tests for 2.0
        /// </summary>
        [TestMethod]
        [Timeout(10000)]
        public async Task TestDependencyCollectionDnsIssue()
        {
            using (var module = new DependencyTrackingTelemetryModule())
            {
                module.Initialize(this.config);

                var request = new HttpRequestMessage(HttpMethod.Get, $"http://{Guid.NewGuid()}");
                await new HttpClient().SendAsync(request).ContinueWith(t => { });
                Assert.IsFalse(this.sentTelemetry.Any());
            }
        }

        /// <summary>
        /// Tests that dependency is collected properly when there is parent activity.
        /// </summary>
        [TestMethod]
        [Timeout(5000)]
        public async Task TestDependencyCollectionWithW3CHeadersAndRequestId()
        {
            using (var module = new DependencyTrackingTelemetryModule())
            {
                module.EnableW3CHeadersInjection = true;
                this.config.TelemetryInitializers.Add(new W3COperationCorrelationTelemetryInitializer());
                module.Initialize(this.config);

                var parent = new Activity("parent")
                    .AddBaggage("k", "v")
                    .SetParentId("|guid.")
                    .Start()
                    .GenerateW3CContext();

                var url = new Uri(localhostUrl);
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                using (new LocalServer(localhostUrl))
                {
                    await new HttpClient().SendAsync(request);
                }

                // DiagnosticSource Response event is fired after SendAsync returns on netcoreapp1.*
                // let's wait until dependency is collected
                Assert.IsTrue(SpinWait.SpinUntil(() => this.sentTelemetry != null, TimeSpan.FromSeconds(1)));

                parent.Stop();

                string expectedTraceId = parent.Tags.Single(t => t.Key == W3CConstants.TraceIdTag).Value;
                string expectedParentId = parent.Tags.Single(t => t.Key == W3CConstants.SpanIdTag).Value;

                DependencyTelemetry dependency = this.sentTelemetry.Single();
                Assert.AreEqual(expectedTraceId, dependency.Context.Operation.Id);
                Assert.AreEqual(expectedParentId, dependency.Context.Operation.ParentId);

                Assert.IsTrue(request.Headers.Contains(W3CConstants.TraceParentHeader));
                Assert.AreEqual($"00-{expectedTraceId}-{dependency.Id}-01", request.Headers.GetValues(W3CConstants.TraceParentHeader).Single());

                Assert.IsTrue(request.Headers.Contains(W3CConstants.TraceStateHeader));
                Assert.AreEqual($"{W3CConstants.ApplicationIdTraceStateField}={expectedAppId}", request.Headers.GetValues(W3CConstants.TraceStateHeader).Single());

                Assert.IsTrue(request.Headers.Contains(RequestResponseHeaders.CorrelationContextHeader));
                Assert.AreEqual("k=v", request.Headers.GetValues(RequestResponseHeaders.CorrelationContextHeader).Single());

                Assert.AreEqual("v", dependency.Properties["k"]);
            }
        }

        /// <summary>
        /// Tests that dependency is collected properly when there is parent activity.
        /// </summary>
        [TestMethod]
        [Timeout(5000)]
        public async Task TestDependencyCollectionWithW3CHeadersWithState()
        {
            using (var module = new DependencyTrackingTelemetryModule())
            {
                module.EnableW3CHeadersInjection = true;
                this.config.TelemetryInitializers.Add(new W3COperationCorrelationTelemetryInitializer());
                module.Initialize(this.config);

                var parent = new Activity("parent")
                    .Start()
                    .GenerateW3CContext();

                parent.SetTraceState("some=state");

                var url = new Uri(localhostUrl);
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                using (new LocalServer(localhostUrl))
                {
                    await new HttpClient().SendAsync(request);
                }

                // DiagnosticSource Response event is fired after SendAsync returns on netcoreapp1.*
                // let's wait until dependency is collected
                Assert.IsTrue(SpinWait.SpinUntil(() => this.sentTelemetry != null, TimeSpan.FromSeconds(1)));

                parent.Stop();

                Assert.AreEqual(2, request.Headers.GetValues(W3CConstants.TraceStateHeader).Count());
                Assert.IsTrue(request.Headers.GetValues(W3CConstants.TraceStateHeader).Contains($"{W3CConstants.ApplicationIdTraceStateField}={expectedAppId}"));
                Assert.IsTrue(request.Headers.GetValues(W3CConstants.TraceStateHeader).Contains("some=state"));
            }
        }

        private void ValidateTelemetryForDiagnosticSource(DependencyTelemetry item, Uri url, HttpRequestMessage request, bool success, string resultCode, bool expectLegacyHeaders)
        {
            Assert.AreEqual(url, item.Data);
            Assert.AreEqual(url.Host, item.Target);
            Assert.AreEqual("GET " + url.AbsolutePath, item.Name);
            Assert.IsTrue(item.Duration > TimeSpan.FromMilliseconds(0), "Duration has to be positive");
            Assert.AreEqual(RemoteDependencyConstants.HTTP, item.Type, "HttpAny has to be dependency kind as it includes http and azure calls");
            Assert.IsTrue(
                item.Timestamp.UtcDateTime < DateTime.UtcNow.AddMilliseconds(20), // DateTime.UtcNow precesion is ~16ms
                "timestamp < now");
            Assert.IsTrue(
                item.Timestamp.UtcDateTime > DateTime.UtcNow.AddSeconds(-5),
                "timestamp > now - 5 sec");

            Assert.AreEqual(resultCode, item.ResultCode);
            Assert.AreEqual(success, item.Success);
            Assert.AreEqual(
                SdkVersionHelper.GetExpectedSdkVersion(typeof(DependencyTrackingTelemetryModule), "rdddsc:"),
                item.Context.GetInternalContext().SdkVersion);

            var requestId = item.Id;
            Assert.IsTrue(requestId.StartsWith('|' + item.Context.Operation.Id + '.'));
            if (request != null)
            {
                Assert.AreEqual(requestId, request.Headers.GetValues(RequestResponseHeaders.RequestIdHeader).Single());
                if (expectLegacyHeaders)
                {
                    Assert.AreEqual(item.Context.Operation.Id, request.Headers.GetValues(RequestResponseHeaders.StandardRootIdHeader).Single());
                    Assert.AreEqual(requestId, request.Headers.GetValues(RequestResponseHeaders.StandardParentIdHeader).Single());
                }
                else
                {
                    Assert.IsFalse(request.Headers.Contains(RequestResponseHeaders.StandardRootIdHeader));
                    Assert.IsFalse(request.Headers.Contains(RequestResponseHeaders.StandardParentIdHeader));   
                }
            }

            // Validate the http request was captured
            Assert.IsNotNull(this.request, "Http request was not found within the operation details.");
            var webRequest = this.request as HttpRequestMessage;
            Assert.IsNotNull(webRequest, "Http request was not the expected type.");

            // Validate the http response was captured
            Assert.IsNotNull(this.response, "Http response was not found within the operation details.");
            var webResponse = this.response as HttpResponseMessage;
            Assert.IsNotNull(webResponse, "Http response was not the expected type.");

            // Validate the http response headers were not captured
            Assert.IsNull(this.responseHeaders, "Http response headers were not found within the operation details.");
        }

        private sealed class LocalServer : IDisposable
        {
            private readonly IWebHost host;
            private readonly CancellationTokenSource cts;

            public LocalServer(string url)
            {
                this.cts = new CancellationTokenSource();
                this.host = new WebHostBuilder()
                    .UseKestrel()
                    .UseStartup<Startup>()
                    .UseUrls(url)
                    .Build();

                Task.Run(() => this.host.Run(this.cts.Token));
            }

            public void Dispose()
            {
                this.cts.Cancel(false);
                try
                {
                    this.host.Dispose();
                }
                catch (Exception)
                {
                    // ignored, see https://github.com/aspnet/KestrelHttpServer/issues/1513
                    // Kestrel 2.0.0 should have fix it, but it does not seem important for our tests
                }
            }

            private class Startup
            {
                public void Configure(IApplicationBuilder app)
                {
                    app.Run(async (context) =>
                    {
                        await context.Response.WriteAsync("Hello World!");
                    });
                }
            }
        }
    }
#pragma warning restore 612, 618
}
