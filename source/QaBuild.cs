namespace NocturneFlatScroll;

/// <summary>
/// The in-game QA hooks (the NFS_QA_* environment variables) work only in a QA build, which
/// defines NFS_QA. A release build never reads those variables, so they can't change it.
/// </summary>
internal static class QaBuild
{
#if NFS_QA
    internal const bool On = true;
#else
    internal const bool On = false;
#endif

    /// <summary>An NFS_QA_* environment variable in a QA build; null in a release build.</summary>
    internal static string? Env(string name) => On ? Environment.GetEnvironmentVariable(name) : null;
}
