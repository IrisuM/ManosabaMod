using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem;
using Il2CppSystem.IO;
using Il2CppSystem.Reflection;
using Naninovel;
using Naninovel.Metadata;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Playables;

namespace ManosabaLoader
{
    public static class ModMetadataGenerator
    {
        public static System.Action<string> ModMetadataGeneratorLogMessage;
        public static System.Action<string> ModMetadataGeneratorLogDebug;
        public static System.Action<string> ModMetadataGeneratorLogWarning;
        public static System.Action<string> ModMetadataGeneratorLogError;

        public static Project GenerateProjectMetadata()
        {
            var meta = new Project();
            var cfg = ProjectConfigurationProvider.LoadOrDefault<ScriptsConfiguration>();
            meta.EntryScript = cfg.StartGameScript;
            meta.TitleScript = cfg.TitleScript;
            ModMetadataGeneratorLogMessage("Processing commands...");
            meta.Commands = GenerateCommandsMetadata();
            ModMetadataGeneratorLogMessage("Processing resources...");
            meta.Resources = GenerateResourcesMetadata();
            ModMetadataGeneratorLogMessage("Processing actors...");
            meta.Actors = GenerateActorsMetadata();
            ModMetadataGeneratorLogMessage("Processing variables...");
            meta.Variables = GenerateVariablesMetadata();
            ModMetadataGeneratorLogMessage("Processing functions...");
            meta.Functions = GenerateFunctionsMetadata();
            ModMetadataGeneratorLogMessage("Processing constants...");
            meta.Constants = GenerateConstantsMetadata();
            meta.Syntax = Compiler.Syntax;
            return meta;
        }

        public static Il2CppReferenceArray<Actor> GenerateActorsMetadata()
        {
            var actors = new List<Actor>();
            var chars = Configuration.GetOrDefault<CharactersConfiguration>().Metadata.ToDictionary();
            foreach (var kv in chars)
            {
                var charActor = new Actor
                {
                    Id = kv.Key,
                    Description = kv.Value.HasName ? kv.Value.DisplayName : "",
                    Type = kv.Value.Loader.PathPrefix,
                    Appearances = FindAppearances(kv.Key, kv.Value.Loader.PathPrefix, kv.Value.Implementation)
                };
                actors.Add(charActor);
            }
            var backs = Configuration.GetOrDefault<BackgroundsConfiguration>().Metadata.ToDictionary();
            foreach (var kv in backs)
            {
                var backActor = new Actor
                {
                    Id = kv.Key,
                    Type = kv.Value.Loader.PathPrefix,
                    Appearances = FindAppearances(kv.Key, kv.Value.Loader.PathPrefix, kv.Value.Implementation)
                };
                actors.Add(backActor);
            }
            var choiceHandlers = Configuration.GetOrDefault<ChoiceHandlersConfiguration>().Metadata.ToDictionary();
            foreach (var kv in choiceHandlers)
            {
                var choiceHandlerActor = new Actor
                {
                    Id = kv.Key,
                    Type = kv.Value.Loader.PathPrefix
                };
                actors.Add(choiceHandlerActor);
            }
            var printers = Configuration.GetOrDefault<TextPrintersConfiguration>().Metadata.ToDictionary();
            foreach (var kv in printers)
            {
                var printerActor = new Actor
                {
                    Id = kv.Key,
                    Type = kv.Value.Loader.PathPrefix
                };
                actors.Add(printerActor);
            }
            return actors.ToArray();

            string[] FindAppearances(string actorId, string pathPrefix, string actorImplementation)
            {
                ModMetadataGeneratorLogDebug(string.Format("FindAppearances actorId:{0} pathPrefix:{1} actorImplementation:{2}", actorId, pathPrefix, actorImplementation));
                var appearances = new List<string>();



                return appearances.ToArray();
            }
        }
        private static Nest ResolveNestMeta(Il2CppSystem.Type commandType)
        {
            if (!Il2CppType.From(typeof(Naninovel.Command.INestedHost)).IsAssignableFrom(commandType)) return null;
            return new() { Required = commandType.GetCustomAttribute<RequireNestedAttribute>() != null };
        }
        private static Branch ResolveBranchMeta(Il2CppSystem.Type commandType)
        {
            var branch = commandType.GetCustomAttribute<BranchAttribute>();
            if (branch is null) return null;
            return new() { Traits = branch.Traits, SwitchRoot = branch.SwitchRoot, Endpoint = branch.Endpoint };
        }
        private static Il2CppSystem.Reflection.FieldInfo[] GetParameterFields(Il2CppSystem.Type commandType)
        {
            return commandType.GetFields(Il2CppSystem.Reflection.BindingFlags.Public | Il2CppSystem.Reflection.BindingFlags.Instance)
                .Where(x => !x.GetCustomAttributes<Il2CppSystem.ObsoleteAttribute>().ToArray().Any())
                .Where(f => f.FieldType.GetInterface(nameof(ICommandParameter)) != null).ToArray();
        }
        private static ValueContext[] GetValueContext(Il2CppSystem.Reflection.MemberInfo member)
        {
            var valueAttr = FindAttribute(false);
            if (valueAttr is null) return null;
            if (valueAttr is EndpointContextAttribute)
                return new[] {
                    new ValueContext { Type = ValueContextType.Endpoint, SubType = Constants.EndpointScript },
                    new ValueContext { Type = ValueContextType.Endpoint, SubType = Constants.EndpointLabel }
                };
            return FindAttribute(true) is { } namedValueAttr
                ? new[] { GetValue(valueAttr), GetValue(namedValueAttr) }
                : new[] { GetValue(valueAttr) };

            ValueContext GetValue(ParameterContextAttribute attr) =>
                new() { Type = attr.Type, SubType = attr.SubType };
            ParameterContextAttribute FindAttribute(bool namedValue) =>
                FindFieldLevelContext(namedValue) ?? FindClassLevelContext(namedValue);
            ParameterContextAttribute FindClassLevelContext(bool namedValue) =>
                member.ReflectedType?.GetCustomAttributes<ParameterContextAttribute>().ToArray()
                    .Where(a => a.ParameterId == member.Name).FirstOrDefault(a => OfSingleOrNamed(a, namedValue));
            ParameterContextAttribute FindFieldLevelContext(bool namedValue) =>
                member.GetCustomAttributes<ParameterContextAttribute>().ToArray().FirstOrDefault(a => OfSingleOrNamed(a, namedValue));
            bool OfSingleOrNamed(ParameterContextAttribute a, bool namedValue) => a.Index < 0 || a.Index == (namedValue ? 1 : 0);
        }
        private static bool TryResolveValueType(Il2CppSystem.Type type, out Naninovel.Metadata.ValueType result)
        {
            var nullableName = typeof(INullable<>).Name;
            var valueTypeName = type.GetInterface(nullableName)?.GetGenericArguments()[0].Name;
            switch (valueTypeName)
            {
                case nameof(System.String):
                case nameof(NullableString):
                case nameof(LocalizableText):
                    result = Naninovel.Metadata.ValueType.String;
                    return true;
                case nameof(System.Int32):
                case nameof(NullableInteger):
                    result = Naninovel.Metadata.ValueType.Integer;
                    return true;
                case nameof(System.Single):
                case nameof(NullableFloat):
                    result = Naninovel.Metadata.ValueType.Decimal;
                    return true;
                case nameof(System.Boolean):
                case nameof(NullableBoolean):
                    result = Naninovel.Metadata.ValueType.Boolean;
                    return true;
            }
            result = default;
            return false;
        }
        private static Parameter ExtractParameterMetadata(CommandLocalization locale, Il2CppSystem.Reflection.FieldInfo field)
        {
            var l10n = locale.Parameters?.ToArray().FirstOrDefault(p => p.Id == field.Name);
            var nullableName = typeof(INullable<>).Name;
            var namedName = typeof(INamed<>).Name;
            var meta = new Parameter
            {
                Id = field.Name,
                Alias = string.IsNullOrWhiteSpace(l10n?.Alias)
                    ? field.GetCustomAttribute<Naninovel.Command.ParameterAliasAttribute>().Alias
                    : l10n.Alias,
                Required = field.GetCustomAttribute<Naninovel.Command.RequiredParameterAttribute>() != null,
                Localizable = field.FieldType == Il2CppType.From(typeof(LocalizableTextParameter)),
                DefaultValue = field.GetCustomAttribute<Naninovel.Command.ParameterDefaultValueAttribute>().Value,
                ValueContext = GetValueContext(field),
                Documentation = new Documentation()
            };
            meta.Nameless = meta.Alias == Naninovel.Command.NamelessParameterAlias;
            if (TryResolveValueType(field.FieldType, out var valueType))
                meta.ValueContainerType = ValueContainerType.Single;
            else if (GetInterface(nameof(System.Collections.IEnumerable)) != null) SetListValue();
            else SetNamedValue();
            meta.ValueType = valueType;
            return meta;

            Il2CppSystem.Type GetInterface(string name) => field.FieldType.GetInterface(name);

            Il2CppSystem.Type GetNullableType() => GetInterface(nullableName).GetGenericArguments()[0];

            void SetListValue()
            {
                var elementType = GetNullableType().GetGenericArguments()[0];
                var namedElementType = elementType.BaseType?.GetGenericArguments()[0];
                if (namedElementType?.GetInterface(nameof(INamedValue)) != null)
                {
                    meta.ValueContainerType = ValueContainerType.NamedList;
                    var namedType = namedElementType.GetInterface(namedName).GetGenericArguments()[0];
                    TryResolveValueType(namedType, out valueType);
                }
                else
                {
                    meta.ValueContainerType = ValueContainerType.List;
                    TryResolveValueType(elementType, out valueType);
                }
            }

            void SetNamedValue()
            {
                meta.ValueContainerType = ValueContainerType.Named;
                var namedType = GetNullableType().GetInterface(namedName).GetGenericArguments()[0];
                TryResolveValueType(namedType, out valueType);
            }
        }
        private static Parameter[] GenerateParametersMetadata(Il2CppSystem.Type commandType, CommandLocalization locale)
        {
            var result = new List<Parameter>();
            foreach (var fieldInfo in GetParameterFields(commandType))
                if (!IsIgnored(fieldInfo))
                    result.Add(ExtractParameterMetadata(locale, fieldInfo));
            return result.ToArray();

            bool IsIgnored(Il2CppSystem.Reflection.FieldInfo i) => IsIgnoredViaField(i) || IsIgnoredViaClass(i);
            bool IsIgnoredViaField(Il2CppSystem.Reflection.FieldInfo i) => i.GetCustomAttribute<IgnoreParameterAttribute>() != null;
            bool IsIgnoredViaClass(Il2CppSystem.Reflection.FieldInfo i) => i.ReflectedType?.GetCustomAttributes<IgnoreParameterAttribute>().ToArray().Any(a => a.ParameterId == i.Name) ?? false;
        }
        public static Il2CppReferenceArray<Naninovel.Metadata.Command> GenerateCommandsMetadata(LiteralMap<Il2CppSystem.Type> commands)
        {
            var commandsMeta = new List<Naninovel.Metadata.Command>();
            foreach(var pair in commands)
            {
                var commandType = pair.Value;

                Compiler.Commands.TryGetValue(commandType.Name, out var locale);
                var metadata = new Naninovel.Metadata.Command
                {
                    Id = commandType.Name,
                    Alias = !string.IsNullOrWhiteSpace(locale.Alias) ? locale.Alias
                        : commandType.GetCustomAttribute<Naninovel.Command.CommandAliasAttribute>().Alias,
                    Localizable = Il2CppType.From(typeof(Naninovel.Command.ILocalizable)).IsAssignableFrom(commandType),
                    Nest = ResolveNestMeta(commandType),
                    Branch = ResolveBranchMeta(commandType),
                    Documentation = new Documentation(),
                    Parameters = GenerateParametersMetadata(commandType, locale)
                };
                commandsMeta.Add(metadata);
            }
            return commandsMeta.OrderBy(c => string.IsNullOrEmpty(c.Alias) ? c.Id : c.Alias).ToArray();
        }
        public static Il2CppReferenceArray<Naninovel.Metadata.Command> GenerateCommandsMetadata()
        {
            return GenerateCommandsMetadata(Naninovel.Command.CommandTypes);
        }
        public static Constant[] GenerateConstantsMetadata(LiteralMap<Il2CppSystem.Type> commands, Il2CppSystem.Collections.Generic.List<ExpressionFunction> functions)
        {
            var enumTypes = new HashSet<Il2CppSystem.Type>();
            foreach (var command_pair in commands)
            {
                var command = command_pair.Value;
                if (command.GetCustomAttribute<ConstantContextAttribute>() is { } cmdAttr && cmdAttr.EnumType != null)
                    enumTypes.Add(cmdAttr.EnumType);
                foreach (var param in GetParameterFields(command))
                    if (param.GetCustomAttribute<ConstantContextAttribute>() is { } paramAttr && paramAttr.EnumType != null)
                        enumTypes.Add(paramAttr.EnumType);
            }
            foreach (var fn in functions)
                foreach (var param in fn.Method.GetParameters())
                    if (param.GetCustomAttributes(Il2CppType.From(typeof(ConstantContextAttribute)),false).FirstOrDefault() as ConstantContextAttribute is { } paramAttr && paramAttr.EnumType != null)
                        enumTypes.Add(paramAttr.EnumType);

            var constants = new List<Constant>();
            foreach (var type in enumTypes)
            {
                var values = Il2CppSystem.Enum.GetNames(type);
                if (Compiler.Constants.TryGetValue(type.Name, out var l10n))
                    for (int i = 0; i < values.Length; i++)
                        if (l10n.Values.ToArray().FirstOrDefault(v => v.Value.EqualsFastIgnoreCase(values[i])) is var cv)
                            if (!string.IsNullOrWhiteSpace(cv.Alias))
                                values[i] = cv.Alias;
                constants.Add(new() { Name = type.Name, Values = values });
            }

            var chars = Configuration.GetOrDefault<CharactersConfiguration>();
            constants.Add(CreatePoseConstant(Constants.CharacterType, Constants.WildcardType, chars.SharedPoses.ToArray().Select(p => p.Name)));
            foreach (var kv in chars.Metadata.ToDictionary())
                if (kv.Value.Poses.Count > 0)
                    constants.Add(CreatePoseConstant(Constants.CharacterType, kv.Key, kv.Value.Poses.ToArray().Select(p => p.Name)));

            var backs = Configuration.GetOrDefault<BackgroundsConfiguration>();
            constants.Add(CreatePoseConstant(Constants.BackgroundType, Constants.WildcardType, backs.SharedPoses.ToArray().Select(p => p.Name)));
            foreach (var kv in backs.Metadata.ToDictionary())
                if (kv.Value.Poses.Count > 0)
                    constants.Add(CreatePoseConstant(Constants.BackgroundType, kv.Key, kv.Value.Poses.ToArray().Select(p => p.Name)));

            return constants.ToArray();

            Constant CreatePoseConstant(string actorType, string actorId, IEnumerable<string> poses)
            {
                var name = $"Poses/{actorType}/{actorId}";
                return new() { Name = name, Values = poses.ToArray() };
            }
        }
        public static Il2CppReferenceArray<Constant> GenerateConstantsMetadata()
        {
            return GenerateConstantsMetadata(Naninovel.Command.CommandTypes, ExpressionFunctions.Resolve());
        }
        public static Function[] GenerateFunctionsMetadata(IEnumerable<ExpressionFunction> functions)
        {
            return functions.Select(fn => new Function
            {
                Name = fn.Id,
                Documentation = new Documentation(),
                Parameters = fn.Method.GetParameters().ToArray().Select(GenerateParameterMetadata).ToArray()
            }).ToArray();

            FunctionParameter GenerateParameterMetadata(Il2CppSystem.Reflection.ParameterInfo info)
            {
                return new()
                {
                    Name = info.Name,
                    Type = ResolveParameterType(info.ParameterType),
                    Context = GetContext(info),
                    Variadic = info.IsDefined(Il2CppType.From(typeof(Il2CppSystem.ParamArrayAttribute)), false)
                };
            }

            Naninovel.Metadata.ValueType ResolveParameterType(Il2CppSystem.Type valueType)
            {
                if (valueType.IsArray) valueType = valueType.GetElementType();
                if (valueType == Il2CppType.From(typeof(Il2CppSystem.String))) return Naninovel.Metadata.ValueType.String;
                if (valueType == Il2CppType.From(typeof(Il2CppSystem.Boolean))) return Naninovel.Metadata.ValueType.Boolean;
                if (valueType == Il2CppType.From(typeof(Il2CppSystem.Int32))) return Naninovel.Metadata.ValueType.Integer;
                return Naninovel.Metadata.ValueType.Decimal;
            }

            ValueContext GetContext(Il2CppSystem.Reflection.ParameterInfo info)
            {
                var attr = info.GetCustomAttributes(Il2CppType.From(typeof(ParameterContextAttribute)), false).FirstOrDefault() as ParameterContextAttribute;
                if (attr is null) return null;
                return new()
                {
                    Type = attr.Type,
                    SubType = attr.SubType
                };
            }
        }
        internal static Il2CppReferenceArray<Function> GenerateFunctionsMetadata()
        {
            var functions = ExpressionFunctions.Resolve();
            return GenerateFunctionsMetadata(functions.ToArray());
        }

        internal static Il2CppReferenceArray<Naninovel.Metadata.Resource> GenerateResourcesMetadata()
        {
            var resources = new List<Naninovel.Metadata.Resource>();

            return resources.ToArray();
        }

        public static Il2CppStringArray GenerateVariablesMetadata()
        {
            var config = Configuration.GetOrDefault<CustomVariablesConfiguration>();
            List<string> variables = new List<string>();
            foreach(var p in config.PredefinedVariables)
            {
                variables.Add(p.Name);
            }
            return variables.ToArray();
        }
    }
}
