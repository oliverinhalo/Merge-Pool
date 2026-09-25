using MergePool.Engine;

namespace MergePool.Service;

/// <summary>
/// Carries the updater, which is absent when MergePool is not running from a managed install.
/// Dependency injection cannot hold a null service, and "there is nothing to update to" is a normal
/// state rather than a failure, so it travels wrapped.
/// </summary>
public sealed record UpdateHost(AutoUpdateService? Service);
