using Jsr4Z8.Core.Settings;

namespace Jsr4Z8.UI.Services;

/// <summary>
/// Read access to the app settings for the queue (Task 13). Deliberately thin — just
/// CurrentSettings: Task 14 swaps <see cref="DefaultSettingsProvider"/> for the
/// persisted settings service behind the same interface, and the queue keeps reading
/// whatever is current at each run.
/// </summary>
public interface ISettingsProvider
{
    AppSettings CurrentSettings { get; }
}

/// <summary>In-memory defaults until Task 14's persisted settings service.</summary>
public sealed class DefaultSettingsProvider : ISettingsProvider
{
    public AppSettings CurrentSettings { get; } = new();
}
