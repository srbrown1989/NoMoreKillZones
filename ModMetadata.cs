using SPTarkov.Server.Core.Models.Spt.Mod;

namespace NoMoreKillZones;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.imperator.nomorekillzones";
    public string Name { get; init; } = "NoMoreKillZones";
    public string Author { get; init; } = "Imperator";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
}
