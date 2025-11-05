using System;
using System.Runtime.CompilerServices;

using Il2CppInterop.Runtime;

using Naninovel;

using BridgePlaybackSpot = Naninovel.Bridging.PlaybackSpot;

namespace ManosabaLoader;

public struct PlaybackSpotStruct
{
    public string scriptPath;
    public int lineIndex;
    public int inlineIndex;
    
    public static implicit operator PlaybackSpotIl2CppStruct(PlaybackSpotStruct managedStruct)
        => new()
        {
            scriptPath = IL2CPP.ManagedStringToIl2Cpp(managedStruct.scriptPath),
            lineIndex = managedStruct.lineIndex,
            inlineIndex = managedStruct.inlineIndex
        };
}

public struct PlaybackSpotIl2CppStruct
{
    public IntPtr scriptPath;
    public int lineIndex;
    public int inlineIndex;
    
    public static implicit operator PlaybackSpotStruct(PlaybackSpotIl2CppStruct il2CppStruct)
        => new()
        {
            scriptPath = IL2CPP.Il2CppStringToManaged(il2CppStruct.scriptPath),
            lineIndex = il2CppStruct.lineIndex,
            inlineIndex = il2CppStruct.inlineIndex
        };

    public static unsafe implicit operator PlaybackSpot(PlaybackSpotIl2CppStruct il2CppStruct)
        => new(IL2CPP.il2cpp_value_box(Il2CppClassPointerStore<PlaybackSpot>.NativeClassPtr, (IntPtr)Unsafe.AsPointer(ref il2CppStruct)));
    
    public static unsafe implicit operator BridgePlaybackSpot(PlaybackSpotIl2CppStruct il2CppStruct)
        => new(IL2CPP.il2cpp_value_box(Il2CppClassPointerStore<BridgePlaybackSpot>.NativeClassPtr, (IntPtr)Unsafe.AsPointer(ref il2CppStruct)));
}