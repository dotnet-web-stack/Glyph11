using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Glyph11.Native;

/// <summary>
/// Resolves the native <c>glyph11</c> library from the <c>GLYPH11_NATIVE_PATH</c>
/// environment variable when set (an explicit path to libglyph11.so/.dll/.dylib),
/// falling back to the default OS search otherwise. Lets tests and benchmarks point
/// at a freshly-built library without installing it.
/// </summary>
internal static class NativeResolver
{
    [ModuleInitializer]
    internal static void Init()
        => NativeLibrary.SetDllImportResolver(typeof(Glyph11Parser).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name == "glyph11")
        {
            var path = Environment.GetEnvironmentVariable("GLYPH11_NATIVE_PATH");
            if (!string.IsNullOrEmpty(path) && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        }
        return IntPtr.Zero; // fall back to default resolution
    }
}
