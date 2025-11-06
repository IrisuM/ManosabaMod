namespace ManosabaLoader.BridgingProtocolAdapt;

public enum MessageType
{
    ServerAnnounced,
    MetadataUpdated,
    PlayerStatusChanged,
    PlaybackStatusChanged,
    SkipStatusChanged,
    RequestedUnknown1,
    GotoRequested,
    PlayRequested,
    StopRequested,
    SkipRequested,
    PauseRequested,
}