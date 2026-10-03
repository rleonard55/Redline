using System.Runtime.InteropServices;
using System.Text;

namespace Redline.Analysis.Harper;

/// <summary>
/// P/Invoke surface of native/harper-ffi. Hand-written rather than csbindgen-generated:
/// three functions don't justify a build-time generator, and the ownership rules below
/// are the part that actually needs care.
/// </summary>
internal static partial class HarperInterop
{
    private const string Library = "harper_ffi";

    /// <summary>Returns a Rust-owned UTF-8 JSON string; must be released with <see cref="harper_free"/>.</summary>
    [LibraryImport(Library)]
    private static unsafe partial IntPtr harper_lint(byte* text, nuint length);

    [LibraryImport(Library)]
    private static partial void harper_free(IntPtr ptr);

    /// <summary>Static string owned by the library — never freed.</summary>
    [LibraryImport(Library)]
    private static partial IntPtr harper_ffi_version();

    public static string Version() => Marshal.PtrToStringUTF8(harper_ffi_version()) ?? "unknown";

    /// <summary>Lints <paramref name="text"/> and returns the raw JSON response.</summary>
    public static unsafe string Lint(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        IntPtr result;
        fixed (byte* p = bytes)
        {
            result = harper_lint(p, (nuint)bytes.Length);
        }

        if (result == IntPtr.Zero)
            throw new InvalidOperationException("harper_lint returned null.");

        try
        {
            return Marshal.PtrToStringUTF8(result)
                ?? throw new InvalidOperationException("harper_lint returned an unreadable string.");
        }
        finally
        {
            // Copy is complete; ownership goes back to Rust exactly once.
            harper_free(result);
        }
    }
}
