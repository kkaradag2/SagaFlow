using SagaFlow.Core.Abstractions;
using SagaFlow.Core.Definitions;

namespace SagaFlow.Engine.ProcessDefinition;

public sealed class YamlSagaDefinitionLoader : ISagaDefinitionLoader
{
    private readonly string? _sourceDirectory;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public YamlSagaDefinitionLoader() { }
    public YamlSagaDefinitionLoader(string sourceDirectory) => _sourceDirectory = Path.GetFullPath(sourceDirectory);

    public async ValueTask<SagaDefinition> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(_sourceDirectory is null ? path : Path.Combine(_sourceDirectory, path));
        if (_sourceDirectory is not null && !fullPath.StartsWith(_sourceDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison))
            throw new DefinitionValidationException(["Definition path must stay within the configured source directory."]);
        RejectLinks(fullPath);
        if (new FileInfo(fullPath).Length > DefinitionYamlCodec.MaximumContentLength)
            throw new DefinitionValidationException([$"Definition '{Path.GetFileName(fullPath)}' exceeds the size limit."]);
        var yaml = await File.ReadAllTextAsync(fullPath, cancellationToken);
        return new DefinitionYamlCodec().Parse(yaml, Path.GetFileName(fullPath));
    }

    public async Task<IReadOnlyList<SagaDefinition>> LoadDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(directory);
        if (_sourceDirectory is not null && !string.Equals(fullPath, _sourceDirectory, PathComparison))
            throw new DefinitionValidationException(["Directory must match the configured source directory."]);
        var definitions = new List<SagaDefinition>();
        var ids = new HashSet<ProcessDefinitionId>();
        var loader = new YamlSagaDefinitionLoader(fullPath);
        foreach (var path in EnumerateDefinitionFiles(fullPath))
        {
            var definition = await loader.LoadAsync(Path.GetFileName(path), cancellationToken);
            if (!ids.Add(ProcessDefinitionId.From(definition)))
                throw new DefinitionValidationException([$"Duplicate local process '{ProcessDefinitionId.From(definition)}' in '{Path.GetFileName(path)}'."]);
            definitions.Add(definition);
        }
        return definitions.AsReadOnly();
    }

    /// <summary>Shared source-directory policy; callers still select their loader through DI.</summary>
    public static IReadOnlyList<string> EnumerateDefinitionFiles(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"SagaFlow definition directory does not exist: {fullPath}");
        RejectLinks(fullPath);
        var paths = Directory.EnumerateFiles(fullPath).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".yaml" or ".yml").Order(StringComparer.Ordinal).ToArray();
        foreach (var path in paths) RejectLinks(path);
        return Array.AsReadOnly(paths);
    }

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new DefinitionValidationException(["Symbolic links/reparse points are not supported in definition paths."]);
    }
}
