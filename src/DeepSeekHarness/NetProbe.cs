using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;


namespace DShNative;

public enum EndpointStatus
{
    /** Nothing is listening at the requested endpoint. */
    Free,
    /** A listener exists, but its HTTP response is still settling. */
    Starting,
    /** The root document identifies the DeepSeek Harness web app. */
    DshReady,
    /** A listener exists but is not the DeepSeek Harness web app. */
    Occupied,
}

public sealed record EndpointProbe(EndpointStatus Status, int? HttpStatus, string? Detail);

/** Bounded endpoint identity probes; an open port alone is never attachable. */
public static class NetProbe
{
    /**
     * The harness injects its client entry graph into the served root document
     * as globalThis["__DSH_BOOT__"] (see packages/host/webserver). Match the
     * bare global name so the check survives the property-access spelling.
     */
    public const string DshBootstrapMarker = "__DSH_BOOT__";

    public static bool IsOpen(string host, int port, int timeoutMs = 600)
    {
        using var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync(host, port);
            if (!connect.Wait(timeoutMs)) return false;
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(4),
        MaxResponseContentBufferSize = 256 * 1024,
    };

    public static bool IsHttp200(string url)
    {
        try
        {
            using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /**
     * Classifies one endpoint without exposing arbitrary local HTTP services in
     * the embedded browser. Transient HTTP failures remain Starting so a dsh
     * process can finish composing its loader tree.
     */
    public static EndpointProbe Probe(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Host.Length == 0
            || uri.Port <= 0)
        {
            return new EndpointProbe(EndpointStatus.Occupied, null, "invalid endpoint URL");
        }

        if (!IsOpen(uri.Host, uri.Port))
        {
            return new EndpointProbe(EndpointStatus.Free, null, null);
        }

        try
        {
            using var response = Http.GetAsync(url).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode && body.Contains(DshBootstrapMarker, StringComparison.Ordinal))
            {
                return new EndpointProbe(EndpointStatus.DshReady, (int)response.StatusCode, null);
            }

            return new EndpointProbe(
                EndpointStatus.Occupied,
                (int)response.StatusCode,
                $"HTTP {(int)response.StatusCode} did not contain the DSH bootstrap marker");
        }
        catch (HttpRequestException ex)
        {
            return new EndpointProbe(EndpointStatus.Starting, null, ex.GetType().Name);
        }
        catch (TaskCanceledException)
        {
            return new EndpointProbe(EndpointStatus.Starting, null, "HTTP probe timed out");
        }
        catch (Exception ex)
        {
            return new EndpointProbe(EndpointStatus.Starting, null, ex.GetType().Name);
        }
    }
}
