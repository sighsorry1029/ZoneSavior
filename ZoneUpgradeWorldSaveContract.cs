using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace ZoneSavior;

/// <summary>Recognizes the empty-chunk algorithm, not the release or build containing it.</summary>
internal static class ZoneUpgradeWorldSaveContract
{
    // Fingerprint of only the reviewed detector, mapping removal, file cleanup and shared state.
    // Metadata tokens, assembly versions, MVIDs and branch encodings are deliberately excluded.
    private const string Expected = "B1953042E43C44374CC58A6DCF63F56E421EE35E0B1448F76975CB2F528DEB3F";
    private static readonly Dictionary<Assembly, (string Fingerprint, MethodBase[] Methods)> Cache = new();
    // Installed Harmony exposes Framework Reflection.Emit types; resolve that boundary explicitly.
    private static readonly MethodInfo ReadInstructions = typeof(PatchProcessor).GetMethods()
        .Single(method => method.Name == "GetOriginalInstructions" && method.GetParameters().Length == 2 &&
            !method.GetParameters()[1].ParameterType.IsByRef);
    private static readonly FieldInfo Labels = AccessTools.Field(typeof(CodeInstruction), "labels");

    internal static bool Recognizes(Assembly assembly, out string detail)
    {
        if (!Cache.TryGetValue(assembly, out var contract))
        {
            var helper = assembly.GetType("UpgradeWorld.ChunkSaveFix", true)!;
            var methods = new List<MethodBase>
            {
                AccessTools.DeclaredMethod(helper, "IsEmpty") ?? throw new MissingMethodException(helper.FullName, "IsEmpty"),
                helper.TypeInitializer ?? throw new MissingMethodException(helper.FullName, ".cctor")
            };
            foreach (var name in new[] { "FindEmptiedChunks", "DropEmptiedChunks", "DeleteEmptiedChunkFiles" })
            {
                var type = assembly.GetType("UpgradeWorld." + name, true)!;
                if (type.TypeInitializer != null)
                    throw new InvalidOperationException(type.FullName + " has an unrecognized static initializer.");
                if (type.GetCustomAttributesData().Any(attribute => attribute.AttributeType.Name == "HarmonyArgument"))
                    throw new InvalidOperationException(type.FullName + " has an unrecognized Harmony argument mapping.");
                methods.Add(AccessTools.DeclaredMethod(type, name == "DropEmptiedChunks" ? "Postfix" : "Prefix")
                    ?? throw new MissingMethodException(type.FullName, "save patch"));
            }
            var text = new StringBuilder();
            var fields = helper.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var field in fields.OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                if (field.IsDefined(typeof(ThreadStaticAttribute), false) || field.IsDefined(typeof(ContextStaticAttribute), false))
                    throw new InvalidOperationException("Empty-chunk state is no longer shared between save threads.");
                text.AppendLine(Member(field) + "|" + field.Attributes);
            }
            foreach (var method in methods) AppendMethod(text, method);
            using var sha = SHA256.Create();
            var fingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString().Replace("\r\n", "\n")))).Replace("-", "");
            contract = (fingerprint, methods.ToArray());
            Cache.Add(assembly, contract);
        }
        detail = "empty-chunk contract=" + contract.Fingerprint;
        // The cached original bodies are immutable, but detours on their helpers are not.
        foreach (var method in contract.Methods)
        {
            var patches = Harmony.GetPatchInfo(method);
            if (patches == null || patches.Owners.Count == 0) continue;
            detail += "; patched implementation: " + Member(method) + " owners=" + string.Join(",", patches.Owners);
            return false;
        }
        return contract.Fingerprint == Expected;
    }

    private static void AppendMethod(StringBuilder text, MethodBase method)
    {
        var body = method.GetMethodBody() ?? throw new InvalidOperationException("No managed body: " + Member(method));
        text.AppendLine(Member(method) + "|" + method.Attributes + "|" + method.GetMethodImplementationFlags());
        foreach (var parameter in method.GetParameters())
        {
            text.AppendLine("arg|" + parameter.Name + "|" + parameter.Attributes);
            if (parameter.GetCustomAttributesData().Any(attribute => attribute.AttributeType.Namespace == "HarmonyLib"))
                throw new InvalidOperationException("Unrecognized Harmony argument mapping: " + Member(method));
        }
        if (method.GetCustomAttributesData().Any(attribute => attribute.AttributeType.Name == "HarmonyArgument"))
            throw new InvalidOperationException("Unrecognized Harmony argument mapping: " + Member(method));
        text.AppendLine("locals|" + body.InitLocals + "|" + string.Join(",", body.LocalVariables.Select(local => TypeName(local.LocalType) + ":" + local.IsPinned)));
        var instructions = (List<CodeInstruction>)ReadInstructions.Invoke(null, new object?[] { method, null })!;
        var labels = new Dictionary<object, int>();
        var index = 0;
        foreach (var instruction in instructions)
        {
            foreach (var label in (IEnumerable)Labels.GetValue(instruction)!) labels[label] = index;
            if (instruction.opcode != OpCodes.Nop) index++;
        }
        foreach (var instruction in instructions)
        {
            foreach (var block in instruction.blocks)
                text.AppendLine("exception|" + block.blockType + "|" + (block.catchType == null ? "" : TypeName(block.catchType)));
            if (instruction.opcode == OpCodes.Nop) continue;
            var op = instruction.opcode.Name!;
            var operand = instruction.operand;
            // Normalize short branches, short operands and the common argument/local/int macros.
            if (op.EndsWith(".s", StringComparison.Ordinal)) op = op.Substring(0, op.Length - 2);
            foreach (var macro in new[] { "ldarg.", "ldloc.", "stloc.", "ldc.i4." })
                if (op.StartsWith(macro, StringComparison.Ordinal))
                {
                    var suffix = op.Substring(macro.Length);
                    if (suffix == "m1" || int.TryParse(suffix, out _))
                    { operand = suffix == "m1" ? -1 : int.Parse(suffix, CultureInfo.InvariantCulture); op = macro.TrimEnd('.'); }
                    break;
                }
            text.AppendLine(op + "|" + Operand(operand, labels));
        }
    }

    private static string Operand(object? operand, Dictionary<object, int> labels) => operand switch
    {
        null => "",
        Label label => "branch:" + labels[label],
        Label[] targets => "switch:" + string.Join(",", targets.Select(target => labels[target])),
        LocalBuilder local => local.LocalIndex.ToString(CultureInfo.InvariantCulture),
        ParameterInfo parameter => (parameter.Position + (parameter.Member is MethodBase method && !method.IsStatic ? 1 : 0)).ToString(CultureInfo.InvariantCulture),
        MemberInfo member => Member(member),
        string value => "string:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)),
        IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("Unrecognized IL operand: " + operand.GetType().FullName)
    };

    private static string Member(MemberInfo member)
    {
        if (member is Type type) return TypeName(type);
        var prefix = TypeName(member.DeclaringType!) + "::" + member.Name;
        if (member is FieldInfo field) return prefix + ":" + TypeName(field.FieldType);
        if (member is MethodBase method)
            return prefix + (method.IsGenericMethod ? "<" + string.Join(",", method.GetGenericArguments().Select(TypeName)) + ">" : "") +
                "(" + string.Join(",", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType))) + ")" +
                ":" + (method is MethodInfo info ? TypeName(info.ReturnType) : "ctor");
        throw new InvalidOperationException("Unrecognized IL member: " + member);
    }

    private static string TypeName(Type type)
    {
        if (type.HasElementType) return TypeName(type.GetElementType()!) +
            (type.IsByRef ? "&" : type.IsPointer ? "*" : "[" + new string(',', type.GetArrayRank() - 1) + "]");
        if (type.IsGenericParameter) return (type.DeclaringMethod == null ? "!" : "!!") + type.GenericParameterPosition;
        var name = type.IsGenericType ? type.GetGenericTypeDefinition().FullName : type.FullName;
        return type.Assembly.GetName().Name + ":" + name +
            (type.IsGenericType ? "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">" : "");
    }
}
