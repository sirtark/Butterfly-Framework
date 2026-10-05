namespace Butterfly.DesignPatterns.Creational
{
    public interface IStaticFactory<TSelf, in TArgs, out TResult> where TSelf : IStaticFactory<TSelf, TArgs, TResult>
    {
        public static abstract TResult Create(TArgs args);
    }
    public interface IStaticFactory<TSelf, out TResult> where TSelf : IStaticFactory<TSelf, TResult>
    {
        public static abstract TResult Create();
    }
}