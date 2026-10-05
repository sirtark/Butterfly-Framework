namespace Butterfly.DesignPatterns.Behavioral
{
    public interface IValidator<in T>
    {
        public bool Validate(T value);
    }
}