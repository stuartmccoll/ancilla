using System.Globalization;
using System.Runtime.CompilerServices;

namespace Ancilla.UI.Tests;

internal static class TestCulture
{
    /// <summary>Formatted numbers and dates read the same on every machine and CI runner.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-GB");
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-GB");
    }
}
