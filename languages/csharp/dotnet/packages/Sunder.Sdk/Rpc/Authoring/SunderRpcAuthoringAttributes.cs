using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Rpc;

/// <summary>Controls whether a source-authored RPC contract is package-local or publicly importable.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public enum SunderRpcExposure
{
    /// <summary>Exposes the contract only to roles in the owning package.</summary>
    Package,

    /// <summary>Exposes the contract for authorized cross-package use.</summary>
    Public,
}

/// <summary>Controls which packages may discover a source-authored RPC provider.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public enum SunderRpcProviderAudience
{
    /// <summary>Infers owner or public audience from the contract exposure.</summary>
    Inferred,

    /// <summary>Restricts the provider to the owning package.</summary>
    Owner,

    /// <summary>Allows authorized cross-package discovery.</summary>
    Public,
}

/// <summary>Declares an implementation-first RPC service contract.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcServiceAttribute : Attribute
{
    /// <summary>Creates a service declaration with the requested exposure.</summary>
    /// <param name="exposure">The package or public exposure. Defaults to the owning package only.</param>
    public SunderRpcServiceAttribute(SunderRpcExposure exposure = SunderRpcExposure.Package) => Exposure = exposure;

    /// <summary>Gets the declared contract exposure.</summary>
    public SunderRpcExposure Exposure { get; }

    /// <summary>Gets or sets an optional explicit contract identifier.</summary>
    public string? ContractId { get; set; }

    /// <summary>Gets or sets an optional explicit service identifier.</summary>
    public string? ServiceId { get; set; }

    /// <summary>Gets or sets whether to retain the fixed V1 JSON Schema dialect annotation in the descriptor.</summary>
    /// <remarks>Defaults to false. Use this to preserve an existing descriptor's exact bytes during source migration; it does not select another value profile or protocol.</remarks>
    public bool IncludeSchemaDialect { get; set; }

    /// <summary>Gets or sets ordinary source value types retained in this contract even when no current method references them.</summary>
    /// <remarks>Use this to preserve named definitions in an existing contract. Types use the same closed source-value validation and limits as method values.</remarks>
    public Type[] AdditionalDefinitions { get; set; } = [];
}

/// <summary>Declares the associated App's use of an owned service without repeating its wire identity.</summary>
/// <remarks>Package-private services default to required App use of all applicable actions. Public services have no automatic App use.
/// This attribute opts a public service in or overrides the private default. Actions default to all method-applicable actions.</remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcAppUseAttribute : Attribute
{
    /// <summary>Gets or sets whether App activation requires an authorized provider. Defaults to false.</summary>
    public bool Required { get; set; }

    /// <summary>Gets or sets a nonempty subset of discover, invoke and subscribe, or leaves the applicable actions inferred.</summary>
    public string[]? Actions { get; set; }
}

/// <summary>Declares the owning Runtime's consumer use of an owned service without repeating its wire identity.</summary>
/// <remarks>Services and provider registrations grant no automatic Runtime use. Explicit use defaults to optional readiness and all method-applicable actions.</remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcRuntimeUseAttribute : Attribute
{
    /// <summary>Gets or sets whether Runtime activation requires an authorized provider. Defaults to false.</summary>
    public bool Required { get; set; }

    /// <summary>Gets or sets a nonempty subset of discover, invoke and subscribe, or leaves the applicable actions inferred.</summary>
    public string[]? Actions { get; set; }
}

/// <summary>Binds a concrete class to an implementation-first RPC service declaration.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcProviderAttribute : Attribute
{
    /// <summary>Creates a provider override for a service declared by the same class.</summary>
    public SunderRpcProviderAttribute()
    {
    }

    /// <summary>Creates a provider binding for a separately declared service type.</summary>
    /// <param name="serviceType">The annotated service interface implemented by the provider.</param>
    public SunderRpcProviderAttribute(Type serviceType) => ServiceType = serviceType;

    /// <summary>Gets the separately declared service type, if any.</summary>
    public Type? ServiceType { get; }

    /// <summary>Gets or sets an optional explicit provider identifier.</summary>
    public string? ProviderId { get; set; }

    /// <summary>Gets or sets the provider audience override.</summary>
    public SunderRpcProviderAudience Audience { get; set; }
}

/// <summary>Identifies a local domain interface whose adapter implements a separately selected platform contract.</summary>
/// <remarks>This declaration conveys no contract ownership, version, hash, or signing authority. Generation requires an authenticated platform import.</remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcPlatformImportAttribute : Attribute
{
    /// <summary>Creates a domain-to-platform adapter declaration.</summary>
    /// <param name="contractId">The platform family to select explicitly in the contract lock.</param>
    /// <param name="adapterType">The public local adapter with a constructor accepting this interface and annotated unary methods.</param>
    public SunderRpcPlatformImportAttribute(string contractId, Type adapterType)
    {
        ContractId = contractId;
        AdapterType = adapterType;
    }

    /// <summary>Gets the platform contract family, without asserting a version or authority.</summary>
    public string ContractId { get; }

    /// <summary>Gets the local semantic adapter type.</summary>
    public Type AdapterType { get; }
}

/// <summary>Marks an instance method as an implementation-first RPC method.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcMethodAttribute : Attribute
{
    /// <summary>Creates an RPC method declaration using deterministic defaults.</summary>
    public SunderRpcMethodAttribute()
    {
    }

    /// <summary>Gets or sets an optional explicit method identifier.</summary>
    public string? MethodId { get; set; }

    /// <summary>Gets or sets an optional explicit request definition name.</summary>
    public string? RequestDefinitionName { get; set; }

    /// <summary>Gets or sets an optional explicit response or event definition name.</summary>
    public string? OutputDefinitionName { get; set; }

    /// <summary>Gets or sets the declared idempotency semantics.</summary>
    public SunderRpcMethodIdempotency Idempotency { get; set; }

    /// <summary>Gets or sets the contract error codes this method may return.</summary>
    public string[] Errors { get; set; } = [];
}

/// <summary>Declares one contract-wide domain error for an implementation-first RPC service.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcDomainErrorAttribute : Attribute
{
    /// <summary>Creates a domain error declaration.</summary>
    /// <param name="code">The stable contract-local error code.</param>
    public SunderRpcDomainErrorAttribute(string code) => Code = code;

    /// <summary>Gets the declared error code.</summary>
    public string Code { get; }

    /// <summary>Gets or sets the optional human-readable error description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the optional source type for structured error details.</summary>
    public Type? DetailsType { get; set; }
}

/// <summary>Assigns a stable descriptor definition name to a source type.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcDefinitionAttribute : Attribute
{
    /// <summary>Creates a named definition declaration.</summary>
    /// <param name="name">The stable descriptor definition name.</param>
    public SunderRpcDefinitionAttribute(string name) => Name = name;

    /// <summary>Gets the descriptor definition name.</summary>
    public string Name { get; }
}

/// <summary>Marks a single-property source type as a transparent RPC value wrapper.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcTransparentAttribute : Attribute
{
    /// <summary>Creates a transparent wrapper declaration.</summary>
    public SunderRpcTransparentAttribute()
    {
    }
}

/// <summary>Marks an integral CLR enum for numeric JSON encoding.</summary>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcNumericEnumAttribute : Attribute
{
    /// <summary>Creates a numeric-enum declaration.</summary>
    public SunderRpcNumericEnumAttribute()
    {
    }

    /// <summary>Gets or sets whether the descriptor includes the enum's minimum and maximum values as explicit bounds.</summary>
    /// <remarks>Defaults to true. Set false to preserve an existing numeric enum that declares only its closed value set.</remarks>
    public bool IncludeRangeBounds { get; set; } = true;
}

/// <summary>Declares the complete variants of an implementation-first RPC union.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcUnionAttribute : Attribute
{
    /// <summary>Creates a union declaration.</summary>
    /// <param name="variants">The complete ordered list of sealed variant types.</param>
    public SunderRpcUnionAttribute(params Type[] variants) => Variants = Array.AsReadOnly(variants.ToArray());

    /// <summary>Gets the immutable ordered list of declared variant types.</summary>
    public IReadOnlyList<Type> Variants { get; }
}

/// <summary>Declares the wire property used to discriminate tagged RPC union variants.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcDiscriminatorAttribute : Attribute
{
    /// <summary>Creates a discriminator declaration.</summary>
    /// <param name="propertyName">The exact wire property name.</param>
    public SunderRpcDiscriminatorAttribute(string propertyName) => PropertyName = propertyName;

    /// <summary>Gets the exact discriminator wire property name.</summary>
    public string PropertyName { get; }
}

/// <summary>Assigns the discriminator tag for one RPC union variant.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcVariantAttribute : Attribute
{
    /// <summary>Creates a variant declaration.</summary>
    /// <param name="tag">The exact discriminator tag.</param>
    public SunderRpcVariantAttribute(string tag) => Tag = tag;

    /// <summary>Gets the exact discriminator tag.</summary>
    public string Tag { get; }
}

/// <summary>Declares optional value-schema facets for a source-authored RPC type or member.</summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property
    | AttributeTargets.Parameter | AttributeTargets.ReturnValue,
    AllowMultiple = false,
    Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcFacetAttribute : Attribute
{
    /// <summary>Creates facet metadata with every facet absent.</summary>
    public SunderRpcFacetAttribute()
    {
    }

    /// <summary>Gets or sets the inclusive minimum string scalar count, or -1 when absent.</summary>
    public long MinLength { get; set; } = -1;

    /// <summary>Gets or sets the inclusive maximum string scalar count, or -1 when absent.</summary>
    public long MaxLength { get; set; } = -1;

    /// <summary>Gets or sets the inclusive minimum array item count, or -1 when absent.</summary>
    public long MinItems { get; set; } = -1;

    /// <summary>Gets or sets the inclusive maximum array item count, or -1 when absent.</summary>
    public long MaxItems { get; set; } = -1;

    /// <summary>Gets or sets the inclusive minimum object property count, or -1 when absent.</summary>
    public long MinProperties { get; set; } = -1;

    /// <summary>Gets or sets the inclusive maximum object property count, or -1 when absent.</summary>
    public long MaxProperties { get; set; } = -1;

    /// <summary>Gets or sets the inclusive maximum decoded byte count, or -1 when absent.</summary>
    public long MaxDecodedBytes { get; set; } = -1;

    /// <summary>Gets or sets the inclusive maximum big-integer digit count, or -1 when absent.</summary>
    public long MaxDigits { get; set; } = -1;

    /// <summary>Gets or sets the inclusive maximum decimal precision, or -1 when absent.</summary>
    public long MaxPrecision { get; set; } = -1;

    /// <summary>Gets or sets the inclusive maximum decimal scale, or -1 when absent.</summary>
    public long MaxScale { get; set; } = -1;

    /// <summary>Gets or sets the canonical inclusive numeric minimum.</summary>
    public string? Minimum { get; set; }

    /// <summary>Gets or sets the canonical inclusive numeric maximum.</summary>
    public string? Maximum { get; set; }

    /// <summary>Gets or sets the canonical exclusive numeric minimum.</summary>
    public string? ExclusiveMinimum { get; set; }

    /// <summary>Gets or sets the canonical exclusive numeric maximum.</summary>
    public string? ExclusiveMaximum { get; set; }

    /// <summary>Gets or sets the canonical exact numeric value.</summary>
    public string? ExactNumber { get; set; }

    /// <summary>Gets or sets the exact constant string value.</summary>
    public string? ConstantString { get; set; }

    /// <summary>Gets or sets a closed inline enumeration for a plain string value.</summary>
    /// <remarks>Absent by default. Values retain their declared order; null, empty, duplicate or oversized selections are rejected by source tooling.</remarks>
    public string[]? AllowedStrings { get; set; }

    /// <summary>Gets or sets whether an unconstrained JSON value must be an object without imposing a property-count bound.</summary>
    public bool JsonObjectOnly { get; set; }

    /// <summary>Gets or sets the optional schema title annotation.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the optional schema description annotation.</summary>
    public string? Description { get; set; }
}
