namespace SagaFlow.Hosting.Dashboard;

internal static class SagaFlowDashboardPath
{
    public static string Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] != '/')
            throw new InvalidOperationException("The SagaFlow Dashboard path must begin with '/' and contain one or more literal path segments.");

        var normalized = path.EndsWith('/') ? path[..^1] : path;
        if (normalized.Length == 0 || normalized[1..].Split('/').Any(segment => segment.Length == 0 || segment is "." or ".." ||
            segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '~'))))
            throw new InvalidOperationException("The SagaFlow Dashboard path must contain one or more literal URL path segments.");

        return normalized;
    }

    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/sagaflow";
        var normalized = path.StartsWith('/') ? path : $"/{path}";
        return normalized.TrimEnd('/');
    }

    public static string Hub(string? path) => $"{Normalize(path)}/hub";
}
