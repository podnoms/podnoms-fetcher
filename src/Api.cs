using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PodNoms.Fetcher;

/// <summary>
/// The HTTP API PodNoms calls. Jobs answer with newline-delimited JSON: a
/// message per yt-dlp output line ({ line }), then exactly one of { end },
/// { file } (a download, to collect from /files/:id) or { error, rateLimited }.
/// </summary>
public static partial class Api {
  // As JSON.stringify writes it: camelCase, and titles left readable.
  private static readonly FetcherJson Json = new(new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

  // yt-dlp's --limit-rate, e.g. 2M or 500K.
  [GeneratedRegex(@"\A[0-9]+(\.[0-9]+)?[KMG]?\z", RegexOptions.IgnoreCase)]
  private static partial Regex LimitRate();

  public static WebApplication UseFetcherApi(this WebApplication app) {
    var secret = Encoding.UTF8.GetBytes(app.Services.GetRequiredService<FetcherOptions>().Secret);
    app.Use(async (context, next) => {
      var header = context.Request.Headers.Authorization.ToString();
      if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
          && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[7..].Trim()), secret)) {
        await next(context);
        return;
      }
      context.Response.StatusCode = StatusCodes.Status401Unauthorized;
      context.Response.Headers.WWWAuthenticate = "Bearer";
      await context.Response.WriteAsync("Unauthorized");
    });

    app.MapGet("/health", async (YtDlp ytDlp, Vpn vpn) =>
        Results.Json(new Health(await ytDlp.VersionAsync(), vpn.PublicIp), Json.Health));

    app.MapPost("/list", async (HttpContext context, Jobs jobs) => {
      using var body = await ReadBodyAsync(context);
      if (body is null || Link(body) is not { } url || !TryLimit(body, out var limit)) return Problem(400, "Expected { url, limit }");
      return new JobStream((send, cancel) => jobs.ListAsync(url, limit, send, cancel));
    });

    app.MapPost("/download", async (HttpContext context, Jobs jobs) => {
      using var body = await ReadBodyAsync(context);
      if (body is null || Link(body) is not { } url || !TryLimitRate(body, out var limitRate)) return Problem(400, "Expected { url, limitRate? }");
      return new JobStream((send, cancel) => jobs.DownloadAsync(url, limitRate, send, cancel));
    });

    app.MapGet("/files/{id}", (string id, DownloadStore store) =>
        IsFileId(id) && store.PathOf(id) is { } path && File.Exists(path)
            ? Results.File(path, "application/octet-stream")
            : NoSuchFile());

    app.MapDelete("/files/{id}", (string id, DownloadStore store) =>
        IsFileId(id) && store.Remove(id) ? Results.NoContent() : NoSuchFile());

    return app;
  }

  private static IResult Problem(int status, string error) => Results.Json(new Refusal(error), Json.Refusal, statusCode: status);

  private static IResult NoSuchFile() => Problem(404, "No such file");

  // Ids are the UUIDs downloads are given, so never a path.
  private static bool IsFileId(string id) => Guid.TryParseExact(id, "D", out _);

  // The request's JSON object, or null if it isn't one.
  private static async Task<JsonDocument?> ReadBodyAsync(HttpContext context) {
    try {
      var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
      if (body.RootElement.ValueKind == JsonValueKind.Object) return body;
      body.Dispose();
    } catch (JsonException) {
    }
    return null;
  }

  // The body's url, if it's an http or https link.
  private static string? Link(JsonDocument body) =>
      body.RootElement.TryGetProperty("url", out var value)
      && value.ValueKind == JsonValueKind.String
      && value.GetString() is { } url
      && Uri.TryCreate(url, UriKind.Absolute, out var uri)
      && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
          ? url
          : null;

  // limit must be there: a whole number above zero, or null for everything.
  private static bool TryLimit(JsonDocument body, out int? limit) {
    limit = null;
    if (!body.RootElement.TryGetProperty("limit", out var value)) return false;
    if (value.ValueKind == JsonValueKind.Null) return true;
    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)) return false;
    if (number < 1 || number > int.MaxValue || number != Math.Floor(number)) return false;
    limit = (int)number;
    return true;
  }

  private static bool TryLimitRate(JsonDocument body, out string? limitRate) {
    limitRate = null;
    if (!body.RootElement.TryGetProperty("limitRate", out var value)) return true;
    if (value.ValueKind != JsonValueKind.String || !LimitRate().IsMatch(value.GetString()!)) return false;
    limitRate = value.GetString();
    return true;
  }

  /// <summary>A job's messages, each written (and sent) as it happens.</summary>
  private sealed class JobStream(Func<Send, CancellationToken, Task> run) : IResult {
    public async Task ExecuteAsync(HttpContext context) {
      // Cancelled when the caller hangs up, which stops the job.
      var gone = context.RequestAborted;
      var response = context.Response;
      response.ContentType = "application/x-ndjson";
      response.Headers.CacheControl = "no-store";

      async ValueTask Send(Message message) {
        if (gone.IsCancellationRequested) return;
        try {
          var json = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Json);
          await response.Body.WriteAsync((byte[])[.. json, (byte)'\n'], gone);
          await response.Body.FlushAsync(gone);
        } catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) {
          // The caller has gone; the job is stopped by the token.
        }
      }

      try {
        // Jobs can be a long time starting (behind a yt-dlp update); answer now.
        await response.StartAsync(gone);
        await run(Send, gone);
      } catch (OperationCanceledException) when (gone.IsCancellationRequested) {
      } catch (Exception error) {
        context.RequestServices.GetRequiredService<ILogger<Jobs>>().LogError(error, "Job failed");
        await Send(new ErrorMessage(error.Message, false));
      }
    }
  }
}

public sealed record Health(string? YtDlpVersion, string? PublicIp);

/// <summary>The body of a 400 or 404.</summary>
public sealed record Refusal(string Error);

/// <summary>
/// Everything written as JSON, serialised by generated code as the fetcher is
/// compiled ahead of time (no reflection). Anything new must be listed here.
/// </summary>
[JsonSerializable(typeof(Health))]
[JsonSerializable(typeof(Refusal))]
[JsonSerializable(typeof(LineMessage))]
[JsonSerializable(typeof(EndMessage))]
[JsonSerializable(typeof(FileMessage))]
[JsonSerializable(typeof(ErrorMessage))]
public sealed partial class FetcherJson : JsonSerializerContext;
