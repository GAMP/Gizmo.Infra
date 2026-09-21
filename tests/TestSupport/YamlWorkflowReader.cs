using YamlDotNet.RepresentationModel;

namespace Gizmo.Infra.Tests.TestSupport;

/// <summary>
/// Reads rendered workflow YAML by raw scalar value rather than resolved tag, so
/// GitHub's <c>on:</c> key is matched literally regardless of YAML 1.1 coercion.
/// </summary>
public static class YamlWorkflowReader
{
    public static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));

        Assert.False(stream.Documents.Count == 0, "workflow YAML contains no document.");
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    public static bool HasChild(YamlMappingNode mapping, string key) => Find(mapping, key) is not null;

    public static YamlNode Child(YamlMappingNode mapping, string key) =>
        Find(mapping, key) ?? throw new Xunit.Sdk.XunitException($"YAML mapping has no '{key}' child.");

    public static YamlMappingNode MappingChild(YamlMappingNode mapping, string key) =>
        Assert.IsType<YamlMappingNode>(Child(mapping, key));

    public static string ScalarChild(YamlMappingNode mapping, string key) =>
        Assert.IsType<YamlScalarNode>(Child(mapping, key)).Value ?? string.Empty;

    public static IReadOnlyList<string> ScalarSequence(YamlNode node) =>
        Assert.IsType<YamlSequenceNode>(node).Children
            .Select(child => Assert.IsType<YamlScalarNode>(child).Value ?? string.Empty)
            .ToArray();

    public static YamlSequenceNode SequenceChild(YamlMappingNode mapping, string key) =>
        Assert.IsType<YamlSequenceNode>(Child(mapping, key));

    public static IReadOnlyList<YamlMappingNode> MappingSequence(YamlMappingNode mapping, string key) =>
        SequenceChild(mapping, key).Children
            .Select(child => Assert.IsType<YamlMappingNode>(child))
            .ToArray();

    private static YamlNode? Find(YamlMappingNode mapping, string key)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode { Value: not null } scalar
                && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }
}
