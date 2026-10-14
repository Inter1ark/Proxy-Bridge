using System.Threading.Tasks;

namespace ProxyBridge.GUI.Services;

/// <summary>
/// An engine that can swap its proxies and rules while running, without a restart.
/// The caller clears and re-adds proxies and rules through <see cref="IProxyEngine"/> (which only
/// records them while the engine runs) and then commits the new set in one step.
/// </summary>
public interface ILiveRules
{
    /// <summary>Sends the recorded proxies and rules to the running engine. False on failure (see LastError).</summary>
    Task<bool> CommitRulesAsync();
}
