using Jsr4Z8.Core.Settings;

namespace Jsr4Z8.UI.Services;

/// <summary>
/// Read access to the app settings for the queue (Task 13). Deliberately thin — just
/// CurrentSettings: the app composes <see cref="SettingsServiceProvider"/> over the
/// persisted <see cref="SettingsService"/> (Task 14), and the queue keeps reading
/// whatever is current at each run.
/// </summary>
public interface ISettingsProvider
{
    AppSettings CurrentSettings { get; }
}

/// <summary>Headless default: in-memory plan defaults, no persistence. Also the
/// fallback of a QueueMediator constructed without a provider.</summary>
public sealed class DefaultSettingsProvider : ISettingsProvider
{
    public AppSettings CurrentSettings { get; } = new();
}

/// <summary>
/// Task 14 composition: adapts the shared <see cref="SettingsService"/> to
/// <see cref="ISettingsProvider"/>. CurrentSettings is the service's in-memory current
/// (loaded from disk at construction, updated by every Save) — the settings dialog, the
/// dark picker's LastDarkPath memory and the queue all share one service instance, so a
/// Save is visible to the next run without any event plumbing.
/// </summary>
public sealed class SettingsServiceProvider : ISettingsProvider
{
    private readonly SettingsService _service;

    public SettingsServiceProvider(SettingsService service) => _service = service;

    public AppSettings CurrentSettings => _service.Current;
}
