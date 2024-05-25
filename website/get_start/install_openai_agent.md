To install `ChatRoom.OpenAI` agent, run the following command:
```bash
dotnet tool install --global ChatRoom.OpenAI
```

After the agent is installed, you can start the agent with the following command:
```bash
chatroom-openai
```

## Configuration
You can configure `ChatRoom.OpenAI` agent by creating a configuration file and pass it to the agent. Below is an example of a configuration file for the agent:

[!code-json[](../../configuration/chatroom-openai.json)]

After creating the configuration and saving it to `chatroom-openai.json`, you can start the agent with the following command:

> [!NOTE]
> Before starting the agent, make sure the `ChatRoom.Client` is running.
> You can refer to the [use client](./use_client.md) for more information.

```bash
chatroom-openai --room "room" --port 30000 --config chatroom-openai.json
```

## Usage
After agent is started, you can chat with the agent from the client by adding this agent to the current channel and send message to it. You can refer to the [use client](./use_client.md) for more information.