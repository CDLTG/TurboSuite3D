// Guarded so this extension stub drops out on net8 (whose BCL has the matching
// instance method) once Core multi-targets net48;net8.0-windows.
#if !NET5_0_OR_GREATER
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TurboSuite.Compatibility
{
    /// <summary>
    /// Extension-method polyfill for <see cref="HttpContent.ReadAsByteArrayAsync()"/>'s
    /// CancellationToken overload, which exists on .NET 5+ but NOT on .NET Framework 4.8.
    /// Lets TurboSuite.Core (and the future net48 Revit2024 shim) call
    /// <c>content.ReadAsByteArrayAsync(ct)</c> with zero call-site changes.
    ///
    /// On net8 the BCL provides the matching *instance* method, which always wins overload
    /// resolution over an extension method — so this stub is inert on net8 and only
    /// activates on net48. The token cannot be honored mid-read on net48 (no underlying
    /// support), so it is accepted and ignored; the request-level token passed to
    /// <c>HttpClient.GetAsync(url, ct)</c> still governs the actual network wait.
    /// </summary>
    internal static class HttpContentPolyfills
    {
        public static Task<byte[]> ReadAsByteArrayAsync(this HttpContent content, CancellationToken cancellationToken)
            => content.ReadAsByteArrayAsync();
    }
}
#endif
