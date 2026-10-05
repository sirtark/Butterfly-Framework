namespace Butterfly.Workflows.Components
{
    // Registers the class under this type for every component contract it implements, when its assembly is scanned.
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class WorkflowComponentAttribute(string type) : Attribute
    {
        public string Type { get; } = type;
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public Type? SettingsType { get; set; }
    }
}
