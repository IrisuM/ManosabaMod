using System.Reflection;
using System.Runtime.InteropServices;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime;

using Naninovel.Bridging;

using Il2CppException = Il2CppInterop.Runtime.Il2CppException;
using ILS = Il2CppSystem;
using SYS = System;
using ILSRfl = Il2CppSystem.Reflection;

using IntPtr = System.IntPtr;

namespace ManosabaLoader.Utils;

public static class Il2CppEx
{
    private static BindingFlags BFlagsAll = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static SYS.Type TypeIl2CppToMonoDelegateReference = typeof(DelegateSupport).GetNestedType("Il2CppToMonoDelegateReference", BindingFlags.NonPublic);
    private static ConstructorInfo CtorIl2CppToMonoDelegateReference = TypeIl2CppToMonoDelegateReference.GetConstructor(BFlagsAll, null, [typeof(SYS.Delegate), typeof(IntPtr)], null)!;
    private static SYS.Type TypeMethodSignature = typeof(DelegateSupport).GetNestedType("MethodSignature", BindingFlags.NonPublic);
    private static ConstructorInfo CtorMethodSignatureIl2CppMethodInfo = TypeMethodSignature.GetConstructor(BFlagsAll, null, [typeof(ILSRfl.MethodInfo), typeof(bool)], null)!;
    private static ConstructorInfo CtorMethodSignatureSYSMethodInfo = TypeMethodSignature.GetConstructor(BFlagsAll, null, [typeof(MethodInfo), typeof(bool)], null)!;
    private static MethodInfo MethodGetOrCreateNativeToManagedTrampoline = typeof(DelegateSupport).GetMethod("GetOrCreateNativeToManagedTrampoline", BFlagsAll)!;
    
    public static TIl2Cpp ConvertDelegateDangerous<TIl2Cpp>(SYS.Delegate @delegate) where TIl2Cpp : Il2CppObjectBase
    {
        if (@delegate == null)
            return null;

        if (!typeof(ILS.Delegate).IsAssignableFrom(typeof(TIl2Cpp)))
            throw new SYS.ArgumentException($"{typeof(TIl2Cpp)} is not a delegate");

        var managedInvokeMethod = @delegate.GetType().GetMethod("Invoke")!;
        var parameterInfos = managedInvokeMethod.GetParameters();
        foreach (var parameterInfo in parameterInfos)
        {
            var parameterType = parameterInfo.ParameterType;
            if (parameterType.IsGenericParameter)
                throw new SYS.ArgumentException(
                    $"Delegate has unsubstituted generic parameter ({parameterType}) which is not supported");
        }

        var classTypePtr = Il2CppClassPointerStore.GetNativeClassPointer(typeof(TIl2Cpp));
        if (classTypePtr == IntPtr.Zero)
            throw new SYS.ArgumentException($"Type {typeof(TIl2Cpp)} has uninitialized class pointer");

        if (Il2CppClassPointerStore.GetNativeClassPointer(TypeIl2CppToMonoDelegateReference) == IntPtr.Zero)
            ClassInjector.RegisterTypeInIl2Cpp(TypeIl2CppToMonoDelegateReference);

        var il2CppDelegateType = ILS.Type.internal_from_handle(IL2CPP.il2cpp_class_get_type(classTypePtr));
        var nativeDelegateInvokeMethod = il2CppDelegateType.GetMethod("Invoke");

        var nativeParameters = nativeDelegateInvokeMethod.GetParameters();
        if (nativeParameters.Count != parameterInfos.Length)
            throw new SYS.ArgumentException(
                $"Managed delegate has {parameterInfos.Length} parameters, native has {nativeParameters.Count}, these should match");

        var signature = CtorMethodSignatureIl2CppMethodInfo.Invoke([nativeDelegateInvokeMethod, true]);
        var managedTrampoline = (SYS.Delegate)MethodGetOrCreateNativeToManagedTrampoline.Invoke(null, [signature, nativeDelegateInvokeMethod, managedInvokeMethod])!;

        var methodInfo = UnityVersionHandler.NewMethod();
        methodInfo.MethodPointer = Marshal.GetFunctionPointerForDelegate(managedTrampoline);
        methodInfo.ParametersCount = (byte)parameterInfos.Length;
        methodInfo.Slot = ushort.MaxValue;
        methodInfo.IsMarshalledFromNative = true;

        var delegateReference = (ILS.Object)CtorIl2CppToMonoDelegateReference.Invoke([@delegate, methodInfo.Pointer]);

        ILS.Delegate converted;
        if (UnityVersionHandler.MustUseDelegateConstructor)
        {
            converted = (((TIl2Cpp)SYS.Activator.CreateInstance(typeof(TIl2Cpp), delegateReference.Cast<ILS.Object>(),
                methodInfo.Pointer))!).Cast<ILS.Delegate>();
        }
        else
        {
            var nativeDelegatePtr = IL2CPP.il2cpp_object_new(classTypePtr);
            converted = new ILS.Delegate(nativeDelegatePtr);
        }

        converted.method_ptr = methodInfo.MethodPointer;
        converted.method_info = nativeDelegateInvokeMethod;
        converted.method = methodInfo.Pointer;
        converted.m_target = delegateReference;

        if (UnityVersionHandler.MustUseDelegateConstructor)
        {
            // U2021.2.0+ hack in case the constructor did the wrong thing anyway
            converted.invoke_impl = converted.method_ptr;
            converted.method_code = converted.m_target.Pointer;
        }

        return converted.Cast<TIl2Cpp>();
    }

    public static ILS.Nullable<T> CreateNullableFix<T>() where T : ILS.ValueType, new()
    {
        var instancePtr = IL2CPP.il2cpp_object_new(Il2CppClassPointerStore<Naninovel.Nullable<PlaybackSpot>>.NativeClassPtr);
        return new ILS.Nullable<T>(instancePtr);
    }
    
    public static unsafe ILS.Nullable<T> CreateNullableFix<T>(T value) where T : ILS.ValueType, new()
    {
        var args = stackalloc IntPtr[1];
        var unbox = IL2CPP.il2cpp_object_unbox(value.Pointer);
        args[0] = unbox;
        
        var instancePtr = IL2CPP.il2cpp_object_new(Il2CppClassPointerStore<Naninovel.Nullable<PlaybackSpot>>.NativeClassPtr);
                
        var exc = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke((IntPtr)typeof(ILS.Nullable<T>).GetField("NativeMethodInfoPtr__ctor_Public_Void_T_0", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!, IL2CPP.il2cpp_object_unbox(instancePtr), (void**) args, ref exc);
        Il2CppException.RaiseExceptionIfNecessary(exc);
                
        return new ILS.Nullable<T>(instancePtr);
    }
}