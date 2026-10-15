using Jsr4Z8.Core.Settings;

namespace Jsr4Z8.UI.Services;

/// <summary>
/// Read access to the app settings for the queue (Task 13). Deliberately thin — just
/// CurrentSettings. The settings feature was removed wholesale (2026-10-16 user
/// ruling): the app runs on <see cref="DefaultSettingsProvider"/> (plan defaults, no
/// persistence); the interface stays as QueueMediator's injectable seam for tests.
/// </summary>
public interface ISettingsProvider
{
    AppSettings CurrentSettings { get; }
}

/// <summary>Plan defaults, in-memory, no persistence — the app's only settings source
/// and the fallback of a QueueMediator constructed without a provider.</summary>
public sealed class DefaultSettingsProvider : ISettingsProvider
{
    public AppSettings CurrentSettings { get; } = new();
}
