using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace McpGateway.Registry;

/// <summary>Reads <c>registry.yaml</c> into a <see cref="RegistryDocument"/>.</summary>
public static class RegistryLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Loads and parses the registry file at <paramref name="path"/>.</summary>
    /// <exception cref="RegistryValidationException">The file is missing or is not valid YAML.</exception>
    public static RegistryDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new RegistryValidationException(
                $"Registry file '{Path.GetFullPath(path)}' was not found. Set GATEWAY_REGISTRY_PATH to its location.");
        }

        return Parse(File.ReadAllText(path));
    }

    /// <summary>Parses registry YAML that has already been read into memory.</summary>
    /// <exception cref="RegistryValidationException">The text is not valid registry YAML.</exception>
    public static RegistryDocument Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        try
        {
            return Deserializer.Deserialize<RegistryDocument>(yaml) ?? new RegistryDocument();
        }
        catch (YamlException ex)
        {
            throw new RegistryValidationException($"registry.yaml could not be parsed: {ex.Message}", ex);
        }
    }
}
