using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DShNative;

/** One plugin entry as the page shows it. */
public sealed record PluginEntry(string Name, string? Version, bool Enabled, bool BuiltIn);

/** What the desktop page bridge can ask the app to do. */
public interface IBridgeHost
{
    /// <summary>State snapshot, serialized to the page.</summary>
    object BuildState();

    Task<object> CheckAsync(CancellationToken ct);
    Task<object> InstallAsync(IProgress<UpdateProgress> progress, CancellationToken ct);
    Task<object> ApplyAsync();
    Task<object> CheckHarnessAsync(CancellationToken ct);
    Task<object> InstallHarnessAsync(CancellationToken ct);
    Task<object> SetPolicyAsync(bool checkOnLaunch);
    Task<object> SetPluginEnabledAsync(string name, bool enabled);

    /// <summary>Installs a plugin package into this home's profile.</summary>
    Task<object> InstallPluginAsync(string spec);

    /// <summary>Removes one.</summary>
    Task<object> RemovePluginAsync(string name);

    /// <summary>Brings the native updates window forward.</summary>
    void OpenNativeWindow();
}

/**
 * The page bridge: `window.__dshDesktop`, injected into every page the window
 * loads, and the request/reply protocol behind it.
 *
 * The app owns the native capabilities (download, verify, staged apply, restart)
 * and the page owns the presentation: a DSH client plugin registers the Updates
 * UI and calls these methods. `version` is part of every message in both
 * directions, so a plugin written against a newer bridge fails politely instead
 * of guessing.
 */
public static class DesktopBridge
{
    /** Bridge protocol version. Bump on an incompatible change. */
    public const int Version = 1;

    private const string BridgeScriptBody = """
        (function () {
          if (window.__dshDesktop && window.__dshDesktop.version) return;

          var VERSION = __VERSION__;
          var pending = new Map();
          var stateListeners = new Set();
          var progressListeners = new Set();
          var nextId = 1;
          var state = null;

          function post(payload) {
            try {
              window.chrome.webview.postMessage(JSON.stringify(payload));
              return true;
            } catch (error) {
              return false;
            }
          }

          function send(method, params, timeoutMs) {
            return new Promise(function (resolve, reject) {
              var id = nextId++;
              if (!post({ dsh: VERSION, id: id, method: method, params: params || {} })) {
                reject(new Error('the desktop bridge is unavailable'));
                return;
              }
              var timer = setTimeout(function () {
                if (pending.has(id)) {
                  pending.delete(id);
                  reject(new Error(method + ' timed out'));
                }
              }, timeoutMs || 600000);
              pending.set(id, {
                resolve: function (value) { clearTimeout(timer); resolve(value); },
                reject: function (error) { clearTimeout(timer); reject(error); }
              });
            });
          }

          function onMessage(event) {
            var data = event && event.data;
            if (typeof data === 'string') {
              try { data = JSON.parse(data); } catch (error) { return; }
            }
            if (!data || data.dsh !== VERSION) return;

            if (data.event === 'state') {
              state = data.state;
              stateListeners.forEach(function (listener) { listener(state); });
              return;
            }
            if (data.event === 'progress') {
              progressListeners.forEach(function (listener) { listener(data.progress); });
              return;
            }

            var entry = pending.get(data.id);
            if (!entry) return;
            pending.delete(data.id);
            if (data.ok) entry.resolve(data.result);
            else entry.reject(new Error(data.error || 'the desktop app reported a failure'));
          }

          try {
            window.chrome.webview.addEventListener('message', onMessage);
          } catch (error) {
            // no host: the API stays present but every call reports it
          }

          /*
            Report page errors and console warnings/errors to the app. A DSH
            plugin that fails in the browser is otherwise invisible: the harness
            host logs nothing and the window just shows less UI than it should.
          */
          (function () {
            function report(level, message, source, line) {
              try {
                post({
                  dsh: VERSION,
                  id: 0,
                  method: 'pageLog',
                  params: {
                    level: level,
                    message: String(message).slice(0, 800),
                    source: String(source || '').slice(0, 200),
                    line: typeof line === 'number' ? line : 0
                  }
                });
              } catch (error) { }
            }
            function describe(value) {
              try {
                return typeof value === 'string' ? value : JSON.stringify(value);
              } catch (error) {
                return String(value);
              }
            }
            window.addEventListener('error', function (event) {
              report('error', (event && event.message) || 'script error', (event && event.filename) || '', event && event.lineno);
            });
            window.addEventListener('unhandledrejection', function (event) {
              var reason = event && event.reason;
              report('error', 'unhandled rejection: ' + describe((reason && reason.message) || reason), '', 0);
            });
            ['warn', 'error'].forEach(function (level) {
              var original = console[level];
              console[level] = function () {
                report(level, Array.prototype.map.call(arguments, describe).join(' '), '', 0);
                return original.apply(console, arguments);
              };
            });
          })();

          window.__dshDesktop = {
            version: VERSION,
            appVersion: '__APP_VERSION__',
            state: function () { return state; },
            check: function () { return send('check'); },
            install: function () { return send('install'); },
            apply: function () { return send('apply', {}, 15000); },
            harnessCheck: function () { return send('harnessCheck'); },
            harnessInstall: function () { return send('harnessInstall'); },
            setPolicy: function (params) { return send('setPolicy', params); },
            setPluginEnabled: function (params) { return send('setPluginEnabled', params); },
            installPlugin: function (params) { return send('installPlugin', params, 900000); },
            removePlugin: function (params) { return send('removePlugin', params, 900000); },
            openNative: function () { return send('open'); },
            subscribe: function (listener) {
              stateListeners.add(listener);
              if (state) { try { listener(state); } catch (error) { } }
              return function () { stateListeners.delete(listener); };
            },
            onProgress: function (listener) {
              progressListeners.add(listener);
              return function () { progressListeners.delete(listener); };
            }
          };
        })();
        """;

    /** The script the app injects before any page code runs. */
    public static string Script(string appVersion)
        => BridgeScriptBody
            .Replace("__VERSION__", Version.ToString(), StringComparison.Ordinal)
            .Replace("__APP_VERSION__", appVersion.Replace("'", ""), StringComparison.Ordinal);

    /** One parsed request from the page. */
    public sealed record Request(int Id, string Method, JsonElement Params);

    /** Parses a page message; null when it is not a bridge request this version handles. */
    public static Request? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("dsh", out var version) || version.ValueKind != JsonValueKind.Number) return null;
            if (version.GetInt32() != Version) return null;
            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) return null;
            if (!root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String) return null;

            var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            return new Request(id.GetInt32(), method.GetString() ?? "", parameters);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /**
     * Runs one request. Every failure becomes a reply with `ok: false` and a
     * message, so the page never sees a hung promise.
     */
    public static async Task<string> DispatchAsync(
        Request request,
        IBridgeHost host,
        IProgress<UpdateProgress>? progress,
        CancellationToken ct)
    {
        try
        {
            object? result = request.Method switch
            {
                "state" => host.BuildState(),
                "check" => await host.CheckAsync(ct).ConfigureAwait(false),
                "install" => await host.InstallAsync(progress ?? new Progress<UpdateProgress>(), ct).ConfigureAwait(false),
                "apply" => await host.ApplyAsync().ConfigureAwait(false),
                "harnessCheck" => await host.CheckHarnessAsync(ct).ConfigureAwait(false),
                "harnessInstall" => await host.InstallHarnessAsync(ct).ConfigureAwait(false),
                "setPolicy" => await host.SetPolicyAsync(BoolParam(request.Params, "checkOnLaunch")).ConfigureAwait(false),
                "setPluginEnabled" => await host
                    .SetPluginEnabledAsync(StringParam(request.Params, "name"), BoolParam(request.Params, "enabled"))
                    .ConfigureAwait(false),
                "installPlugin" => await host.InstallPluginAsync(StringParam(request.Params, "spec")).ConfigureAwait(false),
                "removePlugin" => await host.RemovePluginAsync(StringParam(request.Params, "name")).ConfigureAwait(false),
                "pageLog" => PageLog(request.Params),
                "open" => Open(host),
                _ => throw new InvalidOperationException($"unknown bridge method '{request.Method}'"),
            };
            return Reply(request.Id, result, null);
        }
        catch (OperationCanceledException)
        {
            return Reply(request.Id, null, "the window closed before the request finished");
        }
        catch (Exception ex)
        {
            Log.Warn($"bridge method {request.Method} failed: {ex.Message}");
            return Reply(request.Id, null, ex.Message);
        }
    }

    private static object? Open(IBridgeHost host)
    {
        host.OpenNativeWindow();
        return new { opened = true };
    }

    /**
     * A page error or console line, from the injected reporter. Errors and
     * warnings are logged as warnings - they are usually a plugin explaining
     * that it could not do something.
     */
    private static object? PageLog(JsonElement parameters)
    {
        var level = StringParam(parameters, "level");
        var message = StringParam(parameters, "message");
        var source = StringParam(parameters, "source");
        var where = source.Length == 0 ? "" : $" ({source})";

        if (level is "error" or "warn" or "warning")
        {
            Log.Warn($"page {level}: {message}{where}");
        }
        else
        {
            Log.Info($"page {level}: {message}{where}");
        }
        return new { ok = true };
    }

    public static string Reply(int id, object? result, string? error)
        => JsonSerializer.Serialize(new
        {
            dsh = Version,
            id,
            ok = error == null,
            result,
            error,
        });

    public static string StateEvent(object state)
        => JsonSerializer.Serialize(new { dsh = Version, @event = "state", state });

    public static string ProgressEvent(UpdateProgress progress)
        => JsonSerializer.Serialize(new
        {
            dsh = Version,
            @event = "progress",
            progress = new
            {
                phase = progress.Phase,
                received = progress.Received,
                total = progress.Total,
                percent = progress.Percent,
            },
        });

    private static bool BoolParam(JsonElement parameters, string name)
    {
        if (parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty(name, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
                _ => false,
            };
        }
        return false;
    }

    private static string StringParam(JsonElement parameters, string name)
        => parameters.ValueKind == JsonValueKind.Object
           && parameters.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /** The plugin list a state snapshot carries: what this home runs. */
    public static (List<PluginEntry> Entries, bool CanManage) ReadPlugins(string home)
        => (HarnessProfile.ReadPlugins(home), HarnessProfile.CanManagePlugins);

    /**
     * Reads the updates plugin's marker into the problems it reports.
     *
     * The shell drops a rejected slot registration without telling anyone: the
     * entry simply never renders. The plugin records what it registered and what
     * the shell refused in `window.__dshDesktopUpdates`, which is the only
     * account of a browser half that loaded but could not appear.
     *
     * @param marker - the marker as JSON text, as read back from the page.
     * @returns one message per problem, empty when the plugin is intact.
     */
    public static List<string> ReadPluginMarker(string marker)
    {
        var problems = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(marker);
            var root = document.RootElement;

            if (root.TryGetProperty("registered", out var registered)
                && registered.ValueKind == JsonValueKind.Array
                && registered.GetArrayLength() == 0)
            {
                problems.Add("the updates plugin loaded but registered nothing: its UI cannot appear");
            }

            if (root.TryGetProperty("failed", out var failed) && failed.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in failed.EnumerateArray())
                {
                    problems.Add("the updates plugin could not register " + (entry.GetString() ?? "a slot"));
                }
            }
        }
        catch (JsonException ex)
        {
            problems.Add("the updates plugin marker could not be read: " + ex.Message);
        }

        return problems;
    }

    /**
     * --bridge-selftest: exercises the protocol without a browser, which is the
     * only way to check it on a machine where WebView2 cannot start. Exit codes:
     * 0 all checks passed, 1 something failed.
     */
    public static int RunSelfTest()
    {
        var passed = 0;
        var failed = 0;

        void Check(bool ok, string what)
        {
            if (ok)
            {
                passed++;
                Console.WriteLine("  PASS  " + what);
            }
            else
            {
                failed++;
                Console.WriteLine("  FAIL  " + what);
            }
        }

        Console.WriteLine("== desktop bridge self test ==");

        var script = Script(AppInfo.Version);
        Check(script.Contains($"var VERSION = {Version};", StringComparison.Ordinal), "the injected script carries the protocol version");
        Check(!script.Contains("__VERSION__", StringComparison.Ordinal), "no placeholder is left in the injected script");
        Check(script.Contains("window.__dshDesktop", StringComparison.Ordinal), "the script defines window.__dshDesktop");
        Check(script.Contains(AppInfo.Version, StringComparison.Ordinal), "the script reports the app version");

        Check(Parse(null) == null, "a null message is ignored");
        Check(Parse("not json") == null, "malformed json is ignored");
        Check(Parse("{\"dsh\":1}") == null, "a message without a request id is ignored");
        Check(Parse("{\"dsh\":99,\"id\":1,\"method\":\"state\"}") == null, "a newer protocol version is refused, not guessed at");

        var parsed = Parse($"{{\"dsh\":{Version},\"id\":7,\"method\":\"check\",\"params\":{{\"x\":1}}}}");
        Check(parsed is { Id: 7, Method: "check" }, "a well-formed request parses");

        var host = new StubHost();
        var reply = DispatchAsync(parsed!, host, null, CancellationToken.None).GetAwaiter().GetResult();
        Check(reply.Contains("\"ok\":true", StringComparison.Ordinal), "a handled request replies ok");
        Check(reply.Contains("\"id\":7", StringComparison.Ordinal), "the reply carries the request id");
        Check(host.Calls.Contains("check"), "the request reached the host");

        var unknown = Parse($"{{\"dsh\":{Version},\"id\":8,\"method\":\"nonsense\"}}");
        var unknownReply = DispatchAsync(unknown!, host, null, CancellationToken.None).GetAwaiter().GetResult();
        Check(unknownReply.Contains("\"ok\":false", StringComparison.Ordinal), "an unknown method fails politely");
        Check(unknownReply.Contains("nonsense", StringComparison.Ordinal), "the failure names the method");

        var policy = Parse($"{{\"dsh\":{Version},\"id\":9,\"method\":\"setPolicy\",\"params\":{{\"checkOnLaunch\":true}}}}");
        DispatchAsync(policy!, host, null, CancellationToken.None).GetAwaiter().GetResult();
        Check(host.LastPolicy == true, "a boolean parameter arrives as a boolean");

        var plugin = Parse($"{{\"dsh\":{Version},\"id\":10,\"method\":\"setPluginEnabled\",\"params\":{{\"name\":\"dsh-x\",\"enabled\":false}}}}");
        DispatchAsync(plugin!, host, null, CancellationToken.None).GetAwaiter().GetResult();
        Check(host.LastPlugin == ("dsh-x", false), "plugin parameters arrive intact");

        var state = StateEvent(new { app = new { version = "1.2.3" } });
        Check(state.Contains("\"event\":\"state\"", StringComparison.Ordinal), "a state push is tagged as an event");

        var progress = ProgressEvent(new UpdateProgress("downloading", 50, 200));
        Check(progress.Contains("\"percent\":25", StringComparison.Ordinal), "progress carries a percentage");

        Check(ReadPluginMarker("{\"registered\":[\"sidebar.footer.action\"],\"failed\":[]}").Count == 0,
            "a plugin that registered its slots reports nothing wrong");
        var silent = ReadPluginMarker("{\"registered\":[],\"failed\":[]}");
        Check(silent.Count == 1 && silent[0].Contains("registered nothing", StringComparison.Ordinal),
            "a plugin that registered nothing is reported");
        var refused = ReadPluginMarker("{\"registered\":[\"settings.section\"],\"failed\":[\"sidebar.footer.action: list slot requires options.id\"]}");
        Check(refused.Count == 1 && refused[0].Contains("list slot requires options.id", StringComparison.Ordinal),
            "a refused registration is reported with its reason");
        Check(ReadPluginMarker("not json").Count == 1, "an unreadable marker is reported, not thrown");

        Console.WriteLine($"== {passed} passed, {failed} failed ==");
        return failed == 0 ? 0 : 1;
    }

    /** A host that records what it was asked, so the protocol can be tested alone. */
    private sealed class StubHost : IBridgeHost
    {
        public List<string> Calls { get; } = new();
        public bool? LastPolicy { get; private set; }
        public (string Name, bool Enabled)? LastPlugin { get; private set; }

        public object BuildState() => new { app = new { version = AppInfo.Version } };

        public Task<object> CheckAsync(CancellationToken ct)
        {
            Calls.Add("check");
            return Task.FromResult<object>(new { ok = true });
        }

        public Task<object> InstallAsync(IProgress<UpdateProgress> progress, CancellationToken ct)
        {
            Calls.Add("install");
            return Task.FromResult<object>(new { ok = true });
        }

        public Task<object> ApplyAsync()
        {
            Calls.Add("apply");
            return Task.FromResult<object>(new { ok = true });
        }

        public Task<object> CheckHarnessAsync(CancellationToken ct)
        {
            Calls.Add("harnessCheck");
            return Task.FromResult<object>(new { ok = true });
        }

        public Task<object> InstallHarnessAsync(CancellationToken ct)
        {
            Calls.Add("harnessInstall");
            return Task.FromResult<object>(new { ok = true });
        }

        public Task<object> SetPolicyAsync(bool checkOnLaunch)
        {
            LastPolicy = checkOnLaunch;
            return Task.FromResult<object>(new { ok = true });
        }

        public Task<object> SetPluginEnabledAsync(string name, bool enabled)
        {
            LastPlugin = (name, enabled);
            return Task.FromResult<object>(new { ok = true });
        }

        public Task<object> InstallPluginAsync(string spec)
        {
            Calls.Add("installPlugin:" + spec);
            return Task.FromResult<object>(new { ok = true });
        }

        public Task<object> RemovePluginAsync(string name)
        {
            Calls.Add("removePlugin:" + name);
            return Task.FromResult<object>(new { ok = true });
        }

        public void OpenNativeWindow() => Calls.Add("open");
    }
}
