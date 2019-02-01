using BenchmarkDotNet.Diagnostics.Windows;
using BenchmarkDotNet.Diagnostics.Windows.Tracing;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BenchmarkDotNet.Diagnosers
{
    public class DetailedMemoryDiagnoser : EtwDiagnoserBase, IDiagnoser
    {
        private const string DiagnoserId = nameof(DetailedMemoryDiagnoser);

        public static readonly DetailedMemoryDiagnoser Default = new DetailedMemoryDiagnoser();

        private readonly Dictionary<BenchmarkCase, AllocationTracker> results = new Dictionary<BenchmarkCase, AllocationTracker>();

        public IEnumerable<string> Ids => new[] { DiagnoserId };

        protected override ulong EventType => (ulong)(
            ClrTraceEventParser.Keywords.GCAllObjectAllocation |
            ClrTraceEventParser.Keywords.GCHeapAndTypeNames |
            ClrTraceEventParser.Keywords.Type);

        protected override string SessionNamePrefix => "Memory";

        public void DisplayResults(ILogger logger)
        {
            logger.WriteLine("DetailedMemoryDiagnoser results:");
            foreach (var benchmark in results)
            {
                logger.WriteLine($"{benchmark.Key.DisplayInfo} allocations:");
                foreach (var item in benchmark.Value.GetAllocations())
                {
                    logger.WriteLine($"{item.name}, {item.size}, {item.count}");
                }
            }

            var x = new StringBuilder();
            foreach (var item in Logger.CapturedOutput)
            {
                x.Append(item.Text);
            }
        }

        public bool isActive = false;

        public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
        {
            switch (signal)
            {
                case HostSignal.BeforeProcessStart:
                    Start(parameters); // GCAllObjectAllocation must be enabled before process starts
                    break;
                case HostSignal.BeforeAnythingElse:
                    Session.DisableProvider(ClrTraceEventParser.ProviderGuid); // it can be disabled until the benchmark runs.
                    break;
                case HostSignal.BeforeActualRun:
                    EnableProvider(parameters); // Re-enable allocation tracking now
                    break;
                case HostSignal.AfterActualRun:
                    Stop();
                    break;
                case HostSignal.AfterAll:
                    break;
                case HostSignal.SeparateLogic:
                    break;
                case HostSignal.AfterProcessExit:
                    break;
                default:
                    break;
            }
        }

        public IEnumerable<Metric> ProcessResults(DiagnoserResults results) => Array.Empty<Metric>();
        public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters) => Array.Empty<ValidationError>();

        protected override void AttachToEvents(TraceEventSession session, BenchmarkCase benchmarkCase)
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.ImageLoad);

            var tracker = results[benchmarkCase] = new AllocationTracker();
            Logger.WriteLine();
            Logger.WriteLineInfo($"{benchmarkCase.DisplayInfo}");
            var relogger = new ETWReloggerTraceEventSource(session.SessionName, TraceEventSourceType.Session, @"C:\temp\output.etl");
            relogger.AllEvents += x => relogger.WriteEvent(x);

            Task.Run(() => relogger.Process());
            session.EnableProvider(EngineEventSource.Log.Name, TraceEventLevel.Informational);

            var bdnParser = new EngineEventLogParser(session.Source);
            bdnParser.WorkloadActualStart += _ => { isActive = true; };
            bdnParser.WorkloadActualStop += _ => { isActive = false; }; // stop tracking allocations from outside the benchmark

            session.Source.Clr.GCSampledObjectAllocation += x =>
            {
                if (isActive) tracker.Add(x.TypeID, (ulong)x.TotalSizeForTypeSample);
            };

            session.Source.Clr.TypeBulkType += data =>
            {
                for (int i = 0; i < data.Count; i++)
                {
                    tracker.AddTypeName(data.Values(i).TypeID, data.Values(i).TypeName);
                }
            };
        }

        private class AllocationTracker
        {
            private readonly Dictionary<ulong, string> typeNames = new Dictionary<ulong, string>();
            private Dictionary<ulong, (int count, ulong size)> counters = new Dictionary<ulong, (int count, ulong size)>();

            internal void Add(ulong typeID, ulong totalSizeForTypeSample)
            {
                if (!counters.TryGetValue(typeID, out var value))
                {
                    value = (0, 0);
                }

                var (count, size) = value;
                counters[typeID] = (count + 1, size + totalSizeForTypeSample);
            }

            internal void AddTypeName(ulong typeID, string typeName)
            {
                typeNames[typeID] = typeName;
            }

            internal IEnumerable<(string name, int count, ulong size)> GetAllocations()
            {
                foreach (var item in counters.OrderByDescending(x => x.Value.size))
                {
                    yield return (typeNames[item.Key], item.Value.count, item.Value.size);
                }
            }
        }
    }
}