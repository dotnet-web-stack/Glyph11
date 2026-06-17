using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Glyph11.Pico;

/// <summary>
/// Resolves the native <c>glyph11pico</c> library from the <c>GLYPH11_PICO_NATIVE_PATH</c>
/// environment variable when set (an explicit path to libglyph11pico.so/.dll/.dylib),
/// falling back to the default OS search otherwise.
/// </summary>
internal static class NativeResolver
{
    [ModuleInitializer]
    internal static void Init()
        => NativeLibrary.SetDllImportResolver(typeof(PicoNative).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name == "glyph11pico")
        {
            var path = Environment.GetEnvironmentVariable("GLYPH11_PICO_NATIVE_PATH");
            if (!string.IsNullOrEmpty(path) && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        }
        return IntPtr.Zero; // fall back to default resolution
    }
}
