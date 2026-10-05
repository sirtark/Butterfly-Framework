namespace Butterfly.DesignPatterns.StateMachines
{
    public interface ITransition<TContext>
    {
        public bool CanTransition(TContext context);
        public void Execute(TContext context);
    }
}
