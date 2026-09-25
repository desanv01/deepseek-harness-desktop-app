using System;
using System.Collections.Generic;

namespace DShNative;

/**
 * The environment the harness child is started with.
 *
 * The app launches a real Node process, so the ambient environment is part of
 * the harness's configuration whether or not anyone intended it to be. Three
 * variables change how that process resolves and loads code, and all three are
 * routinely set by unrelated tooling on a developer's machine:
 *
 *   NODE_OPTIONS          can inject --require/--import, so it changes what
 *                         runs before the harness does
 *   NODE_PATH             adds fallback directories to bare-specifier
 *                         resolution, which is the mechanism that decides which
 *                         plugin package a name resolves to
 *   NODE_EXTRA_CA_CERTS   replaces the trust anchors for every outbound TLS
 *                         connection the harness makes
 *
 * None of them is a supported way to configure this app, and an inherited one
 * fails in a way that looks like a harness bug rather than a machine
 * misconfiguration. The harness's own process-launching code strips the same
 * set before spawning a child, so removing them here matches the behaviour a
 * plain `dsh web` already has - the app is not inventing a rule, it is
 * declining to lose one.
 *
 * Only these three are removed. Everything else - proxy settings, locale,
 * certificates in the OS store, PATH - is left alone deliberately, because the
 * harness is entitled to inherit the user's machine.
 */
public static class HarnessEnvironment
{
    /** Variables that change code resolution or trust, and are never ours to pass on. */
    public static readonly string[] StrippedVariables =
    {
        "NODE_OPTIONS",
        "NODE_PATH",
        "NODE_EXTRA_CA_CERTS",
    };

    /**
     * Builds the child environment: `DSH_HOME` plus any caller additions, minus
     * the stripped variables.
     */
    public static Dictionary<string, string> Hardened(
        string home,
        IReadOnlyDictionary<string, string>? extra = null,
        IEnumerable<string>? keep = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DSH_HOME"] = home,
        };

        if (extra != null)
        {
            foreach (var entry in extra) environment[entry.Key] = entry.Value;
        }

        // `keep` exists for the caller that deliberately sets one of the
        // stripped names - none does today, but a future one must be able to say
        // so rather than discovering the value silently vanished.
        //
        // Removal has to be an explicit null: ProcessStartInfo.Environment
        // starts as a COPY of this process's environment, so a name that is
        // merely absent from the dictionary would still reach the child.
        var spared = new HashSet<string>(keep ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();
        foreach (var name in StrippedVariables)
        {
            if (spared.Contains(name)) continue;
            var inherited = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(inherited)) continue;
            environment[name] = null!;
            removed.Add(name);
        }

        if (removed.Count > 0)
        {
            Log.Info("dropped inherited " + string.Join(", ", removed) + " from the harness environment");
        }

        return environment;
    }
}
