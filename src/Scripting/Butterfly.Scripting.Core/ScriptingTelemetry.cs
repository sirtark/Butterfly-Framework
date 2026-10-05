using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Butterfly.Scripting
{
    public static class ScriptingTelemetry
    {
        public const string Name = "Butterfly.Scripting";

        internal static readonly ActivitySource ActivitySource = new(Name);
        internal static readonly Meter Meter = new(Name);
        internal static readonly Counter<long> Invocations = Meter.CreateCounter<long>("butterfly.scripting.invocations");
        internal static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("butterfly.scripting.duration", "ms");
    }
}
