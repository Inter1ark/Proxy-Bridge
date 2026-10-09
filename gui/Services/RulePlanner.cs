using System;
using System.Collections.Generic;
using System.Linq;

namespace ProxyBridge.GUI.Services;

/// <summary>One row of the rules table as the planner sees it.</summary>
/// <param name="Process">Exe name ("chrome.exe") or full path; empty rows are skipped.</param>
/// <param name="Proxy">Saved proxy string, or "" for a direct connection.</param>
/// <param name="Enabled">Disabled rows create no rule.</param>
public sealed record RuleInput(string Process, string Proxy, bool Enabled);

/// <summary>A rule to add with ProxyBridge_AddRuleEx. <see cref="Proxy"/> is "" for DIRECT.</summary>
public sealed record PlannedRule(string Process, string Proxy)
{
    public bool IsDirect => Proxy.Length == 0;
}

/// <summary>What the engine must be told, in order (see docs/CORE_API.md, "Rules").</summary>
public sealed class RulePlan
{
    /// <summary>Distinct proxies in first-use order (each is registered once with AddProxy).</summary>
    public List<string> Proxies { get; } = new();
    /// <summary>App rules first (insertion order = priority), then the catch-all "*" rule when "all other apps" use a proxy.</summary>
    public List<PlannedRule> Rules { get; } = new();
    /// <summary>"" when the plan is usable, else an i18n key explaining why not.</summary>
    public string ErrorKey { get; set; } = "";
    /// <summary>Argument for <see cref="ErrorKey"/> (for example the program whose proxy is invalid).</summary>
    public string ErrorArg { get; set; } = "";
    public bool IsValid => ErrorKey.Length == 0;
    /// <summary>The proxy used by "all other apps", or "" when they go direct.</summary>
    public string DefaultProxy { get; set; } = "";
}

/// <summary>Result of <see cref="RulePlanner.Apply"/>.</summary>
public sealed class AppliedRules
{
    public Dictionary<string, uint> ProxyIds { get; } = new(StringComparer.Ordinal);
    public List<uint> RuleIds { get; } = new();
    public string ErrorKey { get; set; } = "";
    public string ErrorArg { get; set; } = "";
    public bool Ok => ErrorKey.Length == 0;
}

/// <summary>
/// Turns the rules table into engine calls. Pure logic, unit-tested with a fake <see cref="IProxyEngine"/>:
/// AddProxy once per distinct proxy that is actually used, AddRuleEx per enabled app row
/// (PROXY with its proxy id, or DIRECT), then one catch-all <c>*</c> PROXY rule for "all other apps"
/// when they use a proxy (no rule when they go direct: no match means DIRECT in the core).
/// </summary>
public static class RulePlanner
{
    public const string ErrNoProxy = "home.err.no_proxy";
    public const string ErrBadProxy = "home.err.bad_proxy";
    public const string ErrRegister = "home.err.register_failed";
    public const string ErrRule = "home.err.rule_failed";

    public static RulePlan Build(IEnumerable<RuleInput> rows, string defaultProxy)
    {
        var plan = new RulePlan();
        var seenProcess = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var process = (row.Process ?? "").Trim();
            if (!row.Enabled || process.Length == 0) continue;
            if (!seenProcess.Add(process)) continue; // first row for a program wins, like the core
            var proxy = (row.Proxy ?? "").Trim();
            if (proxy.Length > 0 && !ProxyParser.TryParse(proxy, out _))
            {
                plan.ErrorKey = ErrBadProxy;
                plan.ErrorArg = process;
                return plan;
            }
            if (proxy.Length > 0 && !plan.Proxies.Contains(proxy)) plan.Proxies.Add(proxy);
            plan.Rules.Add(new PlannedRule(process, proxy));
        }

        var def = (defaultProxy ?? "").Trim();
        if (def.Length > 0)
        {
            if (!ProxyParser.TryParse(def, out _))
            {
                plan.ErrorKey = ErrBadProxy;
                plan.ErrorArg = "*";
                return plan;
            }
            if (!plan.Proxies.Contains(def)) plan.Proxies.Add(def);
            plan.Rules.Add(new PlannedRule("*", def));
            plan.DefaultProxy = def;
        }

        if (plan.Proxies.Count == 0)
            plan.ErrorKey = ErrNoProxy;
        return plan;
    }

    /// <summary>Registers the plan's proxies (call before Start).</summary>
    public static AppliedRules RegisterProxies(IProxyEngine engine, RulePlan plan)
    {
        var applied = new AppliedRules();
        engine.ClearProxies();
        foreach (var raw in plan.Proxies)
        {
            ProxyParser.TryParse(raw, out var p);
            var id = engine.AddProxy(p.Type, p.Host, ushort.Parse(p.Port), p.User, p.Pass);
            if (id == 0)
            {
                applied.ErrorKey = ErrRegister;
                applied.ErrorArg = p.HostPort;
                return applied;
            }
            applied.ProxyIds[raw] = id;
        }
        return applied;
    }

    /// <summary>Adds the plan's rules in order (call after Start). Stops at the first failure.</summary>
    public static void AddRules(IProxyEngine engine, RulePlan plan, AppliedRules applied)
    {
        foreach (var rule in plan.Rules)
        {
            uint id;
            if (rule.IsDirect)
            {
                id = engine.AddRuleEx(rule.Process, "*", "*", "TCP", "DIRECT", 0);
            }
            else
            {
                id = engine.AddRuleEx(rule.Process, "*", "*", "TCP", "PROXY", applied.ProxyIds[rule.Proxy]);
            }
            if (id == 0)
            {
                applied.ErrorKey = ErrRule;
                applied.ErrorArg = rule.Process;
                return;
            }
            applied.RuleIds.Add(id);
        }
    }
}
