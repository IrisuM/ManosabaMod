using System;
using System.Runtime.CompilerServices;

using Il2CppInterop.Runtime;

using Naninovel.Bridging;

using AdaptMessageType = ManosabaLoader.BridgingProtocolAdapt.MessageType;

namespace ManosabaLoader.Marshaling;

public struct MessageStruct
{
    public string id;
    public AdaptMessageType type;
    public string payload;
    
    public static implicit operator MessageIl2CppStruct(MessageStruct managedStruct)
        => new()
        {
            id = IL2CPP.ManagedStringToIl2Cpp(managedStruct.id),
            type = managedStruct.type,
            payload = IL2CPP.ManagedStringToIl2Cpp(managedStruct.payload)
        };
}

public unsafe struct MessageIl2CppStruct
{
    public IntPtr id;
    public AdaptMessageType type;
    public IntPtr payload;
    
    public static implicit operator MessageStruct(MessageIl2CppStruct il2CppStruct)
        => new()
        {
            id = IL2CPP.Il2CppStringToManaged(il2CppStruct.id),
            type = il2CppStruct.type,
            payload = IL2CPP.Il2CppStringToManaged(il2CppStruct.payload)
        };

    public static implicit operator Message(MessageIl2CppStruct il2CppStruct)
        => new(IL2CPP.il2cpp_value_box(Il2CppClassPointerStore<Message>.NativeClassPtr, (IntPtr)Unsafe.AsPointer(ref il2CppStruct)));
}