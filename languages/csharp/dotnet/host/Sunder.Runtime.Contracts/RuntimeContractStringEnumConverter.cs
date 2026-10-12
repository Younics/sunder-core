using System.Text.Json.Serialization;

namespace Sunder.Runtime.Contracts;

internal sealed class RuntimeContractStringEnumConverter<TEnum>()
    : JsonStringEnumConverter<TEnum>(namingPolicy: null, allowIntegerValues: false)
    where TEnum : struct, Enum;
