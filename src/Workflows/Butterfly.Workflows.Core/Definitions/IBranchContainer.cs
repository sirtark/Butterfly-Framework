namespace Butterfly.Workflows.Definitions
{
    public interface IBranchContainer
    {
        string Id { get; }
        List<BranchDefinition> Branches { get; }
    }
}
