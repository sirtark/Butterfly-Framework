namespace Butterfly.DesignPatterns.Behavioral
{
    public interface IStrategy<in TInput, out TResult>
    {
        public TResult Execute(TInput input);
    }
}