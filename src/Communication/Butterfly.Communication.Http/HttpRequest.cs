using System.Text;

namespace Butterfly.Communication.Http
{
    public static class HttpMethod
    {
        public const string Get = "GET";
        public const string Head = "HEAD";
        public const string Post = "POST";
        public const string Put = "PUT";
        public const string Patch = "PATCH";
        public const string Delete = "DELETE";
        public const string Options = "OPTIONS";
        public const string Query = "QUERY";
    }

    public sealed class HttpRequest
    {
        private Uri _uri;

        public HttpRequest(string method, Uri uri)
        {
            if (!HttpHeaders.IsToken(method))
                throw new ArgumentException($"'{method}' is not a valid HTTP method.", nameof(method));

            Method = method;
            _uri = Validate(uri);
        }

        public HttpRequest(string method, string uri) : this(method, new Uri(uri, UriKind.Absolute)) { }

        public string Method { get; set; }

        public Uri Uri
        {
            get => _uri;
            set => _uri = Validate(value);
        }

        public HttpHeaders Headers { get; } = new();

        public HttpContent? Content { get; set; }

        public static HttpRequest Get(string uri) => new(HttpMethod.Get, uri);
        public static HttpRequest Head(string uri) => new(HttpMethod.Head, uri);
        public static HttpRequest Delete(string uri) => new(HttpMethod.Delete, uri);
        public static HttpRequest Post(string uri, HttpContent content) => new(HttpMethod.Post, uri) { Content = content };
        public static HttpRequest Put(string uri, HttpContent content) => new(HttpMethod.Put, uri) { Content = content };
        public static HttpRequest Patch(string uri, HttpContent content) => new(HttpMethod.Patch, uri) { Content = content };
        public static HttpRequest Query(string uri, HttpContent content) => new(HttpMethod.Query, uri) { Content = content };

        public HttpRequest SetBasicAuthentication(string userName, string password)
        {
            Headers.Set("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")));
            return this;
        }

        public HttpRequest SetBearerToken(string token)
        {
            Headers.Set("Authorization", "Bearer " + token);
            return this;
        }

        public override string ToString() => $"{Method} {Uri}";

        private static Uri Validate(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);
            if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException($"'{uri}' is not an absolute http or https URI.", nameof(uri));
            return uri;
        }
    }
}
