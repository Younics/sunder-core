using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Services;

/// <summary>Controls whether an ordinary service belongs to its package or exposes a reviewed public API.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public enum SunderServiceExposure
{
    /// <summary>Available within the owning package.</summary>
    Package,
    /// <summary>Available through explicitly selected and authorized public APIs.</summary>
    Public,
}

/// <summary>Declares an ordinary service. Declared public async methods form its generated interface and transport.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderServiceAttribute(SunderServiceExposure exposure = SunderServiceExposure.Package) : Attribute
{
    /// <summary>Gets the declared service exposure.</summary>
    public SunderServiceExposure Exposure { get; } = exposure;
    /// <summary>Optional stable service key; use when preserving an identity or resolving a naming collision.</summary>
    public string? Key { get; set; }
    /// <summary>Gets or sets a stable contract identifier override.</summary>
    public string? ContractId { get; set; }
    /// <summary>Gets or sets a stable service identifier override.</summary>
    public string? ServiceId { get; set; }
    /// <summary>Gets or sets whether to preserve the fixed V1 schema annotation.</summary>
    public bool IncludeSchemaDialect { get; set; }
    /// <summary>Gets or sets explicitly retained value definitions for public compatibility.</summary>
    public Type[] AdditionalDefinitions { get; set; } = [];
}

/// <summary>Declares intentional method semantics or stable wire names; ordinary async methods need no annotation.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderMethodAttribute : Attribute
{
    /// <summary>Gets or sets the stable method identifier override.</summary>
    public string? MethodId { get; set; }
    /// <summary>Gets or sets the request schema definition name.</summary>
    public string? RequestDefinitionName { get; set; }
    /// <summary>Gets or sets the result or stream event schema definition name.</summary>
    public string? OutputDefinitionName { get; set; }
    /// <summary>Gets or sets the declared effect semantics.</summary>
    public SunderServiceIdempotency Idempotency { get; set; }
    /// <summary>Gets or sets the selected service error codes.</summary>
    public string[] Errors { get; set; } = [];
    /// <summary>Gets or sets the generated RPC caller's requested duration in seconds (1–2100), or zero for the Host default. Host limits, parent deadlines and permissions still apply.</summary>
    public int TimeoutSeconds { get; set; }
    /// <summary>Gets or sets whether this provider's content-output server stream completes when its response-registration authority retires. Defaults to false.</summary>
    /// <remarks>For replayable watches whose consumers resnapshot or resume after completion. Only authenticated retirement of the current response registration is eligible; caller cancellation, unsafe dispatch, and business failures remain failures. This provider-local choice is not a wire descriptor or client deadline policy.</remarks>
    public bool CompleteOnResponseContentRetirement { get; set; }
}

/// <summary>Documents effect semantics. Idempotency never enables automatic call replay.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public enum SunderServiceIdempotency
{
    /// <summary>Makes no idempotency promise.</summary>
    None,
    /// <summary>Declares idempotent effects without enabling retries.</summary>
    Idempotent,
}

/// <summary>Controls discovery of an explicitly bound service provider.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public enum SunderServiceAudience
{
    /// <summary>Infers audience from the service exposure.</summary>
    Inferred,
    /// <summary>Restricts discovery to the owning package.</summary>
    Owner,
    /// <summary>Allows authorized public discovery.</summary>
    Public,
}

/// <summary>Binds a concrete class to a service contract, or overrides its shorthand provider. Repeat for distinct contracts implemented by the same class.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderServiceProviderAttribute : Attribute
{
    /// <summary>Overrides a service provider declared by the same class.</summary>
    public SunderServiceProviderAttribute() { }
    /// <summary>Binds the class to an explicitly declared service interface.</summary>
    public SunderServiceProviderAttribute(Type serviceType) => ServiceType = serviceType;
    /// <summary>Gets the declared extension interface, when supplied.</summary>
    public Type? ServiceType { get; }
    /// <summary>Gets or sets the stable provider identifier; multiple providers require distinct explicit identifiers.</summary>
    public string? ProviderId { get; set; }
    /// <summary>Gets or sets the discovery audience.</summary>
    public SunderServiceAudience Audience { get; set; }
}

/// <summary>Identifies a local domain interface whose semantic adapter implements an explicitly selected platform service.</summary>
/// <remarks>This metadata grants no platform ownership, consumer permission or publication authority.</remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderPlatformImportAttribute(string contractId, Type adapterType) : Attribute
{
    /// <summary>Gets the selected platform contract family.</summary>
    public string ContractId { get; } = contractId;
    /// <summary>Gets the local adapter type.</summary>
    public Type AdapterType { get; } = adapterType;
}

/// <summary>Declares a service's bounded, stable domain error.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderServiceErrorAttribute(string code) : Attribute
{
    /// <summary>Gets the stable error code.</summary>
    public string Code { get; } = code;
    /// <summary>Gets or sets the error description.</summary>
    public string? Description { get; set; }
    /// <summary>Gets or sets the structured error-detail type.</summary>
    public Type? DetailsType { get; set; }
}

/// <summary>Preserves a deliberate named value identity.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderValueAttribute(string name) : Attribute
{
    /// <summary>Gets the stable schema definition name.</summary>
    public string Name { get; } = name;
}

/// <summary>Includes a public property in a declared service value even when ordinary JSON serialization ignores it.</summary>
/// <remarks>This controls service transfer only. Existing JSON/persistence exclusions remain in effect outside generated service codecs.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderServiceMemberAttribute : Attribute;

/// <summary>Marks a direct nullable service-reference member as an availability-optional dependency.</summary>
/// <remarks>Generated transfer maps safe exact absence to null without coupling dependency retirement to the outer call. Live references remain exact-bound; caller, callee and required-participant cancellation still apply. Nullability alone does not enable this policy.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderOptionalServiceReferenceAttribute : Attribute;

/// <summary>Declares constraints that ordinary CLR types cannot express.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.ReturnValue, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderValueConstraintsAttribute : Attribute
{
    /// <summary>Gets or sets the minimum string scalar length, or -1 when absent.</summary>
    public long MinLength { get; set; } = -1;
    /// <summary>Gets or sets the maximum string scalar length, or -1 when absent.</summary>
    public long MaxLength { get; set; } = -1;
    /// <summary>Gets or sets the minimum collection length, or -1 when absent.</summary>
    public long MinItems { get; set; } = -1;
    /// <summary>Gets or sets the maximum collection length, or -1 when absent.</summary>
    public long MaxItems { get; set; } = -1;
    /// <summary>Gets or sets the minimum object property count, or -1 when absent.</summary>
    public long MinProperties { get; set; } = -1;
    /// <summary>Gets or sets the maximum object property count, or -1 when absent.</summary>
    public long MaxProperties { get; set; } = -1;
    /// <summary>Gets or sets the maximum decoded byte count, or -1 when absent.</summary>
    public long MaxDecodedBytes { get; set; } = -1;
    /// <summary>Gets or sets the maximum big-integer digit count, or -1 when absent.</summary>
    public long MaxDigits { get; set; } = -1;
    /// <summary>Gets or sets the maximum decimal precision, or -1 when absent.</summary>
    public long MaxPrecision { get; set; } = -1;
    /// <summary>Gets or sets the maximum decimal scale, or -1 when absent.</summary>
    public long MaxScale { get; set; } = -1;
    /// <summary>Gets or sets a canonical inclusive numeric minimum.</summary>
    public string? Minimum { get; set; }
    /// <summary>Gets or sets a canonical inclusive numeric maximum.</summary>
    public string? Maximum { get; set; }
    /// <summary>Gets or sets a canonical exclusive numeric minimum.</summary>
    public string? ExclusiveMinimum { get; set; }
    /// <summary>Gets or sets a canonical exclusive numeric maximum.</summary>
    public string? ExclusiveMaximum { get; set; }
    /// <summary>Gets or sets a canonical exact numeric value.</summary>
    public string? ExactNumber { get; set; }
    /// <summary>Gets or sets an exact string value.</summary>
    public string? ConstantString { get; set; }
    /// <summary>Gets or sets a closed, ordered inline string enumeration.</summary>
    public string[]? AllowedStrings { get; set; }
    /// <summary>Gets or sets whether an otherwise arbitrary JSON value must be an object.</summary>
    public bool JsonObjectOnly { get; set; }
    /// <summary>Gets or sets a schema title.</summary>
    public string? Title { get; set; }
    /// <summary>Gets or sets a schema description.</summary>
    public string? Description { get; set; }
}

/// <summary>Overrides the owning App's service-use policy, including an ordinary concrete public service's optional default.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderAppUseAttribute : Attribute
{
    /// <summary>Gets or sets whether a public service grants its owning App access; false explicitly suppresses the concrete-service default.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Gets or sets required readiness; defaults to optional.</summary>
    public bool Required { get; set; }
    /// <summary>Gets or sets the nonempty applicable action subset, or null to infer it.</summary>
    public string[]? Actions { get; set; }
}

/// <summary>Declares Runtime consumption of the owning service or an explicitly selected imported service.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRuntimeUseAttribute : Attribute
{
    /// <summary>Declares use of the annotated owned service.</summary>
    public SunderRuntimeUseAttribute() { }

    /// <summary>Declares use of a canonical generated service from the validated imported contract set.</summary>
    public SunderRuntimeUseAttribute(Type serviceType)
        => ServiceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));

    /// <summary>Gets the imported service type, or null for the annotated owned service.</summary>
    public Type? ServiceType { get; }

    /// <summary>Gets or sets required readiness; defaults to optional.</summary>
    public bool Required { get; set; }
    /// <summary>Gets or sets the nonempty applicable action subset, or null to infer it.</summary>
    public string[]? Actions { get; set; }
}

/// <summary>Marks a single-property value as a deliberate transparent wire value.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderTransparentValueAttribute : Attribute;

/// <summary>Preserves a closed integral enum encoding.</summary>
[AttributeUsage(AttributeTargets.Enum, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderNumericEnumAttribute : Attribute
{
    /// <summary>Gets or sets whether the closed enum also emits explicit range bounds.</summary>
    public bool IncludeRangeBounds { get; set; } = true;
}

/// <summary>Declares the complete supported variants of a value union.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderUnionAttribute(params Type[] variants) : Attribute
{
    /// <summary>Gets an immutable copy of the ordered variant types.</summary>
    public IReadOnlyList<Type> Variants { get; } = Array.AsReadOnly(variants.ToArray());
}

/// <summary>Declares a union's stable discriminator property.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderDiscriminatorAttribute(string propertyName) : Attribute
{
    /// <summary>Gets the exact discriminator property name.</summary>
    public string PropertyName { get; } = propertyName;
}

/// <summary>Declares a variant's stable discriminator value.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderVariantAttribute(string tag) : Attribute
{
    /// <summary>Gets the exact discriminator value.</summary>
    public string Tag { get; } = tag;
}
