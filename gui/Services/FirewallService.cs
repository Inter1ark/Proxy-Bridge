using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace ProxyBridge.GUI.Services;

/// <summary>A Windows Firewall rule as the planner sees it.</summary>
public sealed record FwRule(
    string Name,
    string ApplicationName,
    bool Inbound,
    bool Allow,
    bool Enabled,
    int Protocol,
    int Profiles)
{
    /// <summary>Opaque handle of the store (the COM rule object); not compared.</summary>
    public object? Handle { get; init; }
}

/// <summary>What has to change so ProxyBridge can receive its redirected connections.</summary>
public sealed class FirewallPlan
{
    /// <summary>Enabled Block rules for this exe (usually created by "Cancel" on the Windows firewall prompt).</summary>
    public List<FwRule> Remove { get; } = new();
    /// <summary>True when no enabled inbound Allow rule covers TCP on all profiles for this exe.</summary>
    public bool AddAllow { get; set; }
    public bool IsEmpty => Remove.Count == 0 && !AddAllow;
}

/// <summary>Access to the firewall rules (COM in the app, a fake in tests).</summary>
public interface IFirewallStore
{
    IReadOnlyList<FwRule> GetRules();
    void Remove(FwRule rule);
    void AddAllow(string name, string exePath);
}

public enum FirewallOutcome { Skipped, AlreadyOk, Fixed, Failed }

public sealed record FirewallResult(FirewallOutcome Outcome, string Message);

/// <summary>
/// The core receives redirected connections as inbound TCP on its local relay port, so Windows
/// Firewall applies to ProxyBridge.exe. At startup (elevated, Windows only) this makes sure an
/// inbound Allow rule "ProxyBridge" exists for the current exe on all profiles and removes enabled
/// inbound Block rules for the same exe. Errors are logged and never stop the app.
/// </summary>
public static class FirewallService
{
    public const string RuleName = "ProxyBridge";
    public const int ProtoTcp = 6;
    public const int ProtoAny = 256;
    public const int ProfileDomain = 1, ProfilePrivate = 2, ProfilePublic = 4;
    public const int ProfilesAll = 0x7FFFFFFF;

    /// <summary>Pure diff: which rules to remove and whether the Allow rule must be added.</summary>
    public static FirewallPlan Plan(IEnumerable<FwRule> rules, string exePath)
    {
        var plan = new FirewallPlan();
        var hasAllow = false;
        foreach (var r in rules)
        {
            if (!r.Enabled || !SamePath(r.ApplicationName, exePath)) continue;
            if (!r.Allow)
            {
                plan.Remove.Add(r); // inbound or outbound, a block rule for this exe breaks the relay
                continue;
            }
            if (!r.Inbound) continue;
            var tcp = r.Protocol == ProtoTcp || r.Protocol == ProtoAny;
            var allProfiles = (r.Profiles & (ProfileDomain | ProfilePrivate | ProfilePublic)) == (ProfileDomain | ProfilePrivate | ProfilePublic);
            if (tcp && allProfiles) hasAllow = true;
        }
        plan.AddAllow = !hasAllow;
        return plan;
    }

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string p)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')));
        }
        catch
        {
            return p.Trim();
        }
    }

    /// <summary>Applies the plan to a store. Throws on store errors (the caller logs them).</summary>
    public static FirewallResult Apply(IFirewallStore store, string exePath)
    {
        var plan = Plan(store.GetRules(), exePath);
        if (plan.IsEmpty)
            return new FirewallResult(FirewallOutcome.AlreadyOk, "allow rule present, no block rules");

        var done = new List<string>();
        foreach (var r in plan.Remove)
        {
            store.Remove(r);
            done.Add($"removed block rule '{r.Name}'");
        }
        if (plan.AddAllow)
        {
            store.AddAllow(RuleName, exePath);
            done.Add($"added inbound allow rule '{RuleName}' (TCP, all profiles)");
        }

        // verify
        var after = Plan(store.GetRules(), exePath);
        if (!after.IsEmpty)
            return new FirewallResult(FirewallOutcome.Failed, string.Join("; ", done) + "; verification failed");
        return new FirewallResult(FirewallOutcome.Fixed, string.Join("; ", done));
    }

    /// <summary>Startup entry point: Windows, elevated only; never throws.</summary>
    public static FirewallResult EnsureForCurrentExe()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return new FirewallResult(FirewallOutcome.Skipped, "not Windows");
            if (!IsElevated())
            {
                AppLog.Info("Firewall: not elevated, rule check skipped");
                return new FirewallResult(FirewallOutcome.Skipped, "not elevated");
            }
            var exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0)
                return new FirewallResult(FirewallOutcome.Failed, "process path unknown");

            var result = Apply(new ComFirewallStore(), exe);
            if (result.Outcome == FirewallOutcome.Failed)
                AppLog.Warn($"Firewall: {result.Message} ({exe})");
            else
                AppLog.Info($"Firewall: {result.Message} ({exe})");
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Firewall: could not ensure the allow rule: {ex.Message}");
            return new FirewallResult(FirewallOutcome.Failed, ex.Message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>Windows Firewall through the HNetCfg.FwPolicy2 COM API (late bound, no extra packages).</summary>
[SupportedOSPlatform("windows")]
public sealed class ComFirewallStore : IFirewallStore
{
    private const int DirIn = 1;
    private const int ActionAllow = 1;

    private readonly dynamic _policy;

    public ComFirewallStore()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
        _policy = Activator.CreateInstance(type)!;
    }

    public IReadOnlyList<FwRule> GetRules()
    {
        var list = new List<FwRule>();
        foreach (dynamic r in (IEnumerable)_policy.Rules)
        {
            string app;
            try { app = (string?)r.ApplicationName ?? ""; } catch { app = ""; }
            if (app.Length == 0) continue; // only program rules matter here
            list.Add(new FwRule(
                (string?)r.Name ?? "",
                app,
                (int)r.Direction == DirIn,
                (int)r.Action == ActionAllow,
                (bool)r.Enabled,
                (int)r.Protocol,
                (int)r.Profiles) { Handle = r });
        }
        return list;
    }

    public void Remove(FwRule rule)
    {
        // Rules.Remove works by name and names repeat, so give this exact rule a unique name first.
        dynamic r = rule.Handle ?? throw new InvalidOperationException("rule handle missing");
        var unique = "ProxyBridge-remove-" + Guid.NewGuid().ToString("N");
        r.Name = unique;
        _policy.Rules.Remove(unique);
    }

    public void AddAllow(string name, string exePath)
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!;
        dynamic rule = Activator.CreateInstance(type)!;
        rule.Name = name;
        rule.Description = "Lets ProxyBridge receive the connections it redirects through the proxy.";
        rule.ApplicationName = exePath;
        rule.Protocol = FirewallService.ProtoTcp;
        rule.Direction = DirIn;
        rule.Action = ActionAllow;
        rule.Profiles = FirewallService.ProfilesAll;
        rule.Enabled = true;
        _policy.Rules.Add(rule);
    }
}
