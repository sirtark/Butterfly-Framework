namespace Butterfly.DesignPatterns.Structural
{
    public interface IWrapper<in TInner, out TResult>
    {
        public TResult Wrap(TInner inner);
    }
}