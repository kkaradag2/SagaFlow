using SagaFlow.Core.Definitions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SagaFlow.Engine.ProcessDefinition;

public sealed class DefinitionYamlCodec
{
    public const int MaximumContentLength = 1_048_576;

    public SagaDefinition Parse(string yaml, string source = "YAML content")
    {
        if (yaml.Length > MaximumContentLength)
            throw new DefinitionValidationException([$"{source}: definition exceeds {MaximumContentLength} characters."]);
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithDuplicateKeyChecking()
                .WithTypeMapping<IReadOnlyList<SagaStepDefinition>, List<SagaStepDefinition>>()
                .WithTypeMapping<IReadOnlyList<SagaTransitionDefinition>, List<SagaTransitionDefinition>>()
                .WithTypeMapping<IReadOnlyList<BigPictureSourceDefinition>, List<BigPictureSourceDefinition>>()
                .Build();
            var definition = deserializer.Deserialize<SagaDefinition>(yaml)
                ?? throw new DefinitionValidationException([$"{source}: empty definition."]);
            new ProcessDefinitionValidator().Validate(definition);
            return DefinitionSnapshot.Copy(definition);
        }
        catch (YamlException ex)
        {
            throw new DefinitionValidationException([$"{source}: invalid YAML at line {ex.Start.Line + 1}, column {ex.Start.Column + 1}: {ex.Message}"]);
        }
        catch (DefinitionValidationException ex)
        {
            throw new DefinitionValidationException(ex.Errors.Select(e => $"{source}: {e}"));
        }
    }

    public string Serialize(SagaDefinition definition, bool includeBigPicture = true)
    {
        new ProcessDefinitionValidator().Validate(definition);
        return new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build().Serialize(DefinitionSnapshot.Copy(definition, includeBigPicture));
    }
}
