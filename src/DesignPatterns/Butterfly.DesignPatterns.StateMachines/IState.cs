namespace Butterfly.DesignPatterns.StateMachines
{
    public interface IState<TContext>
    {
        public void Enter(TContext context);
        public void Exit(TContext context);
    }
}
