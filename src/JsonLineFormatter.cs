using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace PodNoms.Fetcher;

/// <summary>
/// One JSON object per line on stdout, as PodNoms logs in production:
/// level, time, app, the message's named values, err and msg.
/// </summary>
public sealed class JsonLineFormatter() : ConsoleFormatter(FormatterName) {
  public const string FormatterName = "json-line";

  private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

  public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter output) {
    using var buffer = new MemoryStream();
    using (var json = new Utf8JsonWriter(buffer, Options)) {
      json.WriteStartObject();
      json.WriteString("level", Level(entry.LogLevel));
      json.WriteString("time", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
      json.WriteString("app", "podnoms-fetcher");
      if (entry.State is IEnumerable<KeyValuePair<string, object?>> fields) {
        foreach (var (name, value) in fields) {
          if (name == "{OriginalFormat}") continue;
          json.WritePropertyName(name);
          WriteValue(json, value);
        }
      }
      if (entry.Exception is { } error) {
        json.WriteStartObject("err");
        json.WriteString("type", error.GetType().Name);
        json.WriteString("message", error.Message);
        json.WriteEndObject();
      }
      json.WriteString("msg", entry.Formatter(entry.State, entry.Exception));
      json.WriteEndObject();
    }
    output.WriteLine(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
  }

  private static string Level(LogLevel level) => level switch {
    LogLevel.Trace => "trace",
    LogLevel.Debug => "debug",
    LogLevel.Information => "info",
    LogLevel.Warning => "warn",
    LogLevel.Error => "error",
    _ => "fatal",
  };

  private static void WriteValue(Utf8JsonWriter json, object? value) {
    switch (value) {
      case null: json.WriteNullValue(); break;
      case string text: json.WriteStringValue(text); break;
      case bool flag: json.WriteBooleanValue(flag); break;
      case int number: json.WriteNumberValue(number); break;
      case long number: json.WriteNumberValue(number); break;
      case double number when double.IsFinite(number): json.WriteNumberValue(number); break;
      default: json.WriteStringValue(value.ToString()); break;
    }
  }
}
