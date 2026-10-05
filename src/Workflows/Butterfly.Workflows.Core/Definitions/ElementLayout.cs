namespace Butterfly.Workflows.Definitions
{
    // Position and size for visual editors; the engine ignores it.
    public sealed class ElementLayout
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public bool Collapsed { get; set; }
    }
}
