using System.Runtime.CompilerServices;

namespace SPOMover.Core.Tests;

internal static class TestLanguage
{
    /// <summary>The tests check messages in Ukrainian: this also checks that the Ukrainian dictionary is complete.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        // Also for hosts the tests start: they load the language from the environment first.
        Environment.SetEnvironmentVariable(SPOMover.Core.Localization.L.EnvironmentVariable, SPOMover.Core.Localization.L.Ukrainian);
        SPOMover.Core.Localization.L.Set(SPOMover.Core.Localization.L.Ukrainian);
    }
}
