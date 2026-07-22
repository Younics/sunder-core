using Sunder.Sdk.Runtime;

namespace Sunder.Package.Quickstart;

public sealed record GreetRequest(string Name);

public sealed record GreetResponse(string Message, int InvocationCount);

public static class QuickstartOperations
{
    public static PackageRuntimeOperation<GreetRequest, GreetResponse> Greet { get; }
        = new("greeting.create");
}
