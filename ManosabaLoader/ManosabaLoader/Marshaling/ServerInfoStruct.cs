using System;
using System.Runtime.CompilerServices;

using Il2CppInterop.Runtime;

using Naninovel.Bridging;

namespace ManosabaLoader.Marshaling;

public struct ServerInfoStruct
{
    public string name;
    public string version;
    
    public static implicit operator ServerInfoIl2CppStruct(ServerInfoStruct managedStruct)
        => new()
        {
            name = IL2CPP.ManagedStringToIl2Cpp(managedStruct.name),
            version = IL2CPP.ManagedStringToIl2Cpp(managedStruct.version)
        };
}

public unsafe struct ServerInfoIl2CppStruct
{
    public IntPtr name;
    public IntPtr version;

    public static implicit operator ServerInfoStruct(ServerInfoIl2CppStruct il2CppStruct)
        => new()
        {
            name = IL2CPP.Il2CppStringToManaged(il2CppStruct.name),
            version = IL2CPP.Il2CppStringToManaged(il2CppStruct.version)
        };
    
    public static unsafe implicit operator ServerInfo(ServerInfoIl2CppStruct il2CppStruct)
        => new(IL2CPP.il2cpp_value_box(Il2CppClassPointerStore<ServerInfo>.NativeClassPtr, (IntPtr)Unsafe.AsPointer(ref il2CppStruct)));
}