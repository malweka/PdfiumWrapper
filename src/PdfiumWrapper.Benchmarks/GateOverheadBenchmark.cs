using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;

namespace PdfiumWrapper.Benchmarks;

/// <summary>
/// The two <see cref="SmallDocumentBenchmark"/> operations with median and P95 reported, for
/// comparison against the <c>bench-baseline-pre-gate</c> run, plus the bare cost of one
/// uncontended gate entry.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(PercentileColumns))]
public class GateOverheadBenchmark : SmallDocumentBenchmark
{
    private sealed class PercentileColumns : ManualConfig
    {
        public PercentileColumns()
        {
            AddColumn(StatisticColumn.Median, StatisticColumn.P95);
        }
    }

    public override void Setup()
    {
        base.Setup();

        // Initialize the native library so EnterExit measures the steady-state path.
        using var _ = PdfiumRuntime.Enter();
    }

    [Benchmark]
    public void EnterExit()
    {
        using var _ = PdfiumRuntime.Enter();
    }
}
