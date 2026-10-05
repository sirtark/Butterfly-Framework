namespace Butterfly.DesignPatterns.Creational
{
    public interface IFactory<in TArgs, out TResult>
    {
        public TResult Create(TArgs args);
    }
    public interface IFactory<out TResult>
    {
        public TResult Create();
    }
}