using System.Runtime.InteropServices;
using System.Text;

namespace Butterfly.SystemInfo.Native
{
    // sysctlbyname: how macOS and FreeBSD expose hardware and kernel information.
    // Every getter returns null when the name does not exist on the running kernel.
    internal static unsafe partial class Sysctl
    {
        [LibraryImport("libc", EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int SysctlByName(string name, void* oldValue, nuint* oldLength, void* newValue, nuint newLength);

        public static byte[]? GetBytes(string name)
        {
            if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
                return null;

            try
            {
                nuint length = 0;
                if (SysctlByName(name, null, &length, null, 0) != 0 || length == 0)
                    return null;

                var buffer = new byte[(int)length];
                fixed (byte* pointer = buffer)
                {
                    if (SysctlByName(name, pointer, &length, null, 0) != 0)
                        return null;
                }
                return (int)length == buffer.Length ? buffer : buffer[..(int)length];
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        // Integer values are int, u_int, long or uint64_t depending on the name.
        public static long? GetInt64(string name)
        {
            var bytes = GetBytes(name);
            if (bytes is null)
                return null;

            return bytes.Length switch
            {
                4 => BitConverter.ToInt32(bytes),
                8 => BitConverter.ToInt64(bytes),
                _ => null
            };
        }

        public static string? GetString(string name)
        {
            var bytes = GetBytes(name);
            if (bytes is null)
                return null;

            var end = Array.IndexOf(bytes, (byte)0);
            var text = Encoding.UTF8.GetString(bytes, 0, end >= 0 ? end : bytes.Length).Trim();
            return text.Length > 0 ? text : null;
        }
    }
}
