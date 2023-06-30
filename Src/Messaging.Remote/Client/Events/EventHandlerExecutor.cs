﻿using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FastEndpoints;

internal sealed class EventHandlerExecutor<TEvent, TEventHandler> : BaseCommandExecutor<string, TEvent>, ICommandExecutor
    where TEvent : class, IEvent
    where TEventHandler : IEventHandler<TEvent>
{
    private readonly ObjectFactory _handlerFactory;
    private readonly IServiceProvider? _serviceProvider;
    private readonly ILogger<EventHandlerExecutor<TEvent, TEventHandler>>? _logger;
    private readonly string _subscriberID;

    internal EventHandlerExecutor(GrpcChannel channel, IServiceProvider? serviceProvider)
        : base(channel, MethodType.ServerStreaming, $"{typeof(TEvent).FullName}")
    {
        _handlerFactory = ActivatorUtilities.CreateFactory(typeof(TEventHandler), Type.EmptyTypes);
        _serviceProvider = serviceProvider;
        _logger = serviceProvider?.GetRequiredService<ILogger<EventHandlerExecutor<TEvent, TEventHandler>>>();
        _subscriberID = (Environment.MachineName + GetType().FullName + channel.Target).ToHash();
        _logger?.LogInformation("Event subscriber registered! [id: {subid}] ({thandler}<{tevent}>)",
            _subscriberID,
            typeof(TEventHandler).FullName,
            typeof(TEvent).FullName);
    }

    internal void Start(CallOptions opts)
    {
        _ = EventProducer(opts, _invoker, _method, _subscriberID, _logger);
        _ = EventConsumer(opts, _subscriberID, _logger, _handlerFactory, _serviceProvider);
    }

    private static async Task EventProducer(CallOptions opts, CallInvoker invoker, Method<string, TEvent> method, string subscriberID, ILogger? logger)
    {
        var call = invoker.AsyncServerStreamingCall(method, null, opts, subscriberID);

        while (!opts.CancellationToken.IsCancellationRequested)
        {
            try
            {
                while (await call.ResponseStream.MoveNext(opts.CancellationToken)) // actual network call happens on MoveNext()
                {
                    var record = EventSubscriberStorage.RecordFactory();
                    record.SubscriberID = subscriberID;
                    record.Event = call.ResponseStream.Current;
                    record.EventType = typeof(TEvent).FullName!;
                    record.ExpireOn = DateTime.UtcNow.AddHours(4);

                    while (!opts.CancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            await EventSubscriberStorage.Provider.StoreEventAsync(record, opts.CancellationToken);
                            break;
                        }
                        catch (Exception ex)
                        {
                            logger?.LogError("Event storage 'create' error for [subscriber-id:{subid}]({tevent}): {msg}. Retrying in 5 seconds...",
                                subscriberID,
                                typeof(TEvent).FullName,
                                ex.Message);
                            await Task.Delay(5000);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogTrace("Event 'receive' error for [subscriber-id:{subid}]({tevent}): {msg}. Retrying in 5 seconds...",
                    subscriberID,
                    typeof(TEvent),
                    ex.Message);
                call.Dispose(); //the stream is most likely broken, so dispose it and initialize a new call
                await Task.Delay(5000);
                call = invoker.AsyncServerStreamingCall(method, null, opts, subscriberID);
            }
        }
    }

    private static async Task EventConsumer(CallOptions opts, string subscriberID, ILogger? logger, ObjectFactory handlerFactory, IServiceProvider? serviceProvider)
    {
        while (!opts.CancellationToken.IsCancellationRequested)
        {
            IEventStorageRecord? evntRecord;

            try
            {
                evntRecord = await EventSubscriberStorage.Provider.GetNextEventAsync(subscriberID, opts.CancellationToken);
            }
            catch (Exception ex)
            {
                logger?.LogError("Event storage 'retrieval' error for [subscriber-id:{subid}]({tevent}): {msg}. Retrying in 5 seconds...",
                    subscriberID,
                    typeof(TEvent).FullName,
                    ex.Message);
                await Task.Delay(5000);
                continue;
            }

            if (evntRecord is not null)
            {
                var handler = (TEventHandler)handlerFactory(serviceProvider!, null);

                try
                {
                    await handler.HandleAsync((TEvent)evntRecord.Event, opts.CancellationToken);
                }
                catch (Exception ex)
                {
                    logger?.LogCritical("Event [{event}] execution error: [{err}]. Retrying after 5 seconds...",
                        typeof(TEvent).FullName,
                        ex.Message);
                    await Task.Delay(5000);
                    continue;
                }

                while (!opts.CancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await EventSubscriberStorage.Provider.MarkEventAsCompleteAsync(evntRecord, opts.CancellationToken);
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError("Event storage 'update' error for [subscriber-id:{subid}]({tevent}): {msg}. Retrying in 5 seconds...",
                            subscriberID,
                            typeof(TEvent).FullName,
                            ex.Message);
                        await Task.Delay(5000);
                    }
                }
            }
            else
            {
                await Task.Delay(300);
            }
        }
    }
}