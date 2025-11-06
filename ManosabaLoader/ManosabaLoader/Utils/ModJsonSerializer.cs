using System;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;

using Il2CppSystem.Runtime.InteropServices;

using Naninovel;

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

using BindingFlags = System.Reflection.BindingFlags;

namespace ManosabaLoader.Utils;

public class ModJsonSerializer : Il2CppSystem.Object
{
    private static Lazy<ModJsonSerializer> shared = new(() => new ModJsonSerializer());
    public static ModJsonSerializer Shared => shared.Value;
    
    public ModJsonSerializer(IntPtr pointer) : base(pointer) { }
    public ModJsonSerializer() : base(ClassInjector.DerivedConstructorPointer<ModJsonSerializer>()) => ClassInjector.DerivedConstructorBody(this);

    private static readonly JsonSerializerSettings settings = new()
    {
        ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy()
        }.Cast<IContractResolver>(),
        Formatting = Formatting.None,
        NullValueHandling = NullValueHandling.Ignore,
        MissingMemberHandling = MissingMemberHandling.Ignore,
        DefaultValueHandling = DefaultValueHandling.Include
    };

    public string Serialize(Il2CppSystem.Object poco) => JsonConvert.SerializeObject(poco, Formatting.None, settings);
    public string Serialize(Il2CppSystem.Object poco, Il2CppSystem.Type type) => JsonConvert.SerializeObject(poco, type, Formatting.None, settings);
    public Il2CppSystem.Object Deserialize(string serialized, Il2CppSystem.Type type) => JsonConvert.DeserializeObject(serialized, type, settings);
}

public static class ModJsonSerializerExtensions
{
    private static class TryDeserializePointerCache<T>
    {
        public static readonly IntPtr pointer = 
            (IntPtr)typeof(SerializerExtensions).GetNestedType("MethodInfoStoreGeneric_TryDeserialize_Public_Static_Boolean_ISerializer_String_byref_T_0`1", BindingFlags.NonPublic)!
                .MakeGenericType(typeof(T))!
                .GetField("Pointer", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null)!;
    }
    
    public static unsafe bool TryDeserializeValueTypeFix<T>(this ISerializer serializer, string serialized, out T poco) where T : Il2CppSystem.ValueType
    {
        var numPtr = stackalloc IntPtr[3];
        numPtr[0] = IL2CPP.Il2CppObjectBaseToPtr(serializer);
        numPtr[1] = IL2CPP.ManagedStringToIl2Cpp(serialized);

        var newObjPtr = IL2CPP.il2cpp_object_new(Il2CppClassPointerStore<T>.NativeClassPtr);
        
        numPtr[2] = IL2CPP.il2cpp_object_unbox(newObjPtr);
        var exc = IntPtr.Zero;
        
        var num2 = IL2CPP.il2cpp_runtime_invoke(TryDeserializePointerCache<T>.pointer, IntPtr.Zero, (void**) numPtr, ref exc);
        Il2CppException.RaiseExceptionIfNecessary(exc);
        
        poco = (T)typeof(T).GetConstructor([typeof(IntPtr)])!.Invoke([newObjPtr]);
        return *(bool*) IL2CPP.il2cpp_object_unbox(num2);
    }
}