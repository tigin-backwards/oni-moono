using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OniMoono.Tweaks;

namespace OniMoono.Host.GameTweaks
{
    /// Game tweak `fast-serialization` (opt-in): the game's serializer (KSerialization) writes
    /// each object through its SerializationTemplate, reading every field with FieldInfo.GetValue.
    /// That boxes every number and struct it saves; in a large colony the boxing alone was 1.3 s
    /// of a 3.5 s save, the main-thread pause of every autosave.
    ///
    /// This compiles each template's field list once into a writer that reads the fields typed and
    /// writes plain values (integers, floats, booleans, int enums, Vector2I, Vector2, Vector3,
    /// colours) with the same BinaryWriter calls the game's Helper.WriteValue makes for them;
    /// every other field is boxed and handed to Helper.WriteValue as before. The callbacks, the
    /// properties, the custom serializer and the error reporting are the template's own, in its
    /// order. The bytes written are the game's (checked: SaveTest).
    [GameMember(GameUse.Uses, "System.Void Debug::LogError(System.Object)")]
    [GameMember(GameUse.Mirrors, "System.Void KSerialization.Helper::WriteValue(System.IO.BinaryWriter,KSerialization.TypeInfo,System.Object)", "2ae90126aff89120 linux-744825 win-744825", "dd15fef912b3eb70 linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Uses, "System.Void KSerialization.IOHelper::WriteSingleFast(System.IO.BinaryWriter,System.Single)")]
    [GameMember(GameUse.Uses, "System.Reflection.FieldInfo KSerialization.SerializationTemplate/SerializationField::field")]
    [GameMember(GameUse.Uses, "KSerialization.TypeInfo KSerialization.SerializationTemplate/SerializationField::typeInfo")]
    [GameMember(GameUse.Uses, "System.Reflection.PropertyInfo KSerialization.SerializationTemplate/SerializationProperty::property")]
    [GameMember(GameUse.Uses, "KSerialization.TypeInfo KSerialization.SerializationTemplate/SerializationProperty::typeInfo")]
    [GameMember(GameUse.Prefix | GameUse.Replaces, "System.Void KSerialization.SerializationTemplate::SerializeData(System.Object,System.IO.BinaryWriter)", "e283fa414b66f4a2 linux-744825 win-744825", "9678fe80f01a053c linux-761211 linux-761654 linux-762107 linux-762230 linux-763023 win-761211 win-761654 win-762107 win-762230 win-763023")]
    [GameMember(GameUse.Uses, "System.Reflection.MethodInfo KSerialization.SerializationTemplate::customSerialize")]
    [GameMember(GameUse.Uses, "System.Reflection.MethodInfo KSerialization.SerializationTemplate::onSerialized")]
    [GameMember(GameUse.Uses, "System.Reflection.MethodInfo KSerialization.SerializationTemplate::onSerializing")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<KSerialization.SerializationTemplate/SerializationField> KSerialization.SerializationTemplate::serializableFields")]
    [GameMember(GameUse.Uses, "System.Collections.Generic.List`1<KSerialization.SerializationTemplate/SerializationProperty> KSerialization.SerializationTemplate::serializableProperties")]
    [GameMember(GameUse.Uses, "System.Type KSerialization.SerializationTemplate::serializableType")]
    [GameMember(GameUse.Uses, "KSerialization.SerializationTypeInfo KSerialization.TypeInfo::info")]
    internal static class FastSerialization
    {
        public const string Name = GameTweakNames.FastSerialization;

        /// Test runs only: off restores the game's own path at run time (for comparing saves).
        public static bool Enabled = true;

        static Type templateType = null!, helperType = null!, ioHelperType = null!, typeInfoType = null!;
        static FieldInfo fieldsField = null!, propertiesField = null!, onSerializingField = null!, onSerializedField = null!, customSerializeField = null!, serializableTypeField = null!;
        static FieldInfo fieldInfoOfEntry = null!, typeInfoOfEntry = null!, propertyOfEntry = null!, typeInfoOfPropertyEntry = null!, infoOfTypeInfo = null!;
        static MethodInfo writeValue = null!, writeSingleFast = null!, gameLogError = null!;

        sealed class Plan
        {
            public Action<object, BinaryWriter> Fields = null!;
            public (PropertyInfo Property, object TypeInfo)[] Properties = [];
            public MethodInfo? OnSerializing, OnSerialized, CustomSerialize;
            public string TypeName = "";
        }

        static readonly ConditionalWeakTable<object, Plan> plans = new();

        public static void Install(Harmony harmony)
        {
            templateType = AccessTools.TypeByName("KSerialization.SerializationTemplate");
            helperType = AccessTools.TypeByName("KSerialization.Helper");
            ioHelperType = AccessTools.TypeByName("KSerialization.IOHelper");
            typeInfoType = AccessTools.TypeByName("KSerialization.TypeInfo");
            fieldsField = AccessTools.Field(templateType, "serializableFields");
            propertiesField = AccessTools.Field(templateType, "serializableProperties");
            onSerializingField = AccessTools.Field(templateType, "onSerializing");
            onSerializedField = AccessTools.Field(templateType, "onSerialized");
            customSerializeField = AccessTools.Field(templateType, "customSerialize");
            serializableTypeField = AccessTools.Field(templateType, "serializableType");
            var fieldEntry = AccessTools.Inner(templateType, "SerializationField");
            var propertyEntry = AccessTools.Inner(templateType, "SerializationProperty");
            fieldInfoOfEntry = AccessTools.Field(fieldEntry, "field");
            typeInfoOfEntry = AccessTools.Field(fieldEntry, "typeInfo");
            propertyOfEntry = AccessTools.Field(propertyEntry, "property");
            typeInfoOfPropertyEntry = AccessTools.Field(propertyEntry, "typeInfo");
            infoOfTypeInfo = AccessTools.Field(typeInfoType, "info");
            writeValue = AccessTools.Method(helperType, "WriteValue", [typeof(BinaryWriter), typeInfoType, typeof(object)]);
            writeSingleFast = AccessTools.Method(ioHelperType, "WriteSingleFast", [typeof(BinaryWriter), typeof(float)]);
            // The game's Debug (not UnityEngine's), as the template reports.
            gameLogError = AccessTools.Method(AccessTools.TypeByName("Debug"), "LogError", [typeof(object)]);
            // Every member read here must exist: a renamed one stops the start-up rather than the
            // first save.
            (string Name, object? Member)[] members = [("SerializationTemplate.serializableFields", fieldsField),
                ("SerializationTemplate.serializableProperties", propertiesField), ("SerializationTemplate.onSerializing", onSerializingField),
                ("SerializationTemplate.onSerialized", onSerializedField), ("SerializationTemplate.customSerialize", customSerializeField),
                ("SerializationTemplate.serializableType", serializableTypeField), ("SerializationField.field", fieldInfoOfEntry),
                ("SerializationField.typeInfo", typeInfoOfEntry), ("SerializationProperty.property", propertyOfEntry),
                ("SerializationProperty.typeInfo", typeInfoOfPropertyEntry), ("TypeInfo.info", infoOfTypeInfo),
                ("Helper.WriteValue", writeValue), ("IOHelper.WriteSingleFast", writeSingleFast), ("Debug.LogError", gameLogError)];
            foreach (var (member, found) in members)
                if (found == null) throw new MissingMemberException($"{Name}: the game has no {member}");
            harmony.Patch(AccessTools.Method(templateType, "SerializeData", [typeof(object), typeof(BinaryWriter)]),
                prefix: new HarmonyMethod(typeof(FastSerialization), nameof(SerializeData)));
        }

        /// SerializationTemplate.SerializeData(object, BinaryWriter), in the template's order.
        static bool SerializeData(object __instance, object obj, BinaryWriter writer)
        {
            if (!Enabled) return true;
            var plan = plans.GetValue(__instance, MakePlan);
            plan.OnSerializing?.Invoke(obj, null);
            plan.Fields(obj, writer);
            foreach (var (property, typeInfo) in plan.Properties)
            {
                try
                {
                    var value = property.GetValue(obj, null);
                    writeValue.Invoke(null, [writer, typeInfo, value]);
                }
                catch (Exception e)
                {
                    throw Failure("property", property.Name, plan.TypeName, e is TargetInvocationException { InnerException: { } inner } ? inner : e);
                }
            }
            plan.CustomSerialize?.Invoke(obj, [writer]);
            plan.OnSerialized?.Invoke(obj, null);
            return false;
        }

        /// The template's own report: logged, then thrown with the cause inside.
        static Exception Failure(string kind, string member, string typeName, Exception cause)
        {
            string text = $"Error occurred while serializing {kind} {member} on template {typeName}";
            gameLogError.Invoke(null, [text]);
            return new ArgumentException(text, cause);
        }

        static Plan MakePlan(object template)
        {
            var type = (Type)serializableTypeField.GetValue(template)!;
            var properties = new List<(PropertyInfo, object)>();
            foreach (var entry in (IEnumerable)propertiesField.GetValue(template)!)
                properties.Add(((PropertyInfo)propertyOfEntry.GetValue(entry)!, typeInfoOfPropertyEntry.GetValue(entry)!));
            return new Plan
            {
                Fields = CompileFields(type, (IEnumerable)fieldsField.GetValue(template)!),
                Properties = properties.ToArray(),
                OnSerializing = (MethodInfo?)onSerializingField.GetValue(template),
                OnSerialized = (MethodInfo?)onSerializedField.GetValue(template),
                CustomSerialize = (MethodInfo?)customSerializeField.GetValue(template),
                TypeName = type.Name,
            };
        }

        static Action<object, BinaryWriter> CompileFields(Type type, IEnumerable fields)
        {
            var objArg = Expression.Parameter(typeof(object), "obj");
            var writerArg = Expression.Parameter(typeof(BinaryWriter), "writer");
            // The object as its own type: a struct is read in its box, not copied.
            var target = type.IsValueType ? (Expression)Expression.Unbox(objArg, type) : Expression.Convert(objArg, type);
            var body = new List<Expression>();
            ParameterExpression? local = null;
            if (!type.IsValueType)
            {
                local = Expression.Variable(type, "self");
                body.Add(Expression.Assign(local, target));
            }
            Expression Self() => type.IsValueType ? target : local!;
            var failure = typeof(FastSerialization).GetMethod(nameof(Failure), BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var entry in fields)
            {
                var field = (FieldInfo)fieldInfoOfEntry.GetValue(entry)!;
                var typeInfo = typeInfoOfEntry.GetValue(entry)!;
                int kind = Convert.ToInt32(infoOfTypeInfo.GetValue(typeInfo)) & 63;
                var read = Expression.Field(field.IsStatic ? null : Self(), field);
                var write = PlainWrite(kind, field.FieldType, read, writerArg)
                            ?? Expression.Call(writeValue, writerArg, Expression.Constant(typeInfo, typeInfoType), Expression.Convert(read, typeof(object)));
                var caught = Expression.Parameter(typeof(Exception), "e");
                body.Add(Expression.TryCatch(Expression.Block(typeof(void), write),
                    Expression.Catch(caught, Expression.Throw(Expression.Call(failure, Expression.Constant("field"), Expression.Constant(field.Name), Expression.Constant(type.Name), caught)))));
            }
            if (body.Count == 0) body.Add(Expression.Empty());
            var block = local == null ? Expression.Block(body) : Expression.Block([local], body);
            return Expression.Lambda<Action<object, BinaryWriter>>(block, objArg, writerArg).Compile();
        }

        /// What Helper.WriteValue writes for a plain value of this kind, read typed; null when the
        /// field is not one the direct path covers exactly (then WriteValue writes it).
        [MirrorsGame("System.Void KSerialization.Helper::WriteValue(System.IO.BinaryWriter,KSerialization.TypeInfo,System.Object)")]
        static Expression? PlainWrite(int kind, Type fieldType, Expression value, Expression writer)
        {
            Expression Write(Type parameter, Expression v) => Expression.Call(writer, typeof(BinaryWriter).GetMethod(nameof(BinaryWriter.Write), [parameter])!, v);
            Expression Single(Expression v) => Expression.Call(writeSingleFast, writer, v);
            Expression Member(Expression v, string name) => Expression.Field(v, name);
            switch (kind)
            {
                case 1 when fieldType == typeof(sbyte): return Write(typeof(sbyte), value);
                case 2 when fieldType == typeof(byte): return Write(typeof(byte), value);
                case 3 when fieldType == typeof(bool): return Write(typeof(byte), Expression.Condition(value, Expression.Constant((byte)1), Expression.Constant((byte)0)));
                case 4 when fieldType == typeof(short): return Write(typeof(short), value);
                case 5 when fieldType == typeof(ushort): return Write(typeof(ushort), value);
                case 6 when fieldType == typeof(int): return Write(typeof(int), value);
                case 7 when fieldType == typeof(uint): return Write(typeof(uint), value);
                case 8 when fieldType == typeof(long): return Write(typeof(long), value);
                case 9 when fieldType == typeof(ulong): return Write(typeof(ulong), value);
                case 10 when fieldType == typeof(float): return Single(value);
                case 11 when fieldType == typeof(double): return Write(typeof(double), value);
                // (int)value unboxes only an int-based enum.
                case 13 when fieldType.IsEnum && Enum.GetUnderlyingType(fieldType) == typeof(int): return Write(typeof(int), Expression.Convert(value, typeof(int)));
                case 14 when fieldType.FullName == "Vector2I":
                    return Expression.Block(Write(typeof(int), Member(value, "x")), Write(typeof(int), Member(value, "y")));
                case 15 when fieldType == typeof(UnityEngine.Vector2):
                    return Expression.Block(Single(Member(value, "x")), Single(Member(value, "y")));
                case 16 when fieldType == typeof(UnityEngine.Vector3):
                    return Expression.Block(Single(Member(value, "x")), Single(Member(value, "y")), Single(Member(value, "z")));
                case 23 when fieldType == typeof(UnityEngine.Color):
                {
                    Expression Channel(string c) => Write(typeof(byte), Expression.Convert(Expression.Multiply(Member(value, c), Expression.Constant(255f)), typeof(byte)));
                    return Expression.Block(Channel("r"), Channel("g"), Channel("b"), Channel("a"));
                }
                default: return null;
            }
        }
    }
}
