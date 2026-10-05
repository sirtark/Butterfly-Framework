namespace Butterfly.Chrysalis
{
    /// <summary>
    /// Marks an interface as a Chrysalis service. The Butterfly.Serialization source generator describes it (and every type
    /// it uses) at compile time and generates a typed client, <c>{Name}Client</c>, next to it. Members of the types it uses
    /// are chosen and renamed with the Butterfly.Serialization attributes ([Serialize], [DontSerialize]).
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface, Inherited = false)]
    public sealed class ChrysalisServiceAttribute : Attribute
    {
        public ChrysalisServiceAttribute()
        { }
        public ChrysalisServiceAttribute(string name)
        {
            Name = name;
        }

        /// <summary>Service name used by every protocol. Default: the interface name without the leading "I".</summary>
        public string? Name { get; set; }
        /// <summary>Namespace (gRPC package, SOAP target namespace). Default: the CLR namespace in lower case.</summary>
        public string? Namespace { get; set; }
        /// <summary>REST base route. Default: the service name.</summary>
        public string? Route { get; set; }
    }

    /// <summary>Renames an operation or a parameter on the wire.</summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, Inherited = false)]
    public sealed class ChrysalisNameAttribute(string name) : Attribute
    {
        public string Name { get; } = name;
    }

    /// <summary>Leaves a method out of the service.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class ChrysalisIgnoreAttribute : Attribute
    { }

    /// <summary>
    /// REST binding of an operation. The route is relative to the service route and may contain parameters ("products/{id}").
    /// Without it an operation is exposed as POST {service}/{operation} with its parameters in a JSON body.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public abstract class HttpMethodAttribute(string method, string? route) : Attribute
    {
        public string Method { get; } = method;
        public string? Route { get; } = route;
    }

    public sealed class HttpGetAttribute(string? route = null) : HttpMethodAttribute("GET", route);
    public sealed class HttpPostAttribute(string? route = null) : HttpMethodAttribute("POST", route);
    public sealed class HttpPutAttribute(string? route = null) : HttpMethodAttribute("PUT", route);
    public sealed class HttpPatchAttribute(string? route = null) : HttpMethodAttribute("PATCH", route);
    public sealed class HttpDeleteAttribute(string? route = null) : HttpMethodAttribute("DELETE", route);
}
