using Sunder.Runtime.LocalState;

namespace Sunder.Host.Supervisor;

internal sealed record RuntimeWorkerConnection(
    RuntimeConnectionInfo ConnectionInfo,
    RuntimeIpcEndpoint Endpoint,
    long WorkerEpoch);
