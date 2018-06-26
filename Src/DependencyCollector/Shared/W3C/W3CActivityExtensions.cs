﻿namespace Microsoft.ApplicationInsights.DependencyCollector.W3C
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;

    /// <summary>
    /// Extends Activity to support W3C distributed tracing standard.
    /// </summary>
    [Obsolete("Not ready for public consumption.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class W3CActivityExtensions
    {
        /// <summary>
        /// Generate new W3C context.
        /// </summary>
        /// <param name="activity">Activity to generate W3C context on.</param>
        /// <returns>The same Activity for chaining.</returns>
        public static Activity GenerateW3CContext(this Activity activity)
        {
            activity.SetTraceId(GenerateTraceId());
            activity.SetSpanId(GenerateSpanId());
            activity.SetVersion(W3CConstants.DefaultVersion);
            activity.SetSampled(W3CConstants.DefaultSampled);
            return activity;
        }

        /// <summary>
        /// Updates context on the Activity based on the W3C Context in the parent Activity.
        /// </summary>
        /// <param name="activity">Activity to update W3C context on.</param>
        /// <returns>The same Activity for chaining.</returns>
        public static Activity UpdateContextFromParent(this Activity activity)
        {
            if (activity != null && activity.Tags.All(t => t.Key != W3CConstants.TraceIdTag))
            {
                if (activity.Parent == null)
                {
                    activity.GenerateW3CContext();
                }
                else
                {
                    foreach (var tag in activity.Parent.Tags)
                    {
                        switch (tag.Key)
                        {
                            case W3CConstants.TraceIdTag:
                                activity.SetTraceId(tag.Value);
                                break;
                            case W3CConstants.SpanIdTag:
                                activity.SetParentSpanId(tag.Value);
                                activity.SetSpanId(GenerateSpanId());
                                break;
                            case W3CConstants.VersionTag:
                                activity.SetVersion(tag.Value);
                                break;
                            case W3CConstants.SampledTag:
                                activity.SetSampled(tag.Value);
                                break;
                            case W3CConstants.TraceStateTag:
                                activity.SetTraceState(tag.Value);
                                break;
                        }
                    }
                }
            }

            return activity;
        }

        /// <summary>
        /// Gets traceparent header value for the Activity or null if there is no W3C context on it.
        /// </summary>
        /// <param name="activity">Activity to read W3C context from.</param>
        /// <returns>traceparent header value.</returns>
        public static string GetTraceParent(this Activity activity)
        {
            string version = null, traceId = null, spanId = null, sampled = null;
            foreach (var tag in activity.Tags)
            {
                switch (tag.Key)
                {
                    case W3CConstants.TraceIdTag:
                        traceId = tag.Value;
                        break;
                    case W3CConstants.SpanIdTag:
                        spanId = tag.Value;
                        break;
                    case W3CConstants.VersionTag:
                        version = tag.Value;
                        break;
                    case W3CConstants.SampledTag:
                        sampled = tag.Value;
                        break;
                }
            }

            if (traceId == null || spanId == null || version == null || sampled == null)
            {
                return null;
            }

            return string.Join("-", version, traceId, spanId, sampled);
        }

        /// <summary>
        /// Intializes W3C context on the Activity from traceparent header value.
        /// </summary>
        /// <param name="activity">Avtivity to set W3C context on.</param>
        /// <param name="value">Valid traceparent header like 00-0af7651916cd43dd8448eb211c80319c-b9c7c989f97918e1-01.</param>
        public static void SetTraceParent(this Activity activity, string value)
        {
            if (value != null)
            {
                var parts = value.Split('-');
                if (parts.Length == 4)
                {
                    activity.SetTraceId(parts[1]);
                    activity.SetParentSpanId(parts[2]);
                    activity.SetVersion(parts[0]);
                    activity.SetSampled(parts[3]);
                    activity.SetSpanId(GenerateSpanId());
                }
            }
        }

        /// <summary>
        /// Gets tracestate header value from the Activity.
        /// </summary>
        /// <param name="activity">Activity to get tracestate from.</param>
        /// <returns>tracestate header value.</returns>
        public static string GetTraceState(this Activity activity) =>
            activity.Tags.FirstOrDefault(t => t.Key == W3CConstants.TraceStateTag).Value;

        /// <summary>
        /// Sets tracestate header value on the Activity.
        /// </summary>
        /// <param name="activity">Activity to set tracestate on.</param>
        /// <param name="value">tracestate header value.</param>
        public static void SetTraceState(this Activity activity, string value) =>
            activity.AddTag(W3CConstants.TraceStateTag, value);

        private static void SetTraceId(this Activity activity, string value) =>
            activity.AddTag(W3CConstants.TraceIdTag, value);

        private static void SetSpanId(this Activity activity, string value) =>
            activity.AddTag(W3CConstants.SpanIdTag, value);

        private static void SetParentSpanId(this Activity activity, string value) =>
            activity.AddTag(W3CConstants.ParentSpanIdTag, value);

        private static void SetVersion(this Activity activity, string value) =>
            activity.AddTag(W3CConstants.VersionTag, value);

        private static void SetSampled(this Activity activity, string value) =>
            activity.AddTag(W3CConstants.SampledTag, value);

        private static string GenerateSpanId()
        {
            // inefficient
            return BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 8).ToString("x", CultureInfo.InvariantCulture);
        }

        private static string GenerateTraceId()
        {
            // inefficient
            return GenerateSpanId() + GenerateSpanId();
        }
    }
}
