namespace Butterfly.DesignPatterns.Structural
{
    public interface IDecorator<out T>
    {
        public T Inner { get; }
    }
}