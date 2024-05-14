﻿using System.Reflection;
using AutoGen;
using AutoGen.Core;
using Azure.AI.OpenAI;
using ChatRoom;
using ChatRoom.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Runtime;
using Spectre.Console;

using var channelHost = new HostBuilder()
    .UseOrleansClient(clientBuilder =>
    {
        clientBuilder
            .UseLocalhostClustering()
            .AddMemoryStreams("chat");
    })
    .Build();

using var agentHost = new HostBuilder()
    .UseOrleansClient(clientBuilder =>
    {
        clientBuilder
            .UseLocalhostClustering(gatewayPort: 34567);
    })
    .Build();

PrintUsage();

// create agents
var AZURE_OPENAI_ENDPOINT = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
var AZURE_OPENAI_KEY = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
var AZURE_DEPLOYMENT_NAME = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOY_NAME");
var OPENAI_API_KEY = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var OPENAI_MODEL_ID = Environment.GetEnvironmentVariable("OPENAI_MODEL_ID") ?? "gpt-3.5-turbo-0125";

OpenAIClient openaiClient;
bool useAzure = false;
if (AZURE_OPENAI_ENDPOINT is string && AZURE_OPENAI_KEY is string && AZURE_DEPLOYMENT_NAME is string)
{
    openaiClient = new OpenAIClient(new Uri(AZURE_OPENAI_ENDPOINT), new Azure.AzureKeyCredential(AZURE_OPENAI_KEY));
    useAzure = true;
}
else if (OPENAI_API_KEY is string)
{
    openaiClient = new OpenAIClient(OPENAI_API_KEY);
}
else
{
    throw new ArgumentException("Please provide either (AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_KEY, AZURE_DEPLOYMENT_NAME) or OPENAI_API_KEY");
}

var deployModelName = useAzure ? AZURE_DEPLOYMENT_NAME! : OPENAI_MODEL_ID;

var client = channelHost.Services.GetRequiredService<IClusterClient>();
var agentClient = agentHost.Services.GetRequiredService<IClusterClient>();

ClientContext context = new(client, agentClient);
await StartAsync(channelHost);
await StartAsync(agentHost);
context = context with
{
    UserName = AnsiConsole.Ask<string>("What is your [aqua]name[/]?")
};
context = await JoinChannel(context, "General");
await ProcessLoopAsync(context);
await StopAsync(channelHost);

static Task StartAsync(IHost host) =>
    AnsiConsole.Status().StartAsync("Connecting to server", async ctx =>
    {
        ctx.Spinner(Spinner.Known.Dots);
        ctx.Status = "Connecting...";

        await host.StartAsync();

        ctx.Status = "Connected!";
    });

async Task ProcessLoopAsync(ClientContext context)
{
    string? input = null;
    do
    {
        input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            continue;
        }

        if (input.StartsWith("/exit") &&
            AnsiConsole.Confirm("Do you really want to exit?"))
        {
            break;
        }

        var firstTwoCharacters = input.Length >= 2 ? input[..2] : string.Empty;
        if (firstTwoCharacters is "/n")
        {
            context = context with { UserName = input.Replace("/n", "").Trim() };
            AnsiConsole.MarkupLine(
                "[dim][[STATUS]][/] Set username to [lime]{0}[/]", context.UserName);
            continue;
        }

        if (firstTwoCharacters switch
        {
            "/j" => JoinChannel(context, input.Replace("/j", "").Trim()),
            "/l" => LeaveChannel(context),
            _ => null
        } is Task<ClientContext> cxtTask)
        {
            context = await cxtTask;
            continue;
        }

        if (firstTwoCharacters switch
        {
            "/h" => ShowCurrentChannelHistory(context),
            "/m" => ShowChannelMembers(context),
            _ => null
        } is Task task)
        {
            await task;
            continue;
        }

        if (context.IsConnectedToChannel)
        {
            await SendMessage(context, input);
        }
    } while (input is not "/exit");
}

static Task StopAsync(IHost host) =>
    AnsiConsole.Status().StartAsync("Disconnecting...", async ctx =>
    {
        ctx.Spinner(Spinner.Known.Dots);
        await host.StopAsync();
    });

static void PrintUsage()
{
    AnsiConsole.WriteLine();
    using var logoStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ChatRoom.Client.logo.png");
    var logo = new CanvasImage(logoStream!)
    {
        MaxWidth = 25
    };

    var table = new Table()
    {
        Border = TableBorder.None,
        Expand = true,
    }.HideHeaders();
    table.AddColumn(new TableColumn("One"));

    var header = new FigletText("Orleans")
    {
        Color = Color.Fuchsia
    };
    var header2 = new FigletText("Chat Room")
    {
        Color = Color.Aqua
    };

    var markup = new Markup(
       "[bold fuchsia]/j[/] [aqua]<channel>[/] to [underline green]join[/] a specific channel\n"
       + "[bold fuchsia]/n[/] [aqua]<username>[/] to set your [underline green]name[/]\n"
       + "[bold fuchsia]/l[/] to [underline green]leave[/] the current channel\n"
       + "[bold fuchsia]/h[/] to re-read channel [underline green]history[/]\n"
       + "[bold fuchsia]/m[/] to query [underline green]members[/] in the channel\n"
       + "[bold fuchsia]/exit[/] to exit\n"
       + "[bold aqua]<message>[/] to send a [underline green]message[/]\n");
    table.AddColumn(new TableColumn("Two"));

    var rightTable = new Table()
        .HideHeaders()
        .Border(TableBorder.None)
        .AddColumn(new TableColumn("Content"));

    rightTable.AddRow(header)
        .AddRow(header2)
        .AddEmptyRow()
        .AddEmptyRow()
        .AddRow(markup);
    table.AddRow(logo, rightTable);

    AnsiConsole.Write(table);
    AnsiConsole.WriteLine();
}

static async Task ShowChannelMembers(ClientContext context)
{
    var room = context.ChannelClient.GetGrain<IChannelGrain>(context.CurrentChannel);

    if (!context.IsConnectedToChannel)
    {
        AnsiConsole.MarkupLine("[bold red]You are not connected to any channel[/]");
        return;
    }

    var members = await room.GetMembers();

    AnsiConsole.Write(new Rule($"Members for '{context.CurrentChannel}'")
    {
        Justification = Justify.Center,
        Style = Style.Parse("darkgreen")
    });

    foreach (var member in members)
    {
        AnsiConsole.MarkupLine("[bold yellow]{0}[/]", member);
    }

    AnsiConsole.Write(new Rule()
    {
        Justification = Justify.Center,
        Style = Style.Parse("darkgreen")
    });
}

static async Task ShowCurrentChannelHistory(ClientContext context)
{
    var room = context.ChannelClient.GetGrain<IChannelGrain>(context.CurrentChannel);

    if (!context.IsConnectedToChannel)
    {
        AnsiConsole.MarkupLine("[bold red]You are not connected to any channel[/]");
        return;
    }

    var history = await room.ReadHistory(1_000);

    AnsiConsole.Write(new Rule($"History for '{context.CurrentChannel}'")
    {
        Justification = Justify.Center,
        Style = Style.Parse("darkgreen")
    });

    foreach (var chatMsg in history)
    {
        AnsiConsole.MarkupLine("[[[dim]{0}[/]]] [bold yellow]{1}:[/] {2}",
            chatMsg.Created.LocalDateTime, chatMsg.From, chatMsg.Text);
    }

    AnsiConsole.Write(new Rule()
    {
        Justification = Justify.Center,
        Style = Style.Parse("darkgreen")
    });
}

async Task SendMessage(
    ClientContext context,
    string messageText)
{
    var room = context.ChannelClient.GetGrain<IChannelGrain>(context.CurrentChannel);
    var members = await room.GetMembers();
    var agentGrains = new List<IAgentGrain>();
    foreach (var member in members)
    {
        if (string.Equals(member, context.UserName, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        var agent = context.AgentClient.GetGrain<IAgentGrain>(member);
        agentGrains.Add(agent);
    }

    var chatHistory = await room.ReadHistory(1_000);
    // remove system message
    chatHistory = chatHistory.Where(x => x.From != "System").ToArray();
    var chatMsg = new ChatMsg(context.UserName, messageText);
    await room.Message(chatMsg);
    chatHistory = chatHistory.Append(chatMsg).ToArray();
    IEnumerable<IAgent> agents = agentGrains.Select(a => new AgentGrainAgent(a)).ToArray();
    var userAgent = new DefaultReplyAgent(context.UserName!, defaultReply: "It's your turn");
    var groupAdmin = AgentFactory.CreateGroupChatAdmin(openaiClient, "admin", deployModelName);
    var transitions = new List<Transition>();
    foreach (var agent in agents)
    {
        transitions.Add(Transition.Create(agent, userAgent));
        transitions.Add(Transition.Create(userAgent, agent));
    }

    var graph = new Graph(transitions);
    
    var group = new GroupChat(
        workflow: graph,
        admin: groupAdmin,
        members: agents.Append(userAgent).ToArray());

    while (true)
    {
        var nextMessages = await group.CallAsync(chatHistory, 1);
        var nextMessage = nextMessages.Last();
        if (nextMessage.From == userAgent.Name || !agents.Any(a => a.Name == nextMessage.From))
        {
            break;
        }
        else if (nextMessage is ChatMsg msg)
        {
            await room.Message(msg);
            chatHistory = chatHistory.Append(msg).ToArray();
        }
        else
        {
            throw new InvalidOperationException("Unexpected message type");
        }
    }
}

static async Task<ClientContext> JoinChannel(
    ClientContext context,
    string channelName)
{
    if (context.CurrentChannel is not null &&
        !string.Equals(context.CurrentChannel, channelName, StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.MarkupLine(
            "[bold olive]Leaving channel [/]{0}[bold olive] before joining [/]{1}",
            context.CurrentChannel, channelName);

        await LeaveChannel(context);
    }

    AnsiConsole.MarkupLine("[bold aqua]Joining channel [/]{0}", channelName);
    context = context with { CurrentChannel = channelName };
    await AnsiConsole.Status().StartAsync("Joining channel...", async ctx =>
    {
        var room = context.ChannelClient.GetGrain<IChannelGrain>(context.CurrentChannel);
        await room.Join(context.UserName!);
        var streamId = StreamId.Create("ChatRoom", context.CurrentChannel!);
        var stream =
            context.ChannelClient
                .GetStreamProvider("chat")
                .GetStream<ChatMsg>(streamId);

        // Subscribe to the stream to receive furthur messages sent to the chatroom
        await stream.SubscribeAsync(new StreamObserver(channelName));
    });
    AnsiConsole.MarkupLine("[bold aqua]Joined channel [/]{0}", context.CurrentChannel!);
    return context;
}

static async Task<ClientContext> LeaveChannel(ClientContext context)
{
    if (!context.IsConnectedToChannel)
    {
        AnsiConsole.MarkupLine("[bold red]You are not connected to any channel[/]");
        return context;
    }

    AnsiConsole.MarkupLine(
        "[bold olive]Leaving channel [/]{0}",
        context.CurrentChannel!);

    await AnsiConsole.Status().StartAsync("Leaving channel...", async ctx =>
    {
        var room = context.ChannelClient.GetGrain<IChannelGrain>(context.CurrentChannel);
        await room.Leave(context.UserName!);
        var streamId = StreamId.Create("ChatRoom", context.CurrentChannel!);
        var stream =
            context.ChannelClient
                .GetStreamProvider("chat")
                .GetStream<ChatMsg>(streamId);

        // Unsubscribe from the channel/stream since client left, so that client won't
        // receive future messages from this channel/stream.
        var subscriptionHandles = await stream.GetAllSubscriptionHandles();
        foreach (var handle in subscriptionHandles)
        {
            await handle.UnsubscribeAsync();
        }
    });

    AnsiConsole.MarkupLine("[bold olive]Left channel [/]{0}", context.CurrentChannel!);

    return context with { CurrentChannel = null };
}
