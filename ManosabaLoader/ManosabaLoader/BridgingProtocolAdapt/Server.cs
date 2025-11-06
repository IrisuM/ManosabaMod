using System;

using BepInEx.Logging;

using ManosabaLoader.Marshaling;
using ManosabaLoader.Utils;

using Naninovel;
using Naninovel.Bridging;

namespace ManosabaLoader.BridgingProtocolAdapt;

public class Server(IFiles files, ISerializer serde)
{
    private readonly ManualLogSource logger = Logger.CreateLogSource($"{MyPluginInfo.PLUGIN_NAME}.{nameof(Server)}");
    private readonly Transport transport = new("", files, serde);
    
    public event Action<PlaybackSpotStruct> OnGotoRequested;
    public event Action OnPlayRequested;
    public event Action<bool> OnSkipRequested;
    public event Action OnStopRequested;

    public void Start(ServerInfo serverInfo)
    {
        SendMessage(MessageType.ServerAnnounced, serde.Serialize(serverInfo));
        transport.Listen(Recipient.Server, Il2CppEx.ConvertDelegateDangerous<Il2CppSystem.Action<Message>>(HandleMessage));
    }
    
    public void NotifyMetadataUpdated()
    {
        SendMessage(MessageType.MetadataUpdated, null);
    }

    public void NotifyPlayerStatusChanged(bool ready)
    {
        SendMessage(MessageType.PlayerStatusChanged, serde.Serialize(ready));
    }
    
    public void NotifyPlaybackStatusChanged(PlaybackStatus status)
    {
        SendMessage(MessageType.PlaybackStatusChanged, serde.Serialize(status));
    }
    
    public void NotifySkipStatusChanged(bool skipEnabled)
    {
        SendMessage(MessageType.SkipStatusChanged, serde.Serialize(skipEnabled));
    }

    private void SendMessage(MessageType type, string payload)
    {
        var message = new MessageStruct
        {
            type = type,
            payload = payload
        };
        logger.LogDebug($"Constructing outgoing message: Type={message.type}({(int)message.type}), payload={message.payload}");
        transport.Send(Recipient.Client, (MessageIl2CppStruct)message);
    }
    
    private void HandleMessage(MessageIl2CppStruct il2cppMessage)
    {
        var message = (MessageStruct)il2cppMessage;
        logger.LogDebug($"Handling income message: Type={message.type}({(int)message.type}), Payload={message.payload ?? "<NULL>"}");
        
        switch (message.type)
        {
            case MessageType.GotoRequested when serde.TryDeserializeValueTypeFix<Naninovel.Bridging.PlaybackSpot>(message.payload, out var playbackSpot):
                OnGotoRequested?.Invoke(new PlaybackSpotIl2CppStruct(playbackSpot));
                break;
            case MessageType.PlayRequested:
                OnPlayRequested?.Invoke();
                break;
            case MessageType.StopRequested:
                OnStopRequested?.Invoke();
                break;
            case MessageType.SkipRequested when serde.TryDeserialize<bool>(message.payload, out var flag):
                OnSkipRequested?.Invoke(flag); 
                break;
            case MessageType.PauseRequested:
                logger.LogWarning("PauseRequested message received, but pause functionality is not implemented.");
                break;
        }
    }
}