using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mailnix.Core.Rules;

/// <summary>A rule as the engine sees it. Scheduling (trigger, source folders) is handled by the caller.</summary>
public sealed record RuleDefinition
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Lower runs first.</summary>
    public int Priority { get; init; }

    /// <summary>Disabled rules run no actions but can still be referenced by other rules.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Do not evaluate rules with a higher priority value once this one matched.</summary>
    public bool StopProcessing { get; init; }

    /// <summary>Plan and log actions without executing them.</summary>
    public bool DryRun { get; init; }

    public required ConditionNode Conditions { get; init; }

    /// <summary>May be empty: "filter-only" rules exist to be referenced by other rules.</summary>
    public IReadOnlyList<RuleAction> Actions { get; init; } = [];
}

/// <summary>Serializer settings for rule conditions and actions (storage and API).</summary>
public static class RuleJson
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Applies the settings rules need to existing options (e.g. the API's).</summary>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        if (!options.Converters.OfType<JsonStringEnumConverter>().Any())
        {
            options.Converters.Add(new JsonStringEnumConverter());
        }

        // Clients may send "kind"/"type" after other properties.
        options.AllowOutOfOrderMetadataProperties = true;
        return options;
    }
}
