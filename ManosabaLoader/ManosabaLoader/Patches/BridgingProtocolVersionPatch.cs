using System.Collections.Generic;
using System.Reflection.Emit;

using HarmonyLib;

using Naninovel;
using Naninovel.Bridging;

namespace ManosabaLoader.Patches;

[HarmonyPatch]
public class BridgingProtocolVersionPatch
{
    [HarmonyPatch(typeof(Server), nameof(Server.HandleMessage))]
    [HarmonyPrefix]
    private static void Server_HandleMessage_Prefix(Message message)
    {
        if ((int)message.Type == 6)
            message.Type = MessageType.GotoRequested;
    }
}