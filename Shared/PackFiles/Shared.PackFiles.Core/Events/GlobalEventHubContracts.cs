namespace Shared.Core.Events;

/// <summary>
/// The small global-event surface used by pack-aware indexing services.
/// The editor owns the concrete event hub implementation; this contract is
/// kept in the low-level project so headless consumers do not need Shared.Core.
/// </summary>
public interface IGlobalEventHub
{
    void PublishGlobalEvent<T>(T e);
    void Register<T>(object owner, Action<T> action);
    void UnRegister(object owner);
}
