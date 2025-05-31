//using System.Collections.Concurrent;
//using System.Threading.Channels;
//using OutOfProcModel.Abstractions.Worker;
//using OutOfProcModel.Mock;

//namespace OutOfProcModel.FunctionsHost.Grpc;

//// Something like how our GrpcService in WebHost would work
//public class MockGrpcService
//{
//    private readonly IJobHostManager _jobHostManager;

//    private readonly ConcurrentDictionary<string, HandlerCreationContext> _placeholderContexts = new(StringComparer.OrdinalIgnoreCase);

//    public MockGrpcService(IJobHostManager jobHostManager)
//    {
//        _jobHostManager = jobHostManager ?? throw new ArgumentNullException(nameof(jobHostManager));
//    }

//    // simulate incoming call from a worker
//    // note these channels are meant to be the bidirectional grpc streams coming from worker connection
//    public async Task StartStream(string workerId, string applicationId, string applicationVersion, string[] capabilities,
//        ChannelWriter<MessageToWorker> outgoingStream, ChannelReader<MessageFromWorker> incomingStream)
//    {
//        _ = StartLoopAsync(workerId, applicationId, applicationVersion, capabilities, outgoingStream, incomingStream);
//        outgoingStream.TryWrite(new MessageToWorker("MetadataRequest", applicationId, string.Empty));
//    }

//    private async Task StartLoopAsync(string workerId, string applicationId, string applicationVersion, string[] capabilities,
//      ChannelWriter<MessageToWorker> outgoingStream, ChannelReader<MessageFromWorker> incomingStream)
//    {
//        await Task.Yield(); //free up the caller        

//        // this is a stream, remember? so read and dispatch to our inner channels based on message type        
//        Channel<MessageToWorker> jobHostToWorkerChannel = Channel.CreateUnbounded<MessageToWorker>();
//        Channel<MessageFromWorker> workerToJobHostChannel = Channel.CreateUnbounded<MessageFromWorker>();

//        _ = PushFromOutboundToGrpc(workerId, jobHostToWorkerChannel.Reader, outgoingStream);

//        while (await incomingStream.WaitToReadAsync())
//        {
//            while (incomingStream.TryRead(out var item))
//            {
//                switch (item.MessageType)
//                {
//                    case "MetadataResponse":
//                        _ = HandleMetadataResponseAsync(item, workerId, applicationId, applicationVersion, capabilities, jobHostToWorkerChannel.Writer, workerToJobHostChannel.Reader);
//                        break;
//                    case "InvocationResponse":
//                        workerToJobHostChannel.Writer.TryWrite(item); // push to JobHost via channel
//                        break;
//                    default:
//                        break;
//                }
//            }
//        }
//    }

//    private async Task HandleMetadataResponseAsync(MessageFromWorker item, string workerId, string applicationId, string applicationVersion,
//        string[] capabilities, ChannelWriter<MessageToWorker> writer, ChannelReader<MessageFromWorker> reader)
//    {
//        string[] metadata = ["HttpTrigger1", "TimerTrigger1"]; // would come from payload data

//        var jobHost = await _jobHostManager.GetOrAddJobHostAsync(applicationId, () =>
//        {
//            var context = new JobHostStartContext(applicationId, applicationVersion);
//            context.FunctionMetadata = metadata;
//            return context;
//        });

//        var handlerManager = jobHost.Services.GetRequiredService<IInvocationHandlerManager>();
//        await handlerManager.AddHandlerAsync(new HandlerCreationContext(applicationId, applicationVersion, capabilities)
//        {
//            Properties =
//            {
//                [GrpcInvocationHandlerProvider.WorkerIdKey] = workerId,
//                [GrpcInvocationHandlerProvider.WorkerChannelWriterKey] = writer,
//                [GrpcInvocationHandlerProvider.WorkerChannelReaderKey] = reader
//            }
//        });
//    }

//    // take from JobHost and push to worker
//    private async Task PushFromOutboundToGrpc(string workerId, ChannelReader<MessageToWorker> jobHostChannelReader, ChannelWriter<MessageToWorker> writer)
//    {
//        await Task.Yield();

//        while (await jobHostChannelReader.WaitToReadAsync())
//        {
//            while (jobHostChannelReader.TryRead(out var item))
//            {
//                await writer.WriteAsync(item);
//            }
//        }
//    }

//    internal async Task SpecializeApplicationAsync(string applicationId, string applicationVersion, string[] capabilities)
//    {
//        // Do stuff here like:
//        // - reload worker's environment
//        // - get worker Metadata 
//        string[] functionMetadata = ["HttpTrigger1", "TimerTrigger1"]; // would come from payload data

//        // This may or may not match an existing placeholder. If it does, reuse the channels and workerId
//        var customerAppId = GetMockPlaceholderId();
//        if (_placeholderContexts.TryRemove(customerAppId, out var handlerContext))
//        {
//            Console.WriteLine($"Found matching JobHost with placeholder id: '{customerAppId}'");

//            await _jobHostManager.StopJobHostAsync(handlerContext.ApplicationId);
//            var newHost = await _jobHostManager.GetOrAddJobHostAsync(applicationId, () =>
//            {
//                var context = new JobHostStartContext(applicationId, applicationVersion)
//                {
//                    FunctionMetadata = functionMetadata
//                };
//                return context;
//            });

//            // Reuse the channels and workerId
//            handlerContext.ApplicationId = applicationId;
//            handlerContext.ApplicationVersion = applicationVersion;
//            handlerContext.Capabilities = capabilities;

//            var handlerManager = newHost.Services.GetRequiredService<IInvocationHandlerManager>();
//            await handlerManager.AddHandlerAsync(handlerContext);
//        }
//        else
//        {
//            Console.WriteLine($"No matching placeholder id found for '{customerAppId}' in [{string.Join(", ", _placeholderContexts.Keys)}]. Requesting new worker...");
//            // We need to signal that we could not specialize; need a new worker to start up for us via control plane
//        }

//        // Now remove any other placeholders and stop their loops
//    }

//    private static string GetMockPlaceholderId()
//    {
//        string[] placeholderIds = ["r=node,rv=20.1", "r=python,rv=3.1", "r=dotnet-isolated,rv=8.0"];
//        return placeholderIds[Random.Shared.Next(placeholderIds.Length)];
//    }
//}
