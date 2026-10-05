namespace Butterfly.DesignPatterns.Behavioral
{
    public interface IObserver<in TEvent>
    {
        public void OnEvent(TEvent @event);
    }
    public interface IAsyncObserver<in TEvent>
    {
        public ValueTask OnEventAsync(TEvent @event, CancellationToken cancellationToken = default);
    }
}