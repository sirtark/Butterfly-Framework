namespace Butterfly.DesignPatterns.Creational
{
    public interface ISingleton<TSelf> : ISingleton where TSelf : ISingleton<TSelf>
    {
        public new static TSelf Instance => (TSelf)TSelf.Instance;
    }
    public interface ISingleton
    {
        public abstract static object Instance { get; }
    }
}