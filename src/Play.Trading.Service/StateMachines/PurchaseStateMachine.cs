using System;
using MassTransit;
using Microsoft.Extensions.Logging;
using Play.Identity.Contracts;
using Play.Inventory.Contracts;
using Play.Trading.Service.Activities;
using Play.Trading.Service.Contracts;
using Play.Trading.Service.SignalR;

namespace Play.Trading.Service.StateMachines
{
    public class PurchaseStateMachine : MassTransitStateMachine<PurchaseState>
    {
        private readonly MessageHub hub;
        private readonly ILogger<PurchaseStateMachine> logger;
        public State Accepted { get; }
        public State ItemsGranted { get; }
        public State Completed { get; }
        public State Faulted { get; }

        public Event<PurchaseRequested> PurchaseRequested { get; }
        public Event<GetPurchaseState> GetPurchaseState { get; }
        public Event<InventoryItemsGranted> InventoryItemsGranted { get; }
        public Event<GilDebited> GilDebited {get;}
        public Event<Fault<GrantItems>> GrantIntemsFaulted {get;}
        public Event<Fault<DebitGil>> DebitGilFaulted {get;}

        public PurchaseStateMachine(MessageHub hub, ILogger<PurchaseStateMachine> logger)
        {
            InstanceState(state => state.CurrentState);
            ConfigureEvents();
            ConfigureInitialState();
            ConfigureAny();
            ConfigureAccepted();
            ConfigureItemsGranted();
            ConfigureFaulted();
            ConfigureCompleted();
            this.hub = hub;
            this.logger = logger;
        }

        private void ConfigureEvents()
        {
            Event(() => PurchaseRequested);
            Event(() => GetPurchaseState);
            Event(() => InventoryItemsGranted);
            Event(() => GilDebited);
            Event(() => GrantIntemsFaulted, x => x.CorrelateById(
                context => context.Message.Message.CorrelationId
            ));
            Event(() => DebitGilFaulted, x => x.CorrelateById(
                context => context.Message.Message.CorrelationId
            ));
        }

        private void ConfigureInitialState()
        {
            Initially(
                When(PurchaseRequested)
                    .Then(context =>
                    {
                        context.Saga.UserId = context.Message.UserId;
                        context.Saga.ItemId = context.Message.ItemId;
                        context.Saga.Quantity = context.Message.Quantity;
                        context.Saga.Received = DateTimeOffset.UtcNow;
                        context.Saga.LastUpdated = context.Saga.Received;
                        logger.LogInformation(
                            "Calculating total price for purchase with CorrelationId {CorrelationId}...",
                            context.Saga.CorrelationId
                        );
                    })
                    .Activity(x => x.OfType<CalculatePurchaseTotalActivity>())
                    .Send(context => new GrantItems(
                        context.Saga.UserId,
                        context.Saga.ItemId,
                        context.Saga.Quantity,
                        context.Saga.CorrelationId))
                    .TransitionTo(Accepted)
                    .Catch<Exception>(ex => ex.
                        Then(context =>
                        {
                            context.Saga.ErrorMessage = context.Exception.Message;
                            context.Saga.LastUpdated = DateTimeOffset.UtcNow;
                            logger.LogError(
                                context.Exception,
                                "Could not calculate the total price of purchase with CorrelationId {CorrelationId}. Error: {ErrorMessage}",
                                context.Saga.CorrelationId,
                                context.Saga.ErrorMessage
                            );
                        })
                        .TransitionTo(Faulted)
                        .ThenAsync(async context => await hub.SendStatusAsync(context.Saga)))
            );
        }

        private void ConfigureAccepted()
        {
            During(Accepted,
                Ignore(PurchaseRequested),
                When(InventoryItemsGranted)
                    .Then(context => {
                        context.Saga.LastUpdated = DateTimeOffset.UtcNow;
                        logger.LogInformation(
                            "Items of purchase with CorrelationId {CorrelationId} have been granted to user {UserId}. ",
                            context.Saga.CorrelationId,
                            context.Saga.UserId
                        );
                    })
                    .Send(context => new DebitGil(
                        context.Saga.UserId,
                        context.Saga.PurchaseTotal.Value,
                        context.Saga.CorrelationId
                    ))
                    .TransitionTo(ItemsGranted),
                When(GrantIntemsFaulted)
                    .Then(context => {
                        context.Saga.ErrorMessage = context.Message.Exceptions[0].Message;
                        context.Saga.LastUpdated = DateTimeOffset.UtcNow;
                        logger.LogError(
                            "Could not grant items for purchase with CorrelationId {CorrelationId}. Error: {ErrorMessage}.",
                            context.Saga.CorrelationId,
                            context.Saga.ErrorMessage
                        );
                    })
                    .TransitionTo(Faulted)
                    .ThenAsync(async context => await hub.SendStatusAsync(context.Saga))
                );
        }

        public void ConfigureItemsGranted()
        {
            During(ItemsGranted,
                Ignore(PurchaseRequested),
                Ignore(InventoryItemsGranted),
                When(GilDebited)
                    .Then(context => {
                        context.Saga.LastUpdated = DateTimeOffset.UtcNow;
                        logger.LogInformation(
                            "Items total price of purchase with CorrelationId {CorrelationId} have been debited from user {UserId}. Purchase complete.",
                            context.Saga.CorrelationId,
                            context.Saga.UserId
                        );
                    })
                    .TransitionTo(Completed)
                    .ThenAsync(async context => await hub.SendStatusAsync(context.Saga)),
                When(DebitGilFaulted)
                    .Send(context => new SubtractItems(
                        context.Saga.UserId,
                        context.Saga.ItemId,
                        context.Saga.Quantity,
                        context.Saga.CorrelationId
                    ))
                    .Then(context =>
                    {
                        context.Saga.ErrorMessage = context.Message.Exceptions[0].Message;
                        context.Saga.LastUpdated = DateTimeOffset.UtcNow;
                        logger.LogError(
                            "Could not debit the total price of purchase with CorrelationId {CorrelationId} from user {UserId}. Error: {ErrorMessage}.",
                            context.Saga.CorrelationId,
                            context.Saga.UserId,
                            context.Saga.ErrorMessage
                        );
                    })
                    .TransitionTo(Faulted)
                    .ThenAsync(async context => await hub.SendStatusAsync(context.Saga))
             );
        }

        private void ConfigureCompleted()
        {
            During(Completed,
                Ignore(PurchaseRequested),
                Ignore(InventoryItemsGranted),
                Ignore(GilDebited)
            );
        }

        private void ConfigureAny()
        {
            DuringAny(
                When(GetPurchaseState)
                    .Respond(x => x.Saga)
            );
        }

        private void ConfigureFaulted()
        {
            During(Faulted,
                Ignore(PurchaseRequested),
                Ignore(InventoryItemsGranted),
                Ignore(GilDebited)
            );
        }
    }
}