namespace Butterfly.DesignPatterns.Behavioral
{
    public interface IHandler<in TRequest, out TResult>
    {
        public TResult Handle(TRequest request);
    }
    public interface IAsyncHandler<in TRequest, TResult>
    {
        public ValueTask<TResult> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
    }
}