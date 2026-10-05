using System.Security.Cryptography;
using System.Text;

namespace Butterfly.Communication
{
    /// <summary>SASL mechanisms shared by SMTP, POP3 and IMAP. Every method returns the base64 client response.</summary>
    public static class Sasl
    {
        /// <summary>PLAIN (RFC 4616): authorization id, user name and password separated by NUL.</summary>
        public static string Plain(string userName, string password, string? authorizationId = null)
            => ToBase64($"{authorizationId}\0{userName}\0{password}");

        /// <summary>LOGIN (draft-murchison-sasl-login): the user name and the password, each in its own step.</summary>
        public static string LoginStep(string value) => ToBase64(value);

        /// <summary>XOAUTH2, used by Gmail and Microsoft 365 with an OAuth 2.0 access token.</summary>
        public static string XOAuth2(string userName, string accessToken)
            => ToBase64($"user={userName}\u0001auth=Bearer {accessToken}\u0001\u0001");

        /// <summary>CRAM-MD5 (RFC 2195): answers the server challenge with an HMAC-MD5 of it.</summary>
        public static string CramMd5(string base64Challenge, string userName, string password)
        {
            byte[] challenge = Convert.FromBase64String(base64Challenge);
            byte[] digest = HMACMD5.HashData(Encoding.UTF8.GetBytes(password), challenge);
            return ToBase64($"{userName} {Convert.ToHexStringLower(digest)}");
        }

        public static string ToBase64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        public static string FromBase64(string base64) => Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }
}
