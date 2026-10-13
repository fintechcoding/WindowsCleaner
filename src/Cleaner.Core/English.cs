using System.Globalization;
using System.Runtime.InteropServices;

namespace Cleaner.Core;

public static class English
{
    public static void Initialize()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        SetThreadUILanguage(0x0409);
    }
    [DllImport("kernel32.dll")] private static extern ushort SetThreadUILanguage(ushort language);
}
