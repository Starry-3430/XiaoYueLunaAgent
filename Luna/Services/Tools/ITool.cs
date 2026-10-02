namespace Luna.Services.Tools;

public interface ITool
{
    string Name { get; }
    string DisplayName { get; }
    string Description { get; }
    string ParametersSchema { get; }
    Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);
}