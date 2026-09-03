// MarketingAgent — Hybrid-shaped campaign harness over MockAdMcp.
//
// Graph mirrors console.platform setup flow (simplified):
//   brief → creative → setup (create RK + SSP + banner + activate) → review
//
// Requires MockAdMcp:
//   terminal 1:  dotnet run --project samples/MockAdMcp
//   terminal 2:  dotnet run --project samples/MarketingAgent -- --offline

using System.Text.Json;
using Voluta;
using Voluta.Abstractions.Channels;
using Voluta.Abstractions.Results;
using Voluta.Abstractions.Runtime;
using Voluta.Abstractions.Streaming;
using Voluta.Checkpoint;
using Voluta.Graph.Builder;
using Voluta.Graph.Options;
using Voluta.Samples.MarketingAgent;
using Voluta.Samples.Shared;

var offline = HarnessCli.HasFlag(args, "--offline");
var dryRun = HarnessCli.HasFlag(args, "--dry-run");
var hitl = HarnessCli.HasFlag(args, "--hitl");
var mcpUrl = HarnessCli.GetOption(args, "--mcp-url") ?? "http://localhost:5190";
var product = HarnessCli.GetOption(args, "--product") ?? "Hybrid.ai Console Platform";
var advertiserId = HarnessCli.GetOption(args, "--advertiser") ?? "adv_demo_brand";
var campaignType = HarnessCli.GetOption(args, "--type") ?? "HybridExtended";
var bet = HarnessCli.GetOption(args, "--bet") ?? "85";
var dailyBudget = HarnessCli.GetOption(args, "--daily-budget") ?? "50000";
var threadId = HarnessCli.GetOption(args, "--thread") ?? $"mkt-{Guid.NewGuid():N}";

CliUi.Banner(
    "MarketingAgent",
    "Hybrid desk · brief → creative → campaign setup → review",
    ("thread", threadId),
    ("product", product),
    ("advertiser", advertiserId),
    ("type", campaignType),
    ("bet", $"{bet} RUB"),
    ("dailyBudget", $"{dailyBudget} RUB"),
    ("mcp", dryRun ? "(dry-run)" : mcpUrl),
    ("mode", (offline ? "offline" : "live") + (hitl ? " · hitl" : "")));

var chatClient = HarnessCli.CreateChatClient(offline, out var chatLifetime);
using var chatDispose = chatLifetime;

MockMcpClient? mcp = null;
if (!dryRun)
{
    try
    {
        mcp = MockMcpClient.Create(mcpUrl);
        var tools = await mcp.ListToolsAsync();
        CliUi.Node("mcp", $"connected · {tools.Count} Hybrid-shaped tool(s)");
        foreach (var tool in tools.Take(8))
        {
            CliUi.Bullet($"{tool.Name} — {tool.Description}");
        }
    }
    catch (Exception exception)
    {
        CliUi.Error($"MCP unreachable at {mcpUrl}: {exception.Message}");
        CliUi.Info("start MockAdMcp first, or pass --dry-run");
        return 1;
    }
}

using var mcpDispose = mcp;

var checkpointer = new InMemoryCheckpointer();
var graph = new StateGraph()
    .AddChannel("brief", ChannelKind.LastValue)
    .AddChannel("creative", ChannelKind.LastValue)
    .AddChannel("campaign_id", ChannelKind.LastValue)
    .AddChannel("desk_log", ChannelKind.Append)
    .AddChannel("verdict", ChannelKind.LastValue)
    .AddChannel("status", ChannelKind.LastValue)
    .AddNode(
        "brief",
        async (context, cancellationToken) =>
        {
            var text = await chatClient.CompleteAsync(
                "You are a Hybrid.ai trading-desk campaign planner (console.platform).",
                $"""
                Draft a short campaign brief for the Hybrid console.
                product: {product}
                advertiserId: {advertiserId}
                campaignType: {campaignType}
                bet_rub: {bet}
                daily_budget_rub: {dailyBudget}
                Include: objective, audience (RU), SSP preference, success metric (eCPM / CPA).
                """,
                cancellationToken);
            CliUi.Node("brief", "campaign brief ready");
            return NodeResult.Continue(
                new ChannelWrite("brief", text),
                new ChannelWrite("status", "creative"));
        })
    .AddNode(
        "creative",
        async (context, cancellationToken) =>
        {
            var brief = context.Read<string>("brief") ?? "";
            var text = await chatClient.CompleteAsync(
                "You are a Hybrid AdLibrary creative writer for RU display/video.",
                $"""
                Write one creative meta for AdLibrary (headline + body + CTA + suggested size).
                brief:
                {brief}
                product: {product}
                """,
                cancellationToken);
            CliUi.Node("creative", "AdLibrary meta drafted");
            return NodeResult.Continue(
                new ChannelWrite("creative", text),
                new ChannelWrite("status", "setup"));
        })
    .AddNode(
        "setup",
        async (context, cancellationToken) =>
        {
            var creative = context.Read<string>("creative") ?? product;
            var writes = new List<ChannelWrite> { new("status", "review") };

            if (dryRun || mcp is null)
            {
                CliUi.Node("setup", "dry-run · skipped Hybrid MCP calls");
                writes.Add(new ChannelWrite("desk_log", "(dry-run) no remote desk"));
                writes.Add(new ChannelWrite("campaign_id", "cmp_dry_run"));
                return NodeResult.Continue(writes);
            }

            CliUi.Node("setup", "list_advertisers");
            var advertisers = await mcp.CallAsync(
                "list_advertisers",
                new Dictionary<string, object?>(),
                cancellationToken);
            writes.Add(new ChannelWrite("desk_log", advertisers));
            CliUi.Bullet(Truncate(advertisers, 140));

            CliUi.Node("setup", "list_ssps");
            var sspsJson = await mcp.CallAsync(
                "list_ssps",
                new Dictionary<string, object?>(),
                cancellationToken);
            writes.Add(new ChannelWrite("desk_log", sspsJson));

            CliUi.Node("setup", "list_ad_library format=Display");
            var library = await mcp.CallAsync(
                "list_ad_library",
                new Dictionary<string, object?> { ["format"] = "Display", ["status"] = "Approved" },
                cancellationToken);
            writes.Add(new ChannelWrite("desk_log", library));
            var adLibraryId = PickString(library, "items", "id") ?? "adl_300x250_v1";

            CliUi.Node("setup", $"suggest_cpm sspId=24 (Yandex)");
            var suggested = await mcp.CallAsync(
                "suggest_cpm",
                new Dictionary<string, object?> { ["sspId"] = 24 },
                cancellationToken);
            writes.Add(new ChannelWrite("desk_log", suggested));

            var campaignName =
                $"{product.Replace(' ', '_')}_{campaignType}_{DateTime.UtcNow:yyyyMMdd}";
            CliUi.Node("setup", $"create_campaign {campaignName}");
            var created = await mcp.CallAsync(
                "create_campaign",
                new Dictionary<string, object?>
                {
                    ["advertiserId"] = advertiserId,
                    ["name"] = campaignName,
                    ["campaignType"] = campaignType,
                    ["bet"] = decimal.Parse(bet),
                    ["betOptimizationType"] = "NoOptimization",
                    ["dailyBudget"] = decimal.Parse(dailyBudget),
                    ["totalBudget"] = decimal.Parse(dailyBudget) * 10,
                    ["startDate"] = DateTime.UtcNow.Date.ToString("yyyy-MM-dd"),
                    ["endDate"] = DateTime.UtcNow.Date.AddDays(30).ToString("yyyy-MM-dd"),
                    ["defaultClickUrl"] = "https://hybrid.ai/console",
                },
                cancellationToken);
            writes.Add(new ChannelWrite("desk_log", created));
            var campaignId = PickRootString(created, "id")
                             ?? throw new InvalidOperationException("create_campaign returned no id");
            writes.Add(new ChannelWrite("campaign_id", campaignId));
            CliUi.Bullet($"campaignId={campaignId}");

            CliUi.Node("setup", "set_linked_ssps Yandex+Between");
            var linked = await mcp.CallAsync(
                "set_linked_ssps",
                new Dictionary<string, object?>
                {
                    ["campaignId"] = campaignId,
                    ["linkedSystems"] = new object[]
                    {
                        new Dictionary<string, object?> { ["sspId"] = 24, ["bet"] = decimal.Parse(bet) + 5 },
                        new Dictionary<string, object?> { ["sspId"] = 7, ["bet"] = null },
                    },
                },
                cancellationToken);
            writes.Add(new ChannelWrite("desk_log", linked));

            CliUi.Node("setup", "list_direct_deals / attach");
            var deals = await mcp.CallAsync(
                "list_direct_deals",
                new Dictionary<string, object?>(),
                cancellationToken);
            writes.Add(new ChannelWrite("desk_log", deals));
            var dealId = PickString(deals, "directDeals", "id");
            if (dealId is not null)
            {
                var attached = await mcp.CallAsync(
                    "attach_direct_deals",
                    new Dictionary<string, object?>
                    {
                        ["campaignId"] = campaignId,
                        ["directDealIds"] = new[] { dealId },
                    },
                    cancellationToken);
                writes.Add(new ChannelWrite("desk_log", attached));
            }

            CliUi.Node("setup", $"attach_banner {adLibraryId}");
            var banner = await mcp.CallAsync(
                "attach_banner",
                new Dictionary<string, object?>
                {
                    ["campaignId"] = campaignId,
                    ["adLibraryId"] = adLibraryId,
                    ["name"] = "auto_from_agent",
                    ["destUrl"] = "https://hybrid.ai/console",
                    ["betKoeff"] = 1.0m,
                },
                cancellationToken);
            writes.Add(new ChannelWrite("desk_log", banner));
            CliUi.Bullet(Truncate(creative, 100));

            CliUi.Node("setup", "set_campaign_status Active");
            var activated = await mcp.CallAsync(
                "set_campaign_status",
                new Dictionary<string, object?>
                {
                    ["campaignId"] = campaignId,
                    ["status"] = "Active",
                },
                cancellationToken);
            writes.Add(new ChannelWrite("desk_log", activated));

            CliUi.Node("setup", "get_delivery_status");
            var delivery = await mcp.CallAsync(
                "get_delivery_status",
                new Dictionary<string, object?> { ["campaignId"] = campaignId },
                cancellationToken);
            writes.Add(new ChannelWrite("desk_log", delivery));
            CliUi.Bullet(Truncate(delivery, 160));

            return NodeResult.Continue(writes);
        })
    .AddNode(
        "review",
        async (context, cancellationToken) =>
        {
            if (hitl && !context.IsResume)
            {
                CliUi.Node("review", "interrupt · approve activation to continue");
                return NodeResult.Interrupt(new
                {
                    reason = "approve_campaign_activation",
                    product,
                    advertiserId,
                    campaignId = context.Read<string>("campaign_id"),
                });
            }

            var brief = context.Read<string>("brief") ?? "";
            var creative = context.Read<string>("creative") ?? "";
            var campaignId = context.Read<string>("campaign_id") ?? "";
            var deskLog = FormatList(context.Read<object>("desk_log"));
            var verdict = await chatClient.CompleteAsync(
                "You are a Hybrid trading-desk reviewer (campaign go / no-go).",
                $"""
                Review this Hybrid campaign package.
                campaignId: {campaignId}
                brief:
                {brief}

                creative:
                {creative}

                desk log (tool results):
                {Truncate(deskLog, 2500)}

                Output: go / no-go + one-line rationale (SSP, limits, banners, DeliveryStatus).
                """,
                cancellationToken);
            CliUi.Node("review", "verdict ready");
            return NodeResult.Continue(
                new ChannelWrite("verdict", verdict),
                new ChannelWrite("status", "done"));
        })
    .AddEdge(GraphConstants.Start, "brief")
    .AddEdge("brief", "creative")
    .AddEdge("creative", "setup")
    .AddEdge("setup", "review")
    .AddEdge("review", GraphConstants.End)
    .Compile(checkpointer, new CompileOptions { RecursionLimit = 24 });

var terminal = await graph.InvokeAsync(
    [new ChannelWrite("status", "start")],
    new RunOptions { ThreadId = threadId, StreamMode = StreamMode.Updates });

if (terminal.Kind == StreamEventKind.Interrupt)
{
    if (!HarnessCli.Confirm("Approve campaign activation / resume review?"))
    {
        CliUi.Warn("aborted at interrupt");
        return 2;
    }

    terminal = await graph.ResumeInvokeAsync(
        threadId,
        Command.Approve("ok"));
}

var snapshot = await checkpointer.GetAsync(threadId);
CliUi.Section("result");
CliUi.KeyValue("status", snapshot?.Status.ToString());
if (snapshot?.ChannelValues.TryGetValue("campaign_id", out var campaignIdBody) is true)
{
    CliUi.KeyValue("campaignId", campaignIdBody?.ToString());
}

if (snapshot?.ChannelValues.TryGetValue("brief", out var briefBody) is true)
{
    CliUi.Panel("brief", briefBody?.ToString() ?? "");
}

if (snapshot?.ChannelValues.TryGetValue("creative", out var creativeBody) is true)
{
    CliUi.Panel("creative", creativeBody?.ToString() ?? "");
}

if (snapshot?.ChannelValues.TryGetValue("verdict", out var verdictBody) is true)
{
    CliUi.Panel("verdict", verdictBody?.ToString() ?? "");
}

if (terminal.Kind is StreamEventKind.Failed)
{
    CliUi.Error("run failed");
    return 1;
}

CliUi.Ok("done");
return 0;

static string? PickString(string json, string arrayName, string field)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        return !doc.RootElement.TryGetProperty(arrayName, out var array)
               || array.ValueKind != JsonValueKind.Array
               || array.GetArrayLength() == 0
            ? null
            : array[0].TryGetProperty(field, out var value)
                ? value.GetString()
                : null;
    }
    catch (JsonException)
    {
        return null;
    }
}

static string? PickRootString(string json, string field)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(field, out var value) ? value.GetString() : null;
    }
    catch (JsonException)
    {
        return null;
    }
}

static string FormatList(object? value)
{
    return value switch
    {
        null => "",
        string text => text,
        System.Collections.IEnumerable list and not string =>
            string.Join("\n---\n", list.Cast<object?>().Select(static item => item?.ToString() ?? "")),
        _ => value.ToString() ?? "",
    };
}

static string Truncate(string text, int max)
{
    return text.Length <= max ? text : text[..max] + "…";
}
