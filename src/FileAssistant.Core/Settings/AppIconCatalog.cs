using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileAssistant.Core.Settings;

internal sealed class AppIconJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString()!;
        reader.Skip();
        return AppIconCatalog.Default;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

public sealed record AppIconChoice(string Id, string Chinese, string English);

public static class AppIconCatalog
{
    public const string Default = "cobalt";
    public static IReadOnlyList<AppIconChoice> Choices { get; } = Array.AsReadOnly<AppIconChoice>(
    [
        new(Default, "原生钴蓝", "Classic Cobalt"),
        new("charcoal", "炭墨点朱", "Charcoal Vermilion"),
        new("pine", "松针墨绿", "Pine Sage"),
        new("vermilion", "丹朱赤红", "Vermilion Clay"),
        new("amber", "蓝珀双面", "Prussian Amber")
    ]);

    public static string Normalize(string? id) => Choices.FirstOrDefault(choice =>
        string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase))?.Id ?? Default;
}
