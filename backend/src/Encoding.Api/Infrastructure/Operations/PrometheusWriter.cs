using System.Globalization;
using System.Text;

namespace Encoding.Api.Infrastructure.Operations;

/// <summary>Écrit le format texte Prometheus 0.0.4. Seule définition du format dans le projet.</summary>
public sealed class PrometheusWriter
{
    private readonly StringBuilder text = new();
    private readonly HashSet<string> declared = [];

    public PrometheusWriter Sample(string name, string type, string help, double value, params (string Name, string Value)[] labels)
    {
        if (declared.Add(name))
        {
            text.Append("# HELP ").Append(name).Append(' ').Append(help.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)).Append('\n');
            text.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        }
        text.Append(name);
        if (labels.Length > 0)
            text.Append('{').Append(string.Join(',', labels.Select(label => $"{label.Name}=\"{Escape(label.Value)}\""))).Append('}');
        text.Append(' ').Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        return this;
    }

    public PrometheusWriter Gauge(string name, string help, double value, params (string Name, string Value)[] labels) => Sample(name, "gauge", help, value, labels);
    public PrometheusWriter Counter(string name, string help, double value, params (string Name, string Value)[] labels) => Sample(name, "counter", help, value, labels);

    public override string ToString() => text.ToString();

    public static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
