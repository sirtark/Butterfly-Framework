namespace Butterfly.DesignPatterns.Behavioral
{
    public interface ICommand<in TContext>
    {
        public void Execute(TContext context);
    }
    public interface ICommand
    {
        public void Execute();
    }
    public interface IAsyncCommand<in TContext>
    {
        public ValueTask ExecuteAsync(TContext context, CancellationToken cancellationToken = default);
    }
    public interface IAsyncCommand
    {
        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default);
    }
}