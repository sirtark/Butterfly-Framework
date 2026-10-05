namespace Butterfly.Workflows.Definitions
{
    // Validations run before anything moves; if any fails the whole operation is rejected. Steps run in order afterwards.
    public sealed class PipelineDefinition
    {
        public List<ValidationDefinition> Validations { get; set; } = [];
        public List<StepDefinition> Steps { get; set; } = [];
    }

    public static class PipelineStages
    {
        public const string Enter = "enter";
        public const string Exit = "exit";
        public const string Fault = "fault";
    }
}
