namespace Butterfly.DesignPatterns.Pipelines
{
    public interface IPipeline<TContext, TResult>
    {
        public TResult Execute(TContext context);
    }
}