using System.Text.Json;
using System.Text.Json.Serialization;
using MarketNotes.Companion.Models;

namespace MarketNotes.Companion.Export;

public static class ScanExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ToJson(CompanionScanExport export) => JsonSerializer.Serialize(export, Options);
}
