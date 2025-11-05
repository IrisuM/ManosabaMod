using System;
using System.Runtime.InteropServices;

using BepInEx.Logging;

using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

namespace ManosabaLoader.Utils;

public class NaninovelLoggerWrapper : Il2CppSystem.Object
{
    private ManualLogSource logger;
    
    public NaninovelLoggerWrapper(IntPtr pointer) : base(pointer)
    {
    }
    
    public NaninovelLoggerWrapper(ManualLogSource logger) : base(ClassInjector.DerivedConstructorPointer<NaninovelLoggerWrapper>())
    {
        ClassInjector.DerivedConstructorBody(this); 
        this.logger = logger;
    }

    public void Log(string message)
    {
        logger.LogInfo(message);
    }

    public void Warn(string message)
    {
        logger.LogWarning(message);
    }

    public void Err(string message)
    {
        logger.LogError(message);
    }
}

// public class NaninovelLoggerWrapper : Il2CppSystem.Object
// {
//     private IntPtr loggerPtr;
//     
//     public NaninovelLoggerWrapper(IntPtr pointer) : base(pointer)
//     {
//     }
//
//     [HideFromIl2Cpp]
//     public NaninovelLoggerWrapper(ManualLogSource logger) : base(ClassInjector.DerivedConstructorPointer<NaninovelLoggerWrapper>())
//     {
//         loggerPtr = GCHandle.ToIntPtr(GCHandle.Alloc(logger));
//     }
//     
//     ~NaninovelLoggerWrapper()
//     {
//         if (loggerPtr != IntPtr.Zero)
//         {
//             var handle = GCHandle.FromIntPtr(loggerPtr);
//             var logger = (ManualLogSource)handle.Target;
//             logger.LogInfo("Disposing logger wrapper and releasing logger handle.");
//             handle.Free();
//             loggerPtr = IntPtr.Zero;
//         }
//     }
//
//     public void Log(string message)
//     {
//         var logger = (ManualLogSource)GCHandle.FromIntPtr(loggerPtr).Target;
//         logger.LogInfo(message);
//     }
//
//     public void Warn(string message)
//     {
//         var logger = (ManualLogSource)GCHandle.FromIntPtr(loggerPtr).Target;
//         logger.LogWarning(message);
//     }
//
//     public void Err(string message)
//     {
//         var logger = (ManualLogSource)GCHandle.FromIntPtr(loggerPtr).Target;
//         logger.LogError(message);
//     }
// }