﻿using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Parameters;
using BenchmarkDotNet.Running;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BenchmarkDotNet.Diagnostics.Windows
{

    public abstract class EtwDiagnoserBase
    {
        internal readonly LogCapture Logger = new LogCapture();
        public virtual RunMode GetRunMode(BenchmarkCase benchmarkCase) => RunMode.ExtraRun;
        public virtual IEnumerable<IExporter> Exporters => Array.Empty<IExporter>();
        public virtual IEnumerable<IAnalyser> Analysers => Array.Empty<IAnalyser>();

        protected TraceEventSession Session { get; private set; }

        protected abstract ulong EventType { get; }

        protected abstract string SessionNamePrefix { get; }

        protected virtual void Start(DiagnoserActionParameters parameters)
        {
            Session = CreateSession(parameters.BenchmarkCase);

            Console.CancelKeyPress += OnConsoleCancelKeyPress;

            NativeWindowsConsoleHelper.OnExit += OnConsoleCancelKeyPress;

            AttachToEvents(Session, parameters.BenchmarkCase);

            EnableProvider(parameters);

            // The ETW collection thread starts receiving events immediately, but we only
            // start aggregating them after ProcessStarted is called and we know which process
            // (or processes) we should be monitoring. Communication between the benchmark thread
            // and the ETW collection thread is through the statsPerProcess concurrent dictionary
            // and through the TraceEventSession class, which is thread-safe.
            var task = Task.Factory.StartNew(() => Session.Source.Process(), TaskCreationOptions.LongRunning);

            // wait until the processing has started, block by then so we don't loose any 
            // information (very important for jit-related things)
            WaitUntilStarted(task);
        }

        protected virtual TraceEventSession CreateSession(BenchmarkCase benchmarkCase)
             => new TraceEventSession(GetSessionName(SessionNamePrefix, benchmarkCase, benchmarkCase.Parameters));

        protected virtual void EnableProvider(DiagnoserActionParameters parameters = null)
        {
            Session.EnableProvider(
                ClrTraceEventParser.ProviderGuid,
                TraceEventLevel.Verbose,
                EventType,
                new TraceEventProviderOptions
                {
                    StacksEnabled = true,
                    ProcessNameFilter = parameters != null
                        ? new List<string> { Path.GetFileName(parameters?.Process.StartInfo.FileName) }
                        : null
                });
        }

        protected abstract void AttachToEvents(TraceEventSession traceEventSession, BenchmarkCase benchmarkCase);

        protected void Stop()
        {
            WaitForDelayedEvents();
            Session.Dispose();

            Console.CancelKeyPress -= OnConsoleCancelKeyPress;
            NativeWindowsConsoleHelper.OnExit -= OnConsoleCancelKeyPress;
        }

        private void OnConsoleCancelKeyPress(object sender, ConsoleCancelEventArgs e) => Session?.Dispose();

        private static string GetSessionName(string prefix, BenchmarkCase benchmarkCase, ParameterInstances parameters = null)
        {
            if (parameters != null && parameters.Items.Count > 0)
                return $"{prefix}-{benchmarkCase.FolderInfo}-{parameters.FolderInfo}";
            return $"{prefix}-{benchmarkCase.FolderInfo}";
        }

        private static void WaitUntilStarted(Task task)
        {
            while (task.Status == TaskStatus.Created
                || task.Status == TaskStatus.WaitingForActivation
                || task.Status == TaskStatus.WaitingToRun)
            {
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// ETW real-time sessions receive events with a slight delay. Typically it
        /// shouldn't be more than a few seconds. This increases the likelihood that
        /// all relevant events are processed by the collection thread by the time we
        /// are done with the benchmark.
        /// </summary>
        private static void WaitForDelayedEvents()
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));
        }
    }

    public abstract class EtwDiagnoser<TStats> : EtwDiagnoserBase where TStats : new()
    {
        protected readonly Dictionary<BenchmarkCase, int> BenchmarkToProcess = new Dictionary<BenchmarkCase, int>();
        protected readonly ConcurrentDictionary<int, TStats> StatsPerProcess = new ConcurrentDictionary<int, TStats>();

        protected override void Start(DiagnoserActionParameters parameters)
        {
            Clear();

            BenchmarkToProcess.Add(parameters.BenchmarkCase, parameters.Process.Id);
            StatsPerProcess.TryAdd(parameters.Process.Id, GetInitializedStats(parameters));

            base.Start(parameters);
        }

        protected virtual TStats GetInitializedStats(DiagnoserActionParameters parameters) => new TStats();

        private void Clear()
        {
            BenchmarkToProcess.Clear();
            StatsPerProcess.Clear();
        }
    }
}
