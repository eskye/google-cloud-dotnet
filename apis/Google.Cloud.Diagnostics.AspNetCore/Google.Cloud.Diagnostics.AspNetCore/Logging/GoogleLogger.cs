﻿// Copyright 2018 Google Inc. All Rights Reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Google.Api.Gax;
using Google.Cloud.Diagnostics.Common;
using Google.Cloud.Logging.V2;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Internal;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Google.Cloud.Diagnostics.AspNetCore
{
    /// <summary>
    /// <see cref="ILogger"/> for Google Stackdriver Logging.
    /// </summary>
    ///
    /// <example>
    /// <code>
    /// public void Configure(ILoggerFactory loggerFactory)
    /// {
    ///     string projectId = "[Google Cloud Platform project ID]";
    ///     loggerFactory.AddGoogle(projectId);
    ///     ...
    /// }
    /// </code>
    /// </example>
    ///
    /// <remarks>
    /// Logs to Google Stackdriver Cloud Logging.
    /// Docs: https://cloud.google.com/logging/docs/
    /// </remarks>
    /// <seealso cref="GoogleLoggerFactoryExtensions"/>
    public sealed class GoogleLogger : ILogger
    {
        private const string GcpConsoleLogsBaseUrl = "https://console.cloud.google.com/logs/viewer";

        /// <summary>The log name given when creating the logger.</summary>
        private readonly string _logName;

        /// <summary>The consumer to push logs to.</summary>
        private readonly IConsumer<LogEntry> _consumer;

        /// <summary>The trace target or null if non exists.</summary>
        private readonly TraceTarget _traceTarget;

        /// <summary>
        /// The log target, indicating mainly if the target is a project or an organization.
        /// </summary>
        private readonly LogTarget _logTarget;

        /// <summary>The logger options.</summary>
        private readonly LoggerOptions _loggerOptions;

        /// <summary>The formatted log name.</summary>
        private readonly string _fullLogName;

        /// <summary>A clock for getting the current timestamp.</summary>
        private readonly IClock _clock;

        /// <summary>The service provider to resolve additional services from.</summary>
        private readonly IServiceProvider _serviceProvider;

        internal GoogleLogger(IConsumer<LogEntry> consumer, LogTarget logTarget, LoggerOptions loggerOptions,
            string logName, IClock clock = null, IServiceProvider serviceProvider = null)
        {
            _logTarget = GaxPreconditions.CheckNotNull(logTarget, nameof(logTarget));
            _traceTarget = logTarget.Kind == LogTargetKind.Project ?
                TraceTarget.ForProject(logTarget.ProjectId) : null;
            _consumer = GaxPreconditions.CheckNotNull(consumer, nameof(consumer));
            _loggerOptions = GaxPreconditions.CheckNotNull(loggerOptions, nameof(loggerOptions));
            _logName = GaxPreconditions.CheckNotNullOrEmpty(logName, nameof(logName));
            _fullLogName = logTarget.GetFullLogName(_loggerOptions.LogName);
            _serviceProvider = serviceProvider;
            _clock = clock ?? SystemClock.Instance;

            WriteCreationDiagnostics();
        }

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state) => new GoogleLoggerScope(state);

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => logLevel >= _loggerOptions.LogLevel;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            try
            {
                GaxPreconditions.CheckNotNull(formatter, nameof(formatter));

                if (!IsEnabled(logLevel))
                {
                    return;
                }

                string message = formatter(state, exception);
                if (string.IsNullOrEmpty(message))
                {
                    return;
                }

                var jsonStruct = new Struct();
                jsonStruct.Fields.Add("message", Value.ForString(message));
                jsonStruct.Fields.Add("log_name", Value.ForString(_logName));

                if (eventId.Id != 0 || eventId.Name != null)
                {
                    var eventStruct = new Struct();
                    if (eventId.Id != 0)
                    {
                        eventStruct.Fields.Add("id", Value.ForNumber(eventId.Id));
                    }
                    if (!string.IsNullOrWhiteSpace(eventId.Name))
                    {
                        eventStruct.Fields.Add("name", Value.ForString(eventId.Name));
                    }
                    jsonStruct.Fields.Add("event_id", Value.ForStruct(eventStruct));
                }

                // If we have format params and its more than just the original message add them.
                if (state is IEnumerable<KeyValuePair<string, object>> formatParams &&
                    !(formatParams.Count() == 1 && formatParams.Single().Key.Equals("{OriginalFormat}")))
                {
                    var paramStruct = new Struct();
                    foreach (var pair in formatParams)
                    {
                        // Consider adding formatting support for values that are IFormattable.
                        paramStruct.Fields[pair.Key] = Value.ForString(pair.Value?.ToString() ?? "");
                    }

                    if (paramStruct.Fields.Count > 0)
                    {
                        jsonStruct.Fields.Add("format_parameters", Value.ForStruct(paramStruct));
                    }
                }

                var currentLogScope = GoogleLoggerScope.Current;
                if (currentLogScope != null)
                {
                    jsonStruct.Fields.Add("scope", Value.ForString(currentLogScope.ToString()));
                }

                // Create a map of format parameters of all the parent scopes,
                // starting from the most inner scope to the top-level scope.
                var scopeParamsList = new List<Value>();
                while (currentLogScope != null)
                {
                    // Determine if the state of the scope are format params
                    if (currentLogScope.State is FormattedLogValues scopeFormatParams)
                    {
                        var scopeParams = new Struct();
                        foreach (var pair in scopeFormatParams)
                        {
                            scopeParams.Fields[pair.Key] = Value.ForString(pair.Value?.ToString() ?? "");
                        }

                        scopeParamsList.Add(Value.ForStruct(scopeParams));
                    }

                    currentLogScope = currentLogScope.Parent;
                }

                if (scopeParamsList.Count > 0)
                {
                    jsonStruct.Fields.Add("parent_scopes", Value.ForList(scopeParamsList.ToArray()));
                }

                Dictionary<string, string> labels;
                var labelProviders = GetLabelProviders()?.ToArray();
                if (labelProviders?.Length > 0)
                {
                    // Create a copy of the labels from the options and invoke each provider
                    labels = _loggerOptions.Labels.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    foreach (var provider in labelProviders)
                    {
                        provider.Invoke(labels);
                    }
                }
                else
                {
                    labels = _loggerOptions.Labels;
                }

                LogEntry entry = new LogEntry
                {
                    Resource = _loggerOptions.MonitoredResource,
                    LogName = _fullLogName,
                    Severity = logLevel.ToLogSeverity(),
                    Timestamp = Timestamp.FromDateTime(_clock.GetCurrentDateTimeUtc()),
                    JsonPayload = jsonStruct,
                    Labels = { labels },
                    Trace = GetTraceName() ?? "",
                };

                _consumer.Receive(new[] { entry });
            }
            catch (Exception) when (_loggerOptions.RetryOptions.ExceptionHandling == ExceptionHandling.Ignore) { }
        }

        /// <summary>
        /// Gets the full trace name if the log target is a project, we have an
        /// HTTP accessor and a valid trace header exists on the current context.
        /// If the trace name cannot be determined null is returned.
        /// See: See: https://cloud.google.com/logging/docs/reference/v2/rest/v2/LogEntry
        /// </summary>
        internal string GetTraceName()
        {
            var httpContext = _serviceProvider?.GetService<IHttpContextAccessor>()?.HttpContext;
            if (_traceTarget == null || httpContext == null)
            {
                return null;
            }

            string header = httpContext.Request?.Headers[TraceHeaderContext.TraceHeader];
            var traceContext = TraceHeaderContext.FromHeader(header);
            return traceContext.TraceId == null ? null : _traceTarget.GetFullTraceName(traceContext.TraceId);
        }

        /// <summary>
        /// Gets the collection of <see cref="ILogEntryLabelProvider"/>'s
        /// registered in the application <see cref="IServiceProvider"/>.
        /// </summary>
        internal IEnumerable<ILogEntryLabelProvider> GetLabelProviders() =>
            _serviceProvider?.GetService<IEnumerable<ILogEntryLabelProvider>>();

        /// <summary>
        /// For diagnostic purposes. Builds and returns the URL where the entries logged by
        /// this <see cref="GoogleLogger"/> can be seen on the GCP Stackdriver Logging Console.
        /// </summary>
        public Uri GetGcpConsoleLogsUrl()
        {
            string target =
                _logTarget.Kind == LogTargetKind.Project ? $"project={_logTarget.ProjectId}" :
                _logTarget.Kind == LogTargetKind.Organization ? $"organizationId={_logTarget.OrganizationId}" :
                throw new InvalidOperationException($"Unrecognized LogTargetKind: {_logTarget.Kind}");

            IList<string> parameters = new List<string>
            {
                $"resource={_loggerOptions.MonitoredResource.Type}",
                $"minLogLevel={(int)_loggerOptions.LogLevel.ToLogSeverity()}",
                $"logName={_fullLogName}",
                target
            };

            return new UriBuilder(GcpConsoleLogsBaseUrl)
            {
                Query = string.Join("&", parameters)
            }.Uri;
        }

        private void WriteCreationDiagnostics()
        {
            // Explicitly not catching exceptions.
            // This should only be activated for diagnostics purposes so in that case
            // we shouldn't throw exceptions.

            _loggerOptions.LoggerDiagnosticsOutput?.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd'T'HH:mm:ss} - GoogleLogger created. Logs written to: {1}", DateTime.UtcNow, GetGcpConsoleLogsUrl()));
            _loggerOptions.LoggerDiagnosticsOutput?.Flush();
        }
    }
}
