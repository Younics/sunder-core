/**
 * Supported runtime ABI for Sunder-generated code. This is not an authoring recipe.
 * Exported signatures and their referenced types remain compatibility commitments.
 * This entry point is browser-safe and contains no build-time source generator.
 */
export { createApiToken, bindApiContext, getApiBindingContext, apiTokenOwner } from "./api";
export type { ApiBindingContext, ApiIdentity } from "./api";
export { createServiceContract, assertServiceOptions } from "./services";
export { createServiceToken, createServiceCatalog } from "./service-reference";
export type { OptionalServiceReferenceMember } from "./service-reference";
export { RpcMarshallingContext, ServiceDomainError, setServiceErrorDetails, getServiceErrorDetails } from "./resources";
export { RpcDomainError } from "./types";
export { validateRpcInstance } from "./descriptor-v1";
