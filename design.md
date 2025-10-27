### ApplicationContext
- *current name... don't love it*
- current definition for an application, which includes code + configuration
- an application id can have multiple versions... each one is seen as a separate context
- for example, my-function-app is deployed. That's v1. Then I fix something and deploy it again. That's v2. They're seen as distinct context from the system's perspective.
- similarly, without any deployment, if I change some configuration, that'd have to be a separate context.
- we'd have to rely on infrastructure to help us here. That doesn't currently exist. Big caveat.

### WebHost
- the app entry point
- ASP.NET Core application that is responsible for hosting services like http listener, grpc, etc.
- responsible for reacting to incoming connections/requests and taking actions to create inner `JobHost`s that will run applications
- for example, when assigned an application to run, it will create an internal `JobHost` for that application and start it
- the long-term goal is for `WebHost` to be able to host multiple `JobHosts` side-by-side, each representing a different application context. However, this is tricky with the way that our environment settings "leak" today. But we want to design for this.

### WorkerController
- an external (*sometimes*) component that is responsible for managing the lifecycle of a worker process
- responsible for starting the worker process and supplying it the ip/port of a `WebHost` to connect to
- needs some signal from infrastructure to know when to start a worker process and scale, etc. It doesn't have those smarts -- it only knows how to read worker configs and start them with the appropriate urls
- today the process lifetime is all managed in-line with the grpc worker channel, but this needs to be abstracted away in order to support workers running on separate compute
- for first iteration, this will be an `IHostedService` in the `WebHost` itself, but will only be interacted with via an interface that we can eventually use with an external service (in Flex, for example)
- goal is to keep interactions with the `WebHost` minimal -- with most behavior driven by the worker process itself
- worker management **and monitoring** are now managed here, rather than the `WebHost`.

### JobHostManager
- component in the `WebHost` that is responsible for managing the lifecycle of `JobHost`s
- manages many `JobHost`s, each representing a different application context
- current design, realistically, it's only managing one (other than brief restart overlaps)

### GrpcWorkerStream
- representation of a single grpc bidirectional stream from an external worker
- creates everything needed to run a worker, interfaces with the `JobHostManager`, etc.
- maintains the state/lifecycle of the worker -- prevents bad lifecycle requests (StartStream only called once), etc.
- is first to act on any incoming grpc messages from the worker
- Maintains the `BidirectionalChannel` (which implements `IWorkerChannel`) that is passed into the `JobHost` for communication with the worker.

### JobHost (or FunctionApplication?)
- Note: possibly renaming this to `FunctionApplication` to remove confusion with current `JobHost`
- a single `JobHost` represents a single application context
- a single `JobHost` can have multiple workers running in it (think `FUNCTIONS_WORKER_PROCESS_COUNT`)
- unlike today, the idea is that workers can come-and-go on a single `JobHost` as needed by the infrastructure (i.e. it's not set at `WebHost` startup time)
- unlike current design, `JobHost` is only started once a connection is made and function metadata is received from the worker. This fixes several "chicken-and-egg" issues we have with bundles, extensions, etc.
- function metadata, application details, etc, will not change within the lifetime of a `JobHost`. If anything is changed, a new `JobHost` will be created.
- represents a single application context and where things like Listeners, Triggers, etc run. 
- effectively maps to a single WebJobs apps
- runs in its own DI sub-container with some shared services from the `WebHost` 

### IWorker
- representation of a worker inside the `JobHost`
- responsible for running the actual functions in the application context
- interacts with the worker via the `IWorkerChannel` interface, which in the case of grpc is provided by the `BidirectionalChannel`
- when invoked, functions flow from Triggers -> JobHost -> IWorker -> IWorkerChannel -> GrpcWorkerStream -> (via grpc) -> Worker
- responses return back the same way: Worker -> (via grpc) ->  GrpcWorkerStream -> IWorkerChannel -> IWorker -> JobHost -> Triggers
- only meant to handle *Worker-specific* messages, like function invocations or rpc logs sent from the worker
- when a new grpc connection is made, the flow is: `GrpcWorkerStream` -> `JobHostManager` -> `GetOrAddJobHost(appContext)` -> `AddWorker()`. This means that if it's the first worker for an application, a new JobHost is created. If a JobHost already exists, this current worker is added to it and invocations are load-balanced.

### Placeholders
- the `WebHost` is aware of placeholders (`JobHost` doesn't know what a placeholder is, however) and treats them slightly differently than normal applications:
- every placeholder connection is treated as **the same application context**. 
- this means that every placeholder will be running in the same `JobHost`
- when in placeholder mode, the `JobHost` is created with a special `IWorkerResolver` that multi-casts invocations (for warmup) rather than load-balancing them
- this allows for vastly simplified placeholder management as its no longer 1:1 with `WebHost`
- when specialized, the system tells the WebHost to specialize a worker with a specific `RuntimeEnvironment`. This is opaque to the `WebHost` and it just looks for a match.
- when the match is found, the `GrpcWorkerStream` is signaled to specialize that worker. This allows the stream to re-use the existing grpc connection and existing `BidirectionalStream` with its new `JobHost`.
- the current placeholder `JobHost` is shut down, which drains and tells all existing workers to exit.

### Misc tenets
- `WebHost` should not be aware of the worker process itself, only the `WorkerController` should be aware of it
- Nothing in the host should know about `FUNCTIONS_WORKER_RUNTIME`. This is a worker-specific setting that should only be known by the worker process itself. It doesn't care what the worker's runtime is anymore.
- `JobHost` settings should not change once it is started. If any settings change, a new `JobHost` will be created.
- `JobHost` has no knowledge of placeholders. All it knows is that it's running a function app and communicating with workers via `IWorker`s