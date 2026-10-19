using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace UnifiedRgb.Contracts;

/// <summary>Shared JSON settings for both pipe ends (Host, App, Cli) and for status.json.</summary>
public static class ContractsJson
{
    /// <summary>Puts the source-generated DTO metadata first and uses camelCase, keeping any resolver already present as fallback.</summary>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.TypeInfoResolver = JsonTypeInfoResolver.Combine(ContractsJsonContext.Default, options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver());
        return options;
    }
}
