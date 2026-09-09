using System.Runtime.CompilerServices;
using UsageDock.Services;

namespace UsageDock.Tests;

/// <summary>
/// Redirects everything the app writes into a throwaway folder for the duration of a test run.
///
/// Without this, tests share the log and cache of whatever real installation exists on the
/// machine. That is not just untidy: the concurrency tests deliberately provoke write failures,
/// so a test run would fill a real log with alarming errors that have nothing to do with the app
/// as the user is running it.
///
/// A module initializer runs before any test touches AppPaths, which matters because
/// AppPaths.DataDirectory is resolved once and then cached.
/// </summary>
internal static class TestDataDirectory
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "usagedock-testrun-" + Guid.NewGuid().ToString("N"));

        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, directory);
    }
}
