using System.Runtime.CompilerServices;

public static class FluentAssertionsConfig
{
    [ModuleInitializer]
    public static void DisableLicenseBanner()
    {
        FluentAssertions.License.Accepted = true;
    }
}